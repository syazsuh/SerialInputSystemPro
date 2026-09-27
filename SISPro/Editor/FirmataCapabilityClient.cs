#if UNITY_EDITOR
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO.Ports;
using System.Text;
using System.Threading;

namespace SISPro
{
/// <summary>
/// Minimal Firmata client for discovery only: firmware name, pin capabilities,
/// and analog channel-to-pin mapping. Not a full runtime driver - just enough
/// to populate a SerialDeviceProfile from a board running StandardFirmata
/// (or ConfigurableFirmata), from the Editor, with no Play Mode required.
///
/// The runtime transport that actually streams live input during Play lives in
/// SerialManager (SendFirmataSetupSequence / FirmataReadLoop) - this class is
/// purely a pre-Play discovery tool used by SerialCapabilityExplorerWindow.
/// Both share the same protocol byte constants from FirmataProtocol so they
/// can never drift apart.
///
/// Ownership rule mirrors SerialManager: _port is only ever touched by the
/// worker thread. This class is specifically meant to run outside Play Mode,
/// so it also has to survive assembly reload - callers (SerialCapabilityExplorerWindow)
/// hook AssemblyReloadEvents.beforeAssemblyReload and call Dispose() there,
/// not just from OnDisable/OnDestroy, since a domain reload doesn't run those.
/// </summary>
public struct PinModeCapability
{
    public FirmataPinMode Mode;
    public int ResolutionBits;
}

public class PinCapability
{
    public int PinNumber;
    public List<PinModeCapability> Modes = new List<PinModeCapability>();

    public bool SupportsDigital
    {
        get
        {
            foreach (var m in Modes)
                if (m.Mode == FirmataPinMode.Input || m.Mode == FirmataPinMode.Output) return true;
            return false;
        }
    }

    public bool SupportsAnalog
    {
        get
        {
            foreach (var m in Modes)
                if (m.Mode == FirmataPinMode.Analog) return true;
            return false;
        }
    }

    public int AnalogResolutionBits
    {
        get
        {
            foreach (var m in Modes)
                if (m.Mode == FirmataPinMode.Analog) return m.ResolutionBits;
            return 0;
        }
    }
}

public abstract class FirmataEvent { }
public class FirmataConnectedEvent : FirmataEvent { }
public class FirmataDisconnectedEvent : FirmataEvent { public string Reason; }
public class FirmataFirmwareEvent : FirmataEvent { public string Name; public int Major; public int Minor; }
public class FirmataCapabilitiesEvent : FirmataEvent { public List<PinCapability> Pins; }
public class FirmataAnalogMappingEvent : FirmataEvent { public Dictionary<int, int> ChannelToPin; }

public class FirmataCapabilityClient : IDisposable
{
    // Discovery queries are re-sent if unanswered, because a board still in its
    // bootloader silently drops them. Optiboot (Uno) is ready well within the initial
    // 1.5s wait; the old Nano/Duemilanove bootloader can take ~2s+ after a reset.
    const int ResendAfterMs = 1000;
    const int MaxSendsPerQuery = 5;

    class PendingQuery
    {
        public byte[] Frame;
        public DateTime LastSent;
        public int Sends;
    }

    readonly ConcurrentQueue<FirmataEvent> _events = new ConcurrentQueue<FirmataEvent>();
    readonly ConcurrentQueue<byte[]> _outbox = new ConcurrentQueue<byte[]>();

    CancellationTokenSource _cts;
    Thread _thread;
    SerialPort _port; // owned exclusively by _thread - never touch this elsewhere

    // Worker-thread only: sysex query command byte -> the query awaiting its reply.
    readonly Dictionary<byte, PendingQuery> _pending = new Dictionary<byte, PendingQuery>();

    public bool IsRunning => _thread != null && _thread.IsAlive;

    public void Connect(string portName, int baudRate)
    {
        Disconnect(); // clean slate - never start a second worker while one exists
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        _thread = new Thread(() => Run(portName, baudRate, token)) { IsBackground = true };
        _thread.Start();
    }

    public void RequestFirmware() => _outbox.Enqueue(new byte[] { FirmataProtocol.START_SYSEX, FirmataProtocol.REPORT_FIRMWARE, FirmataProtocol.END_SYSEX });
    public void RequestCapabilities() => _outbox.Enqueue(new byte[] { FirmataProtocol.START_SYSEX, FirmataProtocol.CAPABILITY_QUERY, FirmataProtocol.END_SYSEX });
    public void RequestAnalogMapping() => _outbox.Enqueue(new byte[] { FirmataProtocol.START_SYSEX, FirmataProtocol.ANALOG_MAPPING_QUERY, FirmataProtocol.END_SYSEX });

    public bool TryDequeueEvent(out FirmataEvent evt) => _events.TryDequeue(out evt);

