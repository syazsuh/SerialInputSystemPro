#if UNITY_STANDALONE_WIN || UNITY_STANDALONE_OSX || UNITY_STANDALONE_LINUX || UNITY_EDITOR
using System;
using System.IO.Ports;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using UnityEngine;
using UnityEngine.Events;
using System.IO;

namespace SISPro
{
[Serializable]
public class StringUnityEvent : UnityEvent<string> { }

public struct SerialLine
{
    public readonly string Text;
    public readonly DateTime Timestamp;

    public SerialLine(string text)
    {
        Text = text;
        Timestamp = DateTime.Now;
    }

    public override string ToString() => $"[{Timestamp:HH:mm:ss.fff}] {Text}";
}

// No [RequireComponent(typeof(SerialToInputSystemAdapter))] here: the adapter already
// requires SerialManager, and requiring each other made neither component removable
// in the Inspector. An output-only rig doesn't need the adapter at all.
public class SerialManager : MonoBehaviour
{
    [Header("SerialDeviceProfile")]
    public SerialDeviceProfile deviceProfile;

    [Header("Manual Connection")]
    [Tooltip("Leave blank to auto-select the most likely Arduino port (USB serial ports first, " +
             "COM1 and Bluetooth ports last). The pick is re-made on every connect, so a board " +
             "that comes back on a different port is still found.")]
    public string portName = "";

    [Tooltip("Ignored if device profile is assigned")]
    public int baudRate = 115200;

    [Tooltip("Assert DTR when opening the port. Boards with native USB (Leonardo, Micro, many " +
             "ESP32-S2/S3 and RP2040 boards) may not send anything without it. On Uno/Nano/Mega it " +
             "also resets the board on open - the Firmata handshake waits for the board to boot, so " +
             "that's harmless.")]
    public bool dtrEnable = true;

    [Header("Behavior")]
    public bool autoConnectOnStart = true;
    public bool autoReconnect = false;
    public float reconnectInterval = 5f;

    [Header("Debugging")]
    public bool logIncomingLines = false;
    public bool warnOnGibberish = true;

    [Header("Events")]
    public UnityEvent OnSerialConnected;
    public UnityEvent OnSerialDisconnected;
    public StringUnityEvent OnLineReceived;

    // Firmata boot handshake. Opening the port resets most AVR boards (always on
    // macOS/Linux; on Windows depending on driver/DTR), and the bootloader plus
    // StandardFirmata's startup takes well over a second. Anything sent before the
    // sketch is running is silently dropped - which used to mean "connected, but no
    // input" on some machines. So the Firmata thread waits until the board
    // identifies itself (StandardFirmata sends its firmware report on boot, and
    // answers a firmware query any time) before sending pin setup.
    const double FirmataFirstQuerySeconds = 1.5;  // don't talk to the bootloader
    const double FirmataQueryIntervalSeconds = 1.0;
    const double FirmataHandshakeTimeoutSeconds = 5.0;

    private SerialPort _serialPort;
    private string _activePortName; // port actually opened (differs from portName when auto-selected)
    private Thread _readThread;
    private volatile bool _keepReading;
    private volatile bool _firmataReady;
    private bool _runningFirmata; // transport actually in use for the current connection
    private float _lastRetryTime = -10f;

    // Populated at connect time (main thread, before the read thread starts) when
    // running the Firmata transport. The read thread only ever reads these - it
    // never touches deviceProfile, which is a UnityEngine.Object.
    // All are already filtered to exclude pins with a role conflict or an
    // out-of-range pin number - see PrepareFirmataLookups.
    private readonly HashSet<int> _firmataDigitalPins = new HashSet<int>();
    private readonly HashSet<int> _firmataAnalogChannels = new HashSet<int>();
    private readonly List<int> _firmataDigitalOutputs = new List<int>();
    private readonly List<int> _firmataPwmOutputs = new List<int>();
    private readonly List<int> _firmataServoOutputs = new List<int>();

    // Pins with more than one role assigned on the profile (e.g. listed in both
    // digitalPins and pwmOutputPins). These are deliberately left unconfigured -
    // no SET_PIN_MODE is ever sent for them - rather than guessing which role wins,
    // since guessing wrong on a physical rig could mean driving voltage onto a pin
    // wired to a switch. See SerialDeviceProfile.ValidatePinAssignments.
    private readonly HashSet<int> _firmataConflictingPins = new HashSet<int>();

    // Tracks the last-written bitmask per digital output port, since a Firmata digital
    // write addresses a whole 8-pin port at once - changing one output pin means
    // re-sending the port's full bitmask with just that bit flipped.
    private readonly Dictionary<int, int> _firmataOutputPortBitmask = new Dictionary<int, int>();

    private readonly ConcurrentQueue<SerialLine> _lineQueue = new();
    private readonly List<SerialLine> _recentLines = new();

    public IReadOnlyList<SerialLine> RecentLines => _recentLines.AsReadOnly();

    /// <summary>
    /// Increments whenever RecentLines changes (line added or cleared), so a UI can
    /// skip rebuilding its log on frames where nothing arrived.
    /// </summary>
    public int LinesVersion { get; private set; }

    /// <summary>True when the serial port is open.</summary>
    public bool serial_connected { get; private set; }

    /// <summary>
    /// The port currently (or most recently) opened. Equal to portName when one is
    /// set; the auto-selected port when portName is blank. Null if no port has been
    /// opened yet (or the last attempt failed).
    /// </summary>
    public string ActivePortName => _activePortName;

    /// <summary>
    /// Firmata only: true once the board has answered and pin setup has been sent.
    /// Output writes (SetDigitalOutput etc.) are ignored until then.
    /// </summary>
    public bool IsFirmataReady => _firmataReady;

    void OnEnable()
    {
        SerialHub.Register(this);
    }

    void Start()
    {
        if (deviceProfile != null)
        {
            baudRate = deviceProfile.baudRate;
        }

        if (autoConnectOnStart)
            StartSerial();
    }

    void Update()
    {
        // The read thread exits on its own when the port dies (cable pulled, board
        // reset into a different sketch, driver error). Nothing else notices, so
        // without this serial_connected stayed true forever: the UI kept showing
        // CONNECTED and autoReconnect never fired.
        if (serial_connected && _readThread != null && !_readThread.IsAlive)
        {
            Debug.LogWarning($"⚡ Lost connection on {_activePortName}." +
                             (autoReconnect ? $" Retrying in {reconnectInterval:0.#}s." : ""));
            StopSerial();
            _lastRetryTime = Time.time;
        }

        if (autoReconnect && !serial_connected && Time.time - _lastRetryTime > reconnectInterval)
        {
            Debug.Log("🔄 Attempting auto-reconnect...");
            StartSerial();
            _lastRetryTime = Time.time;
        }

        while (_lineQueue.TryDequeue(out var line))
        {
            if (warnOnGibberish && IsGibberish(line.Text))
            {
                Debug.LogWarning($"⚠️ Gibberish: {line.Text}");
                continue;
            }

            if (logIncomingLines)
                Debug.Log(line);

            lock (_recentLines)
            {
                _recentLines.Add(line);
                if (_recentLines.Count > 500)
                    _recentLines.RemoveAt(0);
            }
            LinesVersion++;

            OnLineReceived?.Invoke(line.Text);
        }
    }

    public bool IsOpen => _serialPort != null && _serialPort.IsOpen;

    bool UseFirmata => deviceProfile != null && deviceProfile.transport == SerialTransport.Firmata;