    void Run(string portName, int baudRate, CancellationToken token)
    {
        _pending.Clear();

        try
        {
            // DtrEnable resets AVR boards on open, so every discovery starts from a
            // freshly booted board (SerialManager also asserts DTR by default).
            _port = new SerialPort(portName, baudRate) { ReadTimeout = 200, WriteTimeout = 500, DtrEnable = true };
            _port.Open();
            Thread.Sleep(1500); // let the bootloader hand over to the sketch
            _events.Enqueue(new FirmataConnectedEvent());
        }
        catch (Exception ex)
        {
            try { _port?.Dispose(); } catch { }
            _port = null;
            _events.Enqueue(new FirmataDisconnectedEvent { Reason = ex.Message });
            return;
        }

        var sysexBuffer = new List<byte>();
        bool inSysex = false;
        string closeReason = "closed";

        while (!token.IsCancellationRequested)
        {
            bool writeFailed = false;
            byte[] msg;
            while (_outbox.TryDequeue(out msg))
            {
                try
                {
                    _port.Write(msg, 0, msg.Length);
                    TrackQuery(msg);
                }
                catch (Exception ex) { closeReason = ex.Message; writeFailed = true; break; }
            }
            if (writeFailed) break;

            try { ResendUnansweredQueries(); }
            catch (Exception ex) { closeReason = ex.Message; break; }

            int b;
            try { b = _port.ReadByte(); }
            catch (TimeoutException) { continue; }
            catch (Exception ex) { closeReason = ex.Message; break; }

            if (b < 0) continue;
            byte value = (byte)b;

            if (!inSysex)
            {
                if (value == FirmataProtocol.START_SYSEX) { inSysex = true; sysexBuffer.Clear(); }
                // any other stray non-sysex byte is ignored - this client only cares about discovery messages
            }
            else if (value == FirmataProtocol.END_SYSEX)
            {
                HandleSysex(sysexBuffer);
                inSysex = false;
            }
            else
            {
                sysexBuffer.Add(value);
            }
        }

        try { if (_port != null && _port.IsOpen) _port.Close(); } catch { /* best effort */ }
        try { _port?.Dispose(); } catch { }
        _port = null;
        _events.Enqueue(new FirmataDisconnectedEvent { Reason = closeReason });
    }

    /// <summary>Remembers a just-sent sysex query so it can be re-sent if no reply comes.</summary>
    void TrackQuery(byte[] frame)
    {
        if (frame.Length < 3 || frame[0] != FirmataProtocol.START_SYSEX) return;
        byte cmd = frame[1];

        if (!_pending.TryGetValue(cmd, out var q))
            _pending[cmd] = q = new PendingQuery { Frame = frame };
        q.LastSent = DateTime.UtcNow;
        q.Sends++;
    }

    void ResendUnansweredQueries()
    {
        if (_pending.Count == 0) return;
        var now = DateTime.UtcNow;

        List<byte> giveUp = null;
        foreach (var kvp in _pending)
        {
            var q = kvp.Value;
            if ((now - q.LastSent).TotalMilliseconds < ResendAfterMs) continue;

            if (q.Sends >= MaxSendsPerQuery)
            {
                (giveUp ??= new List<byte>()).Add(kvp.Key); // not Firmata, or not answering - the caller's timeout handles it
                continue;
            }

            _port.Write(q.Frame, 0, q.Frame.Length);
            q.LastSent = now;
            q.Sends++;
        }

        if (giveUp != null)
            foreach (var cmd in giveUp) _pending.Remove(cmd);
    }

    void HandleSysex(List<byte> data)
    {
        if (data.Count == 0) return;
        byte cmd = data[0];

        if (cmd == FirmataProtocol.REPORT_FIRMWARE && data.Count >= 3)
        {
            _pending.Remove(FirmataProtocol.REPORT_FIRMWARE);

            int major = data[1], minor = data[2];
            var sb = new StringBuilder();
            for (int i = 3; i + 1 < data.Count; i += 2)
                sb.Append((char)(data[i] | (data[i + 1] << 7))); // Firmata packs each char as two 7-bit bytes
            _events.Enqueue(new FirmataFirmwareEvent { Name = sb.ToString(), Major = major, Minor = minor });
        }
        else if (cmd == FirmataProtocol.CAPABILITY_RESPONSE)
        {
            _pending.Remove(FirmataProtocol.CAPABILITY_QUERY);

            var pins = new List<PinCapability>();
            int pinIndex = 0;
            var current = new PinCapability { PinNumber = 0 };
            int i = 1;
            while (i < data.Count)
            {
                byte b = data[i++];
                if (b == 0x7F) // per-pin terminator
                {
                    pins.Add(current);
                    pinIndex++;
                    current = new PinCapability { PinNumber = pinIndex };
                    continue;
                }
                if (i >= data.Count) break; // malformed tail, bail rather than throw
                byte resolution = data[i++];
                current.Modes.Add(new PinModeCapability { Mode = (FirmataPinMode)b, ResolutionBits = resolution });
            }
            _events.Enqueue(new FirmataCapabilitiesEvent { Pins = pins });
        }
        else if (cmd == FirmataProtocol.ANALOG_MAPPING_RESPONSE)
        {
            _pending.Remove(FirmataProtocol.ANALOG_MAPPING_QUERY);

            // Firmata spec: the response has ONE BYTE PER PIN, in pin order (pin 0, pin 1, ...).
            // Each byte is that pin's analog channel number, or 0x7F (127) if the pin
            // isn't analog-capable. On an Uno: pins 0-13 -> 127, pin 14 -> 0 (A0), ... pin 19 -> 5 (A5).
            //
            // (This used to be read the other way round - index as channel, value as pin -
            // which still produced 6 entries on an Uno, but keyed wrong, so every Analog In
            // pin was silently skipped when applying to a profile.)
            var map = new Dictionary<int, int>();
            for (int pin = 0, i = 1; i < data.Count; i++, pin++)
            {
                byte channel = data[i];
                if (channel != 0x7F) map[channel] = pin;
            }
            _events.Enqueue(new FirmataAnalogMappingEvent { ChannelToPin = map });
        }
    }

    public void Disconnect()
    {
        if (_cts != null) _cts.Cancel();
        if (_thread != null && _thread.IsAlive)
            _thread.Join(1000); // bounded - if it misses this, IsBackground still guarantees no hang on exit
        if (_cts != null) _cts.Dispose();
        _cts = null;
        _thread = null;
    }

    public void Dispose() => Disconnect();
}
}
#endif