    public void StartSerial()
    {
        StopSerial();

        try
        {
            if (deviceProfile != null)
            {
                baudRate = deviceProfile.baudRate;
            }

            // Resolve the port for this connection only - portName stays blank when
            // auto-selecting, so the next reconnect picks again instead of being stuck
            // on whatever port was first in the list.
            string port = portName;
            if (string.IsNullOrWhiteSpace(port))
            {
                var ranked = SerialPortRanking.Rank(SerialPort.GetPortNames());
                if (ranked.Count == 0)
                {
                    Debug.LogWarning("❌ No serial ports found.");
                    return;
                }

                port = ranked[0];
                Debug.Log($"📍 Auto-selected port: {port}" +
                          (ranked.Count > 1
                              ? $" (also found: {string.Join(", ", ranked.GetRange(1, ranked.Count - 1))} - set Port Name to use one of those)"
                              : ""));
            }

            _activePortName = port;

            _serialPort = new SerialPort(port, baudRate)
            {
                ReadTimeout = 500,
                WriteTimeout = 500, // never let a stalled device freeze the main thread on a write
                NewLine = "\n",
                ReadBufferSize = 4096,
                WriteBufferSize = 2048,
                DtrEnable = dtrEnable
            };

            _serialPort.Open();

            _runningFirmata = UseFirmata;
            _firmataReady = false;

            if (_runningFirmata)
            {
                if (!deviceProfile.HasDiscoveredPins && !deviceProfile.HasOutputPins)
                {
                    Debug.LogWarning("⚠️ Firmata transport selected but the profile has no discovered inputs " +
                                     "or configured outputs. Run the Serial Capability Explorer first " +
                                     "(Tools/SISPro/Set Up Arduino), or add output pins to the profile.");
                }

                // No fixed sleep here - the read thread waits for the board to
                // identify itself before sending setup (see FirmataThreadMain), so
                // the main thread isn't blocked while the board boots.
                PrepareFirmataLookups();
            }
            else
            {
                Thread.Sleep(500); // Wait for Arduino to boot
            }

            _keepReading = true;
            _readThread = new Thread(_runningFirmata ? (ThreadStart)FirmataThreadMain : ReadLoop)
            {
                IsBackground = true // never keep the Editor/player alive on exit
            };
            _readThread.Start();

            Debug.Log($"📡 Serial opened on {port} @ {baudRate}bps" +
                      (_runningFirmata ? " (Firmata - waiting for board...)" : " (custom text)"));
            serial_connected = true;
            OnSerialConnected?.Invoke();
        }
        catch (Exception ex)
        {
            Debug.LogError($"❌ Failed to open serial{(_activePortName != null ? " on " + _activePortName : "")}: {ex.Message}");
            try { _serialPort?.Dispose(); } catch { }
            _serialPort = null;
            _activePortName = null;
        }
    }

    public void StopSerial()
    {
        _keepReading = false;

        bool threadExited = true;
        try
        {
            if (_readThread != null)
            {
                _readThread.Join(1000);
                threadExited = !_readThread.IsAlive;
            }
        }
        catch { }

        // Read after the join, so a thread that finished setup just before we
        // stopped it still counts as "setup was sent".
        bool setupWasSent = _firmataReady;
        _firmataReady = false;

        try
        {
            // Only safe to write here because the read thread has demonstrably exited
            // (threadExited) - writing while it might still be blocked in ReadByte()
            // would violate the single-owner-thread rule that keeps the port from
            // wedging on close (see the port-busy-after-disconnect fix elsewhere).
            // Skipped entirely if setup never reached the board: writing to pins that
            // were never configured as outputs is meaningless at best.
            if (threadExited && setupWasSent && _runningFirmata && _serialPort != null && _serialPort.IsOpen)
            {
                // Stop the board from continuing to stream data nobody's listening to.
                foreach (var port in DistinctFirmataInputPorts())
                    if (port >= 0 && port <= 15)
                        _serialPort.Write(new byte[] { (byte)(FirmataProtocol.REPORT_DIGITAL_PORT | port), 0 }, 0, 2);

                foreach (var channel in _firmataAnalogChannels)
                    if (channel >= 0 && channel <= 15)
                        _serialPort.Write(new byte[] { (byte)(FirmataProtocol.REPORT_ANALOG_PIN | channel), 0 }, 0, 2);

                // Safety: return outputs to a known-safe state before releasing the port,
                // so a relay/LED/motor driver doesn't stay energized after Unity
                // disconnects. Servo angle is deliberately left alone - snapping an
                // unknown servo to 0deg could slam a physical linkage, which is worse
                // than leaving it wherever it last was commanded. Conflicting pins were
                // never configured, so they aren't in these lists.
                foreach (var pin in _firmataDigitalOutputs)
                    WriteDigitalOutputRaw(pin, false);
                foreach (var pin in _firmataPwmOutputs)
                    WriteAnalogOutputRaw(pin, 0);
            }
        }
        catch { /* best effort only - we're closing the port either way (it may already be gone) */ }

        try
        {
            if (_serialPort?.IsOpen == true)
                _serialPort.Close();
            _serialPort?.Dispose();
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"⚠️ Serial close failed: {ex.Message}");
        }

        _serialPort = null;
        _readThread = null;
        _runningFirmata = false;

        if (serial_connected)
        {
            serial_connected = false;
            OnSerialDisconnected?.Invoke();
        }
    }

    // ---------------- Custom text transport (unchanged) ----------------

    private void ReadLoop()
    {
        while (_keepReading && _serialPort != null && _serialPort.IsOpen)
        {
            try
            {
                string line = _serialPort.ReadLine();
                if (!string.IsNullOrWhiteSpace(line))
                    _lineQueue.Enqueue(new SerialLine(line.Trim()));
            }
            catch (TimeoutException) { }
            catch (IOException ex)
            {
                Debug.LogWarning($"⚡ Serial IO error: {ex.Message}");
                break;
            }
            catch (Exception ex)
            {
                Debug.LogError($"🔥 Serial thread crash: {ex}");
                break;
            }
        }

        _keepReading = false;
    }

    // ---------------- Firmata transport ----------------
    //
    // Translates Firmata's binary digital/analog INPUT messages into the exact same
    // "D<pin>:<0|1>;A<channel>:<raw>" text convention the custom protocol uses, and
    // enqueues them onto the same _lineQueue. Everything downstream - Update()'s
    // dequeue loop, OnLineReceived, SerialToInputSystemAdapter's TryGetChildControl
    // dispatch, the analogMaxValue normalization - is completely unaware which
    // transport produced the line.
    //
    // OUTPUT (digital/PWM/servo writes) goes the other way: SetDigitalOutput /
    // SetPwmOutput / SetServoOutput below write directly to _serialPort from whatever
    // thread calls them (same concurrency model SendLine already used - concurrent
    // read-thread + caller-thread write is a standard, safe serial-port usage
    // pattern; it's Close()-racing-a-blocked-Read() that's unsafe, not ordinary
    // duplex read+write). They're gated on _firmataReady so they can't interleave
    // with the setup sequence the read thread sends.

    /// <summary>
    /// Validates the profile's pin role assignments and populates the filtered
    /// lookups. Any pin found in more than one role (see
    /// SerialDeviceProfile.ValidatePinAssignments) is recorded in
    /// _firmataConflictingPins and excluded from every lookup here - it will not
    /// be configured as an input OR an output. This is the last line of defense
    /// before hardware actually gets touched; SerialDeviceProfile.OnValidate warns
    /// about the same conflicts earlier, at edit time, but a profile could still
    /// reach here unvalidated (e.g. modified outside the Editor, or an old asset
    /// that predates that check).
    /// </summary>
    void PrepareFirmataLookups()
    {
        _firmataDigitalPins.Clear();
        _firmataAnalogChannels.Clear();
        _firmataDigitalOutputs.Clear();
        _firmataPwmOutputs.Clear();
        _firmataServoOutputs.Clear();
        _firmataOutputPortBitmask.Clear();
        _firmataConflictingPins.Clear();
        if (deviceProfile == null) return;

        if (!deviceProfile.ValidatePinAssignments(out var conflicts, out var conflictingPins))
        {
            foreach (var c in conflicts)
                Debug.LogError($"[SISPro] {c} This pin will be left unconfigured (board default state) " +
                                "until the profile is fixed.");
            _firmataConflictingPins.UnionWith(conflictingPins);
        }

        foreach (var pin in deviceProfile.digitalPins)
            if (!_firmataConflictingPins.Contains(pin) && IsValidPin(pin, "digital input"))
                _firmataDigitalPins.Add(pin);

        // analogChannelPins is parallel to analogChannels - only add a channel if we
        // can confirm its underlying pin isn't also claimed elsewhere. An older profile
        // without analogChannelPins populated can't be checked this way; it's let
        // through as before rather than losing analog input entirely for it.
        for (int i = 0; i < deviceProfile.analogChannels.Count; i++)
        {
            int channel = deviceProfile.analogChannels[i];
            bool hasPinMapping = i < deviceProfile.analogChannelPins.Count;
            int mappedPin = hasPinMapping ? deviceProfile.analogChannelPins[i] : -1;

            if (hasPinMapping && _firmataConflictingPins.Contains(mappedPin))
                continue;

            if (channel < 0 || channel > 15)
            {
                Debug.LogWarning($"⚠️ Analog channel {channel} is outside Firmata's 0-15 range for REPORT_ANALOG_PIN - skipped.");
                continue;
            }

            _firmataAnalogChannels.Add(channel);
        }

        FilterOutputs(deviceProfile.digitalOutputPins, _firmataDigitalOutputs, "digital output");
        FilterOutputs(deviceProfile.pwmOutputPins, _firmataPwmOutputs, "PWM output");
        FilterOutputs(deviceProfile.servoOutputPins, _firmataServoOutputs, "servo output");
    }

    void FilterOutputs(List<int> declared, List<int> configured, string role)
    {
        foreach (var pin in declared)
            if (!_firmataConflictingPins.Contains(pin) && IsValidPin(pin, role) && !configured.Contains(pin))
                configured.Add(pin);
    }

    IEnumerable<int> DistinctFirmataInputPorts()
    {
        var ports = new HashSet<int>();
        foreach (var pin in _firmataDigitalPins) ports.Add(pin / 8);
        return ports;
    }

    /// <summary>
    /// Read-thread entry point for the Firmata transport: handshake, setup, then the
    /// read loop. Everything here runs on _readThread, which owns _serialPort for
    /// reading; setup writes happen before _firmataReady is set, so no caller-thread
    /// output write can interleave with them.
    /// </summary>
    void FirmataThreadMain()
    {
        bool answered = WaitForFirmataHandshake(out bool portFailed);

        if (portFailed || !_keepReading)
        {
            _keepReading = false; // lets Update() notice the dead thread
            return;
        }

        if (answered)
        {
            Debug.Log("🤝 Firmata board answered - sending pin setup.");
        }
        else
        {
            Debug.LogWarning($"⚠️ No Firmata reply on {_activePortName} within {FirmataHandshakeTimeoutSeconds:0}s. " +
                             "Is StandardFirmata uploaded, and is the baud rate 57600? Sending pin setup anyway.");
        }

        SendFirmataSetupSequence();
        _firmataReady = true;

        FirmataReadLoop();
    }

    /// <summary>
    /// Waits for a REPORT_FIRMWARE sysex from the board - either the one
    /// StandardFirmata sends unprompted when it finishes booting, or the reply to a
    /// query. Queries start after FirmataFirstQuerySeconds (so we don't send bytes
    /// into the bootloader) and repeat every FirmataQueryIntervalSeconds, in case
    /// the board didn't reset on open and has no boot report to send.
    /// Anything else received meanwhile (leftover reports from a previous session)
    /// is discarded.
    /// </summary>
    bool WaitForFirmataHandshake(out bool portFailed)
    {
        portFailed = false;
        var start = DateTime.UtcNow;
        var deadline = start.AddSeconds(FirmataHandshakeTimeoutSeconds);
        var nextQuery = start.AddSeconds(FirmataFirstQuerySeconds);

        bool inSysex = false;
        bool haveCommandByte = false;
        bool isFirmwareReport = false;

        while (_keepReading && DateTime.UtcNow < deadline)
        {
            try
            {
                if (DateTime.UtcNow >= nextQuery)
                {
                    _serialPort.Write(new byte[]
                    {
                        FirmataProtocol.START_SYSEX, FirmataProtocol.REPORT_FIRMWARE, FirmataProtocol.END_SYSEX
                    }, 0, 3);
                    nextQuery = DateTime.UtcNow.AddSeconds(FirmataQueryIntervalSeconds);
                }

                int b = _serialPort.ReadByte();
                if (b < 0) continue;

                if (!inSysex)
                {
                    if (b == FirmataProtocol.START_SYSEX)
                    {
                        inSysex = true;
                        haveCommandByte = false;
                        isFirmwareReport = false;
                    }
                    continue;
                }

                if (b == FirmataProtocol.END_SYSEX)
                {
                    inSysex = false;
                    if (isFirmwareReport) return true;
                    continue;
                }

                if (!haveCommandByte)
                {
                    haveCommandByte = true;
                    isFirmwareReport = b == FirmataProtocol.REPORT_FIRMWARE;
                }
            }
            catch (TimeoutException) { }
            catch (Exception ex)
            {
                Debug.LogWarning($"⚡ Serial error while waiting for the Firmata board: {ex.Message}");
                portFailed = true;
                return false;
            }
        }

        return false;
    }

    /// <summary>
    /// Resets the board to a known state, configures every discovered/declared pin's
    /// mode (input-pullup for sensing, output/pwm/servo for the output lists), and
    /// enables reporting for exactly the input ports/channels this profile cares
    /// about. Runs on the read thread after the handshake, before _firmataReady is
    /// set. Uses only the pre-filtered lookups - never deviceProfile.
    /// </summary>
    void SendFirmataSetupSequence()
    {
        try
        {
            _serialPort.Write(new byte[] { FirmataProtocol.SYSTEM_RESET }, 0, 1);

            foreach (var pin in _firmataDigitalPins)
                _serialPort.Write(new byte[] { FirmataProtocol.SET_PIN_MODE, (byte)pin, (byte)FirmataPinMode.Pullup }, 0, 3);

            foreach (var port in DistinctFirmataInputPorts())
            {
                if (port < 0 || port > 15)
                {
                    Debug.LogWarning($"⚠️ Digital port {port} is outside Firmata's 0-15 range for REPORT_DIGITAL_PORT - skipped.");
                    continue;
                }
                _serialPort.Write(new byte[] { (byte)(FirmataProtocol.REPORT_DIGITAL_PORT | port), 1 }, 0, 2);
            }

            foreach (var channel in _firmataAnalogChannels)
                _serialPort.Write(new byte[] { (byte)(FirmataProtocol.REPORT_ANALOG_PIN | channel), 1 }, 0, 2);

            foreach (var pin in _firmataDigitalOutputs)
                _serialPort.Write(new byte[] { FirmataProtocol.SET_PIN_MODE, (byte)pin, (byte)FirmataPinMode.Output }, 0, 3);

            foreach (var pin in _firmataPwmOutputs)
                _serialPort.Write(new byte[] { FirmataProtocol.SET_PIN_MODE, (byte)pin, (byte)FirmataPinMode.Pwm }, 0, 3);

            foreach (var pin in _firmataServoOutputs)
                _serialPort.Write(new byte[] { FirmataProtocol.SET_PIN_MODE, (byte)pin, (byte)FirmataPinMode.Servo }, 0, 3);

            Debug.Log($"🔧 Firmata setup sent: {_firmataDigitalPins.Count} digital inputs, " +
                      $"{_firmataAnalogChannels.Count} analog inputs, " +
                      $"{_firmataDigitalOutputs.Count} digital outputs, " +
                      $"{_firmataPwmOutputs.Count} PWM outputs, " +
                      $"{_firmataServoOutputs.Count} servo outputs" +
                      (_firmataConflictingPins.Count > 0
                          ? $" ({_firmataConflictingPins.Count} pins skipped due to role conflicts - see errors above)."
                          : "."));
        }
        catch (Exception ex)
        {
            Debug.LogError($"❌ Firmata setup failed: {ex.Message}");
        }
    }

    bool IsValidPin(int pin, string role)
    {
        if (pin < 0 || pin > 127)
        {
            Debug.LogWarning($"⚠️ {role} pin {pin} is outside the representable range - skipped.");
            return false;
        }
        return true;
    }

    void FirmataReadLoop()
    {
        bool inSysex = false;

        while (_keepReading && _serialPort != null && _serialPort.IsOpen)
        {
            int b0;
            try { b0 = _serialPort.ReadByte(); }
            catch (TimeoutException) { continue; }
            catch (IOException ex)
            {
                Debug.LogWarning($"⚡ Serial IO error (Firmata): {ex.Message}");
                break;
            }
            catch (Exception ex)
            {
                Debug.LogError($"🔥 Firmata read thread crash: {ex}");
                break;
            }

            if (b0 < 0) continue;
            byte cmd = (byte)b0;

            if (inSysex)
            {
                if (cmd == FirmataProtocol.END_SYSEX) inSysex = false;
                // else: still inside a sysex frame we don't need at runtime (e.g. an
                // unsolicited firmware report) - drop the byte and keep scanning.
                continue;
            }

            if (cmd == FirmataProtocol.START_SYSEX)
            {
                inSysex = true;
                continue;
            }

            byte upperNibbleCmd = (byte)(cmd & 0xF0);

            if (upperNibbleCmd == FirmataProtocol.DIGITAL_MESSAGE)
            {
                int port = cmd & 0x0F;
                if (!TryReadTwo7BitBytes(out byte lsb, out byte msb)) continue;
                int bitmask = lsb | (msb << 7);

                var sb = new StringBuilder();
                for (int bit = 0; bit < 8; bit++)
                {
                    int pin = port * 8 + bit;
                    if (!_firmataDigitalPins.Contains(pin)) continue;

                    int state = (bitmask >> bit) & 0x1;
                    if (sb.Length > 0) sb.Append(';');
                    sb.Append('D').Append(pin).Append(':').Append(state);
                }

                if (sb.Length > 0)
                    _lineQueue.Enqueue(new SerialLine(sb.ToString()));
            }
            else if (upperNibbleCmd == FirmataProtocol.ANALOG_MESSAGE)
            {
                int channel = cmd & 0x0F;
                if (!TryReadTwo7BitBytes(out byte lsb, out byte msb)) continue;
                int value = lsb | (msb << 7);

                if (_firmataAnalogChannels.Contains(channel))
                    _lineQueue.Enqueue(new SerialLine($"A{channel}:{value}"));
            }
            // else: some other Firmata command - not relevant at runtime, ignored.
        }

        _keepReading = false;
    }

    /// <summary>
    /// Reads the two 7-bit data bytes that follow every digital/analog message's
    /// command byte. If the port stalls mid-message (ReadTimeout elapses between
    /// the two bytes), the partial message is dropped rather than corrupting the
    /// next command's framing - Firmata resyncs on the next status byte (>=0x80)
    /// on its own, so losing one update is harmless; the board will send another.
    /// </summary>
    bool TryReadTwo7BitBytes(out byte lsb, out byte msb)
    {
        lsb = 0; msb = 0;
        try
        {
            int a = _serialPort.ReadByte();
            int b = _serialPort.ReadByte();
            if (a < 0 || b < 0) return false;
            lsb = (byte)a;
            msb = (byte)b;
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
    }

    // ---------------- Firmata output API ----------------
    //
    // Only meaningful on the Firmata transport - the custom text protocol has no
    // standardized "write" convention, so these are no-ops (with a warning) there.

    /// <summary>Sets a digital output pin (relay, LED, solenoid) high or low.</summary>
    public void SetDigitalOutput(int pin, bool state)
    {
        if (!CanWriteOutput(pin, deviceProfile?.digitalOutputPins, _firmataDigitalOutputs, "digitalOutputPins")) return;
        try { WriteDigitalOutputRaw(pin, state); }
        catch (Exception ex) { Debug.LogError($"❌ Digital output write failed: {ex.Message}"); }
    }

    /// <summary>Sets a PWM output pin (motor driver, dimmable LED) to a 0-255 duty value.</summary>
    public void SetPwmOutput(int pin, int value)
    {
        if (!CanWriteOutput(pin, deviceProfile?.pwmOutputPins, _firmataPwmOutputs, "pwmOutputPins")) return;
        try { WriteAnalogOutputRaw(pin, Mathf.Clamp(value, 0, 255)); }
        catch (Exception ex) { Debug.LogError($"❌ PWM output write failed: {ex.Message}"); }
    }

    /// <summary>Sets a servo output pin to a 0-180 degree angle.</summary>
    public void SetServoOutput(int pin, int angleDegrees)
    {
        if (!CanWriteOutput(pin, deviceProfile?.servoOutputPins, _firmataServoOutputs, "servoOutputPins")) return;
        try { WriteAnalogOutputRaw(pin, Mathf.Clamp(angleDegrees, 0, 180)); }
        catch (Exception ex) { Debug.LogError($"❌ Servo output write failed: {ex.Message}"); }
    }

    bool CanWriteOutput(int pin, List<int> declared, List<int> configured, string listName)
    {
        if (!_runningFirmata && !UseFirmata)
        {
            Debug.LogWarning("⚠️ Output control is only supported on the Firmata transport.");
            return false;
        }
        if (_serialPort == null || !_serialPort.IsOpen)
        {
            Debug.LogWarning("📭 Cannot write output - port not open.");
            return false;
        }
        if (!_firmataReady)
        {
            Debug.LogWarning("⏳ Board is still starting up (waiting for Firmata) - output ignored. " +
                             "Check SerialManager.IsFirmataReady before writing.");
            return false;
        }
        if (declared == null || !declared.Contains(pin))
        {
            Debug.LogWarning($"⚠️ Pin {pin} is not declared in {listName} on this profile.");
            return false;
        }
        if (!configured.Contains(pin))
        {
            Debug.LogError($"❌ Pin {pin} was never configured as an output (role conflict or out-of-range " +
                           "pin, see earlier errors, or it was added to the profile after connecting) - " +
                           "fix the profile and reconnect.");
            return false;
        }
        return true;
    }

    /// <summary>
    /// A Firmata digital write addresses a whole 8-pin port at once, so this maintains
    /// a per-port bitmask cache and only flips the bit for the requested pin - other
    /// output pins sharing that port keep their last-commanded state.
    /// </summary>
    void WriteDigitalOutputRaw(int pin, bool state)
    {
        int port = pin / 8;
        int bitIndex = pin % 8;

        int mask = _firmataOutputPortBitmask.TryGetValue(port, out var existing) ? existing : 0;
        mask = state ? (mask | (1 << bitIndex)) : (mask & ~(1 << bitIndex));
        _firmataOutputPortBitmask[port] = mask;

        byte lsb = (byte)(mask & 0x7F);
        byte msb = (byte)((mask >> 7) & 0x01);
        _serialPort.Write(new byte[] { (byte)(FirmataProtocol.DIGITAL_MESSAGE | port), lsb, msb }, 0, 3);
    }

    /// <summary>
    /// PWM and servo writes both use Firmata's analog message, addressed by the raw
    /// pin number (not an analog channel index - that distinction only applies to
    /// analog INPUT). Pins 0-15 fit the 4-bit nibble in the command byte directly;
    /// pins above that need the Extended Analog sysex form instead.
    /// </summary>
    void WriteAnalogOutputRaw(int pin, int value)
    {
        byte lsb = (byte)(value & 0x7F);
        byte msb = (byte)((value >> 7) & 0x7F);

        if (pin <= 15)
        {
            _serialPort.Write(new byte[] { (byte)(FirmataProtocol.ANALOG_MESSAGE | pin), lsb, msb }, 0, 3);
        }
        else
        {
            _serialPort.Write(new byte[]
            {
                FirmataProtocol.START_SYSEX, FirmataProtocol.EXTENDED_ANALOG,
                (byte)pin, lsb, msb, FirmataProtocol.END_SYSEX
            }, 0, 6);
        }
    }

    // ---------------- Shared ----------------

    public void SendLine(string line)
    {
        try
        {
            if (_serialPort != null && _serialPort.IsOpen)
            {
                _serialPort.WriteLine(line);
                Debug.Log($"⬆ Sent: {line}");
            }
            else
            {
                Debug.LogWarning("📭 Attempted to send, but serial port not open.");
            }
        }
        catch (Exception ex)
        {
            Debug.LogError($"❌ Send failed: {ex.Message}");
        }
    }

    public void ClearRecentLines()
    {
        lock (_recentLines)
            _recentLines.Clear();
        LinesVersion++;
    }

    private bool IsGibberish(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return true;
        int printable = 0;
        foreach (char c in line)
            if (c >= 32 && c <= 126) printable++;

        return (float)printable / line.Length < 0.75f;
    }

    void OnApplicationQuit() => StopSerial();

    void OnDisable()
    {
        StopSerial();
        SerialHub.Unregister(this);
    }
}
}

#endif