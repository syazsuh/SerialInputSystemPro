#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Ports;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;
using UnityEngine.InputSystem;
using SISPro.EditorTools;

namespace SISPro
{
/// <summary>
/// "Set Up Arduino" - the beginner entry point for the whole plugin.
/// (Class name kept as SerialCapabilityExplorerWindow so existing references and
/// saved window layouts keep working; only the menu path and title changed.)
///
/// "Find My Arduino" probes each serial port for a board running Firmata and
/// connects to the first one that answers; the manual port picker is still there
/// as a fallback. Deliberately NOT done automatically when the window opens:
/// OnEnable runs after every script recompile, and opening a port resets the
/// Arduino and holds the port - so an auto-probe there would reset the board on
/// every compile and fight SerialManager for the port.
///
/// Pre-Play surface: pick a serial port, talk Firmata to it (no Play Mode needed),
/// read back its firmware name and per-pin capabilities, and write the discovered
/// pin layout either into an existing SerialDeviceProfile or into a brand-new one
/// created on the spot.
///
/// On connect, the board is scanned automatically (firmware, capabilities, analog
/// mapping); "Re-scan Board" repeats that scan without reconnecting.
///
/// Each discovered pin gets an explicit role assignment (Unused / Digital Input /
/// Digital Output / PWM Output / Servo Output / Analog Input), restricted to what
/// the pin actually supports and defaulted sensibly. Because a pin can only hold
/// one entry in _pinRoles, overlap between roles is impossible by construction.
/// SerialDeviceProfile.ValidatePinAssignments() is still called defensively
/// after applying (and again at runtime in SerialManager) to catch conflicts that
/// might be introduced by hand-editing a profile outside this window.
///
/// Applying also (optionally) generates/updates an InputActionAsset for the
/// profile via SISProInputActionsBuilder, so one click goes from "board on a USB
/// cable" to "actions ready to bind" - no separate trip to the Input Actions Wizard.
///
/// This window owns its own FirmataCapabilityClient instance and polls it from
/// EditorApplication.update (there is no MonoBehaviour Update() in the editor).
/// It must close the client deterministically before a domain reload, or the
/// same close/read race that SerialManager guards against can leave the port
/// wedged - see the AssemblyReloadEvents hookup in OnEnable/OnDisable.
/// </summary>
public class SerialCapabilityExplorerWindow : EditorWindow
{
    [MenuItem("Tools/SISPro/Set Up Arduino", priority = 0)]
    public static void ShowWindow()
    {
        var w = GetWindow<SerialCapabilityExplorerWindow>("Set Up Arduino");

        // Small minimum so it can still dock into a narrow panel - the whole window
        // scrolls, so nothing gets cut off at any size.
        w.minSize = new Vector2(400, 360);

        // First open as a floating window: start tall enough to show a typical
        // Uno/Nano pin list without scrolling. Docked windows ignore this.
        if (w.position.height < 700)
        {
            var r = w.position;
            float h = Mathf.Min(900f, Screen.currentResolution.height - 120f);
            w.position = new Rect(r.x, Mathf.Max(40f, r.y), Mathf.Max(r.width, 520f), h);
        }

        w.Show();
    }

    /// <summary>
    /// The role a discovered pin is assigned to. Exactly one per pin.
    /// </summary>
    enum PinRole
    {
        Unused,
        DigitalInput,
        DigitalOutput,
        PwmOutput,
        ServoOutput,
        AnalogInput
    }

    // --- connection state ---
    string[] _ports = new string[0];
    int _selectedPortIndex;
    int _baudRate = 57600; // StandardFirmata default
    FirmataCapabilityClient _client;
    bool _connected;
    string _statusMessage = "Not connected.";
    string _activePort;             // port of the current/last connection, for status text

    // --- auto-detect ("Find My Arduino") ---
    bool _autoDetecting;
    readonly Queue<string> _probeQueue = new Queue<string>();
    double _probeDeadline;
    int _probeTotal;
    const double ProbeTimeoutSeconds = 5.0; // port open + ~1.5s board reset + firmware reply
    bool _showManualConnect;

    // --- discovered data ---
    string _firmwareName;
    int _firmwareMajor, _firmwareMinor;
    List<PinCapability> _pins;
    Dictionary<int, int> _analogChannelToPin;
    Dictionary<int, PinRole> _pinRoles = new Dictionary<int, PinRole>();

    // --- layout state ---
    Vector2 _windowScroll;          // one scroll view for the whole window - nothing gets clipped
    bool _showPinList = true;
    bool _showExistingProfile;      // "update existing" is the secondary path, collapsed by default

    // --- apply target ---
    SerialDeviceProfile _targetProfile;
    string _newProfileName = "NewSerialDevice";
    const string GeneratedDevicesFolder = "Assets/SISPro/GeneratedDevices"; // same convention as SerialDeviceCodeGenerator

    // --- input actions automation ---
    bool _generateInputActions = true;
    InputActionAsset _targetInputActions; // optional; blank = one asset per profile, next to it

    void OnEnable()
    {
        RefreshPortList();
        EditorApplication.update += OnEditorUpdate;
        AssemblyReloadEvents.beforeAssemblyReload += HardClose;
        EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
    }

    void OnDisable()
    {
        EditorApplication.update -= OnEditorUpdate;
        AssemblyReloadEvents.beforeAssemblyReload -= HardClose;
        EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
        HardClose();
    }

    /// <summary>
    /// Release the port before entering Play Mode. Otherwise the most natural flow -
    /// set up the board here, then press Play - fails: SerialManager's Open() gets
    /// "access denied" because this window is still holding the same port. (With
    /// domain reload enabled, beforeAssemblyReload also closes it; this covers
    /// "Enter Play Mode Options" with reload disabled.)
    /// </summary>
    void OnPlayModeStateChanged(PlayModeStateChange change)
    {
        if (change == PlayModeStateChange.ExitingEditMode && _client != null)
        {
            HardClose();
            _statusMessage = "Released " + (_activePort ?? "the port") + " so your scene's SerialManager can use it in Play Mode.";
        }
    }

    /// <summary>Stops the worker thread and releases the port, without touching status text.</summary>
    void DisposeClient()
    {
        // Always safe to call even if already disconnected. This is what prevents the
        // "port busy until Unity restarts" failure mode: a recompile while connected
        // would otherwise yank the C# world out from under a live read thread.
        if (_client != null)
        {
            _client.Dispose();
            _client = null;
        }
        _connected = false;
    }

    void HardClose()
    {
        bool wasActive = _client != null || _autoDetecting;
        _autoDetecting = false;
        _probeQueue.Clear();
        DisposeClient();
        if (wasActive)
            _statusMessage = "Disconnected.";
    }

    void RefreshPortList()
    {
        _ports = SerialPort.GetPortNames();
        if (_selectedPortIndex >= _ports.Length) _selectedPortIndex = 0;
    }

    void OnGUI()
    {
        // No try/finally here on purpose: object pickers throw ExitGUIException to
        // abort the frame, and ending layout groups after that logs spurious errors.
        _windowScroll = EditorGUILayout.BeginScrollView(_windowScroll);
        DrawContents();
        EditorGUILayout.EndScrollView();
    }

    void DrawContents()
    {
        EditorGUILayout.LabelField("Set Up Arduino", EditorStyles.boldLabel);

        // Only draws anything when a project setting would break serial ports.
        SISProSetupChecks.DrawProblemsBanner();

        if (_pins == null)
        {
            EditorGUILayout.HelpBox(
                "1. Upload StandardFirmata to your board once\n" +
                "    (Arduino IDE: File > Examples > Firmata > StandardFirmata).\n" +
                "2. Plug the board in and click Find My Arduino.\n" +
                "3. Choose what each pin does, then Create Profile.",
                MessageType.Info);
        }

        EditorGUILayout.Space(6);
        DrawConnectionSection();

        EditorGUILayout.Space(10);
        DrawDiscoverySection();

        EditorGUILayout.Space(10);
        DrawApplySection();
    }

    // ---------------------------------------------------------------------
    // Connection
    // ---------------------------------------------------------------------

    void DrawConnectionSection()
    {
        bool idle = _client == null && !_autoDetecting;

        if (idle)
        {
            var prevColor = GUI.backgroundColor;
            GUI.backgroundColor = new Color(0.45f, 0.8f, 0.5f);
            bool clicked = GUILayout.Button(_pins == null ? "Find My Arduino" : "Find My Arduino Again",
                GUILayout.Height(36));
            GUI.backgroundColor = prevColor;
            if (clicked) StartAutoDetect();
        }
        else
        {
            string label = _autoDetecting ? "Stop Searching" : _connected ? "Disconnect" : "Cancel";
            if (GUILayout.Button(label, GUILayout.Height(28)))
                HardClose();
        }

        EditorGUILayout.LabelField(_statusMessage, EditorStyles.wordWrappedLabel);

        if (!string.IsNullOrEmpty(_firmwareName))
            EditorGUILayout.LabelField("Firmware:", _firmwareName + " v" + _firmwareMajor + "." + _firmwareMinor);

        // Fallback for when auto-detect can't decide (several boards plugged in, a
        // custom baud rate, a port the heuristics skipped).
        _showManualConnect = EditorGUILayout.Foldout(_showManualConnect, "Choose port manually", true);
        if (!_showManualConnect) return;

        EditorGUI.indentLevel++;
        using (new EditorGUI.DisabledScope(!idle))
        {
            EditorGUILayout.BeginHorizontal();
            if (_ports.Length == 0)
                EditorGUILayout.LabelField("Port", "No serial ports found.");
            else
                _selectedPortIndex = EditorGUILayout.Popup("Port", _selectedPortIndex, _ports);
            if (GUILayout.Button("Refresh", GUILayout.Width(70)))
                RefreshPortList();
            EditorGUILayout.EndHorizontal();

            _baudRate = EditorGUILayout.IntField("Baud Rate", _baudRate);

            using (new EditorGUI.DisabledScope(_ports.Length == 0))
            {
                if (GUILayout.Button("Connect to This Port", GUILayout.Height(24)))
                    Connect();
            }
        }
        EditorGUI.indentLevel--;
    }

    void ResetDiscoveredData()
    {
        _firmwareName = null;
        _pins = null;
        _analogChannelToPin = null;
        _pinRoles.Clear();
    }

    void Connect()
    {
        if (_ports.Length == 0) return;

        HardClose();
        ResetDiscoveredData();

        _activePort = _ports[_selectedPortIndex];
        _statusMessage = "Connecting to " + _activePort + " (board resets on open, ~2s)...";

        _client = new FirmataCapabilityClient();
        _client.Connect(_activePort, _baudRate);
    }

    // ---------------------------------------------------------------------
    // Auto-detect
    // ---------------------------------------------------------------------

    /// <summary>
    /// Orders ports most-likely-Arduino first, and drops ones that are never an
    /// Arduino over USB but can block for seconds on open (Bluetooth serial).
    /// </summary>
    static List<string> OrderProbeCandidates(string[] ports)
    {
        var candidates = new List<string>();
        foreach (var port in ports)
        {
            string lower = port.ToLowerInvariant();
            if (lower.Contains("bluetooth")) continue;

            // macOS lists each device twice: tty.* (waits for carrier detect on open)
            // and cu.* (opens immediately). Probe only the cu.* twin.
            if (lower.StartsWith("/dev/tty.") &&
                ports.Contains("/dev/cu." + port.Substring("/dev/tty.".Length)))
                continue;

            candidates.Add(port);
        }

        int Score(string port)
        {
            string l = port.ToLowerInvariant();
            if (l.Contains("usbmodem") || l.Contains("usbserial") || l.Contains("wchusbserial") ||
                l.Contains("ttyacm") || l.Contains("ttyusb"))
                return 0;               // mac/linux names for USB serial adapters
            if (l == "com1") return 2;  // on Windows usually a legacy motherboard port
            return 1;
        }

        return candidates.OrderBy(Score).ToList(); // OrderBy is stable: OS order kept within a score
    }

    void StartAutoDetect()
    {
        HardClose();
        ResetDiscoveredData();
        RefreshPortList();

        _probeQueue.Clear();
        foreach (var port in OrderProbeCandidates(_ports))
            _probeQueue.Enqueue(port);
        _probeTotal = _probeQueue.Count;

        if (_probeTotal == 0)
        {
            _statusMessage = "No serial ports found. Plug the Arduino in with a data USB cable " +
                             "(some cables are charge-only), then try again.";
            return;
        }

        _autoDetecting = true;
        ProbeNext();
    }

    void ProbeNext()
    {
        DisposeClient();

        if (_probeQueue.Count == 0)
        {
            _autoDetecting = false;
            _statusMessage =
                "No board running Firmata answered on " + _probeTotal + " port(s). Check that:\n" +
                "- StandardFirmata is uploaded (Arduino IDE: File > Examples > Firmata > StandardFirmata)\n" +
                "- the Arduino IDE's Serial Monitor is closed (it locks the port)\n" +
                "- the scene isn't in Play Mode with a SerialManager using the board";
            return;
        }

        _activePort = _probeQueue.Dequeue();
        int attempt = _probeTotal - _probeQueue.Count;
        _statusMessage = "Looking for an Arduino on " + _activePort + "... (" + attempt + "/" + _probeTotal + ")";
        _probeDeadline = EditorApplication.timeSinceStartup + ProbeTimeoutSeconds;

        // Sending Firmata's firmware query to a non-Firmata device is harmless - it's a
        // 3-byte sysex frame that anything else just ignores or echoes.
        _client = new FirmataCapabilityClient();
        _client.Connect(_activePort, _baudRate);
    }

    void OnProbeSucceeded()
    {
        _autoDetecting = false;
        _probeQueue.Clear();

        int index = Array.IndexOf(_ports, _activePort);
        if (index >= 0) _selectedPortIndex = index;
    }

    /// <summary>
    /// Queues the full discovery sequence. Firmata answers requests in order, so
    /// the three responses arrive back-to-back and are handled in OnEditorUpdate.
    /// </summary>
    void ScanBoard()
    {
        if (_client == null || !_connected) return;
        if (!_autoDetecting) // keep the "Looking for an Arduino on COMx..." message while probing
            _statusMessage = "Scanning " + (_activePort ?? "board") + "...";
        _client.RequestFirmware();
        _client.RequestCapabilities();
        _client.RequestAnalogMapping();
    }

    // ---------------------------------------------------------------------
    // Discovery + role assignment
    // ---------------------------------------------------------------------

    /// <summary>
    /// Which roles a pin is allowed to be assigned to, based on what the board's
    /// capability response actually reported for it. Unused is always first.
    /// </summary>
    static List<PinRole> AllowedRoles(PinCapability pin)
    {
        var roles = new List<PinRole> { PinRole.Unused };

        bool hasInput = pin.Modes.Any(m => m.Mode == FirmataPinMode.Input || m.Mode == FirmataPinMode.Pullup);
        bool hasOutput = pin.Modes.Any(m => m.Mode == FirmataPinMode.Output);
        bool hasPwm = pin.Modes.Any(m => m.Mode == FirmataPinMode.Pwm);
        bool hasServo = pin.Modes.Any(m => m.Mode == FirmataPinMode.Servo);
        bool hasAnalog = pin.SupportsAnalog;

        if (hasInput) roles.Add(PinRole.DigitalInput);
        if (hasOutput) roles.Add(PinRole.DigitalOutput);
        if (hasPwm) roles.Add(PinRole.PwmOutput);
        if (hasServo) roles.Add(PinRole.ServoOutput);
        if (hasAnalog) roles.Add(PinRole.AnalogInput);

        return roles;
    }

    /// <summary>
    /// Analog-capable pins default to Analog In, other readable pins to Digital In,
    /// everything else to Unused - never guess an output, since driving a pin that's
    /// wired to a switch is the worse mistake.
    /// </summary>
    static PinRole DefaultRole(PinCapability pin)
    {
        if (pin.SupportsAnalog) return PinRole.AnalogInput;
        if (pin.SupportsDigital) return PinRole.DigitalInput;
        return PinRole.Unused;
    }

    static string RoleLabel(PinRole role)
    {
        switch (role)
        {
            case PinRole.Unused: return "Unused";
            case PinRole.DigitalInput: return "Digital In";
            case PinRole.DigitalOutput: return "Digital Out";
            case PinRole.PwmOutput: return "PWM Out";
            case PinRole.ServoOutput: return "Servo Out";
            case PinRole.AnalogInput: return "Analog In";
            default: return role.ToString();
        }
    }

    /// <summary>
    /// Short labels so the mode list fits beside the role popup instead of being
    /// clipped ("Input, PullUp, Output, Pwm, Serv..."). Full names are in the tooltip.
    /// </summary>
    static string ShortMode(FirmataPinMode mode)
    {
        switch (mode)
        {
            case FirmataPinMode.Input: return "In";
            case FirmataPinMode.Pullup: return "Pull";
            case FirmataPinMode.Output: return "Out";
            case FirmataPinMode.Pwm: return "PWM";
            case FirmataPinMode.Servo: return "Servo";
            case FirmataPinMode.Analog: return "Analog";
            default: return mode.ToString();
        }
    }

    void DrawDiscoverySection()
    {
        using (new EditorGUI.DisabledScope(!_connected))
        {
            if (GUILayout.Button("Re-scan Board", GUILayout.Height(24)))
                ScanBoard();
        }

        if (_pins == null) return;

        EditorGUILayout.Space(4);

        bool anyAnalogCapable = _pins.Any(p => p.SupportsAnalog);
        if (anyAnalogCapable && _analogChannelToPin == null)
        {
            EditorGUILayout.HelpBox(
                "Some pins support analog input, but the analog mapping hasn't arrived yet. " +
                "Click 'Re-scan Board' if this persists - any pin set to Analog In will be " +
                "skipped when you apply without it.", MessageType.Warning);
        }

        // Summary is always visible, so the list can be collapsed once roles look right.
        var counts = _pins
            .Select(p => _pinRoles.TryGetValue(p.PinNumber, out var r) ? r : PinRole.Unused)
            .GroupBy(r => r)
            .OrderBy(g => g.Key)
            .Select(g => g.Count() + " " + RoleLabel(g.Key));
        string summary = string.Join("  |  ", counts.ToArray());

        _showPinList = EditorGUILayout.Foldout(_showPinList,
            $"Pin roles ({_pins.Count} pins, {(_analogChannelToPin?.Count ?? 0)} analog channels)", true);
        EditorGUILayout.LabelField(summary, EditorStyles.miniLabel);

        if (!_showPinList) return;

        EditorGUILayout.BeginHorizontal();
        if (GUILayout.Button("All Defaults", EditorStyles.miniButtonLeft))
            foreach (var pin in _pins) _pinRoles[pin.PinNumber] = DefaultRole(pin);
        if (GUILayout.Button("All Unused", EditorStyles.miniButtonRight))
            foreach (var pin in _pins) _pinRoles[pin.PinNumber] = PinRole.Unused;
        EditorGUILayout.EndHorizontal();

        // No inner scroll box - the whole window scrolls, so a Mega's 70 pins
        // just make the window longer instead of hiding in a 220px strip.
        float labelWidth = Mathf.Clamp(position.width * 0.5f, 160f, 300f);
        foreach (var pin in _pins)
        {
            string fullModes = string.Join(", ", pin.Modes.Select(m => m.Mode.ToString()).ToArray());
            string shortModes = string.Join(" ", pin.Modes.Select(m => ShortMode(m.Mode)).Distinct().ToArray());
            bool reserved = pin.Modes.Count == 0;

            if (!_pinRoles.TryGetValue(pin.PinNumber, out var currentRole))
            {
                currentRole = DefaultRole(pin);
                _pinRoles[pin.PinNumber] = currentRole;
            }

            var allowed = AllowedRoles(pin);
            int currentIndex = allowed.IndexOf(currentRole);
            if (currentIndex < 0) currentIndex = 0; // role no longer valid for this pin - fall back to Unused

            string label = reserved
                ? $"Pin {pin.PinNumber}  - reserved"
                : $"Pin {pin.PinNumber}  {shortModes}";
            string tooltip = reserved
                ? "The board reports no usable modes for this pin (e.g. pins 0/1 are the USB serial link on an Uno)."
                : fullModes;

            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField(new GUIContent(label, tooltip), GUILayout.Width(labelWidth));

            using (new EditorGUI.DisabledScope(reserved))
            {
                var labels = allowed.Select(RoleLabel).ToArray();
                int newIndex = EditorGUILayout.Popup(currentIndex, labels);
                _pinRoles[pin.PinNumber] = allowed[newIndex];
            }
            EditorGUILayout.EndHorizontal();
        }
    }

    // ---------------------------------------------------------------------
    // Apply
    // ---------------------------------------------------------------------

    void DrawApplySection()
    {
        if (_pins == null)
        {
            EditorGUILayout.HelpBox(
                _connected
                    ? "Waiting for the board's capability response - click 'Re-scan Board' if nothing appears."
                    : "Connect to a board first - there's nothing discovered yet to apply.",
                MessageType.None);
            return;
        }

        // --- Primary path: create a new profile (what most users want) ---
        EditorGUILayout.LabelField("Create Profile", EditorStyles.boldLabel);

        _newProfileName = EditorGUILayout.TextField("Profile Name", _newProfileName);

        _generateInputActions = EditorGUILayout.ToggleLeft("Also generate Input Actions", _generateInputActions);
        if (_generateInputActions)
        {
            EditorGUI.indentLevel++;
            _targetInputActions = (InputActionAsset)EditorGUILayout.ObjectField(
                "Into Asset (optional)", _targetInputActions, typeof(InputActionAsset), false);
            if (_targetInputActions == null)
                EditorGUILayout.LabelField("Blank = a new <Device>Controls.inputactions next to the profile",
                    EditorStyles.miniLabel);
            EditorGUI.indentLevel--;
        }

        EditorGUILayout.Space(4);

        string createLabel = _generateInputActions ? "Create Profile + Input Actions" : "Create Profile";

        var prevColor = GUI.backgroundColor;
        GUI.backgroundColor = new Color(0.45f, 0.8f, 0.5f); // make the main action obvious
        bool createClicked = GUILayout.Button(createLabel, GUILayout.Height(34));
        GUI.backgroundColor = prevColor;
        if (createClicked)
            CreateAndApplyNewProfile();

        // --- Secondary path: overwrite an existing profile (collapsed by default) ---
        EditorGUILayout.Space(10);
        _showExistingProfile = EditorGUILayout.Foldout(_showExistingProfile,
            "Update an existing profile instead", true);

        if (_showExistingProfile)
        {
            EditorGUI.indentLevel++;
            _targetProfile = (SerialDeviceProfile)EditorGUILayout.ObjectField(
                "Existing Profile", _targetProfile, typeof(SerialDeviceProfile), false);
            EditorGUI.indentLevel--;

            using (new EditorGUI.DisabledScope(_targetProfile == null))
            {
                string updateLabel = _generateInputActions
                    ? "Write Pins Into Profile + Update Input Actions"
                    : "Write Pins Into Profile";
                if (GUILayout.Button(updateLabel, GUILayout.Height(26)))
                    ApplyToProfile(_targetProfile);
            }

            EditorGUILayout.LabelField("Replaces the profile's pin lists with the roles above.",
                EditorStyles.miniLabel);
        }

        EditorGUILayout.Space(6);
    }

    void OnEditorUpdate()
    {
        if (_client == null) return;

        // A port that opened but never answered like Firmata (another device, or an
        // Arduino running a different sketch) - move on to the next candidate.
        if (_autoDetecting && EditorApplication.timeSinceStartup > _probeDeadline)
        {
            ProbeNext();
            Repaint();
            return;
        }

        bool dirty = false;
        FirmataEvent evt;
        while (_client != null && _client.TryDequeueEvent(out evt))
        {
            dirty = true;

            if (evt is FirmataConnectedEvent)
            {
                _connected = true;
                ScanBoard(); // auto-discover on connect (also the probe's "are you Firmata?" question)
            }
            else if (evt is FirmataDisconnectedEvent)
            {
                var d = (FirmataDisconnectedEvent)evt;

                if (_autoDetecting)
                {
                    // Port in use / access denied / not a real device - try the next one.
                    ProbeNext();
                    continue;
                }

                _connected = false;
                _statusMessage = "Disconnected from " + (_activePort ?? "port") + " (" + d.Reason + ").";

                // Worker thread has exited on its own (unplugged, open failed) -
                // release the client so Find My Arduino comes back.
                _client.Dispose();
                _client = null;
            }
            else if (evt is FirmataFirmwareEvent)
            {
                var f = (FirmataFirmwareEvent)evt;

                if (_autoDetecting)
                    OnProbeSucceeded();

                _firmwareName = f.Name;
                _firmwareMajor = f.Major;
                _firmwareMinor = f.Minor;
                _statusMessage = "Found " + f.Name + " on " + _activePort + ". Reading pins...";

                // Only overwrite the suggested new-profile name if the user hasn't
                // already typed something of their own over the default placeholder.
                // Uniqueness against existing profiles is enforced at create time.
                if (_newProfileName == "NewSerialDevice" && !string.IsNullOrWhiteSpace(f.Name))
                    _newProfileName = Regex.Replace(Path.GetFileNameWithoutExtension(f.Name), "[^a-zA-Z0-9_]", "_");
            }
            else if (evt is FirmataCapabilitiesEvent)
            {
                var c = (FirmataCapabilitiesEvent)evt;
                _pins = c.Pins;
                _statusMessage = "Connected to " + _activePort + ": received " + c.Pins.Count + " pins.";

                // A fresh capability response is the source of truth - re-seed roles.
                _pinRoles.Clear();
                foreach (var pin in _pins)
                    _pinRoles[pin.PinNumber] = DefaultRole(pin);
            }
            else if (evt is FirmataAnalogMappingEvent)
            {
                var a = (FirmataAnalogMappingEvent)evt;
                _analogChannelToPin = a.ChannelToPin;
                _statusMessage = "Connected to " + _activePort + ": " + (_pins != null ? _pins.Count : 0) +
                                 " pins, " + a.ChannelToPin.Count + " analog channels. Choose pin roles below.";
            }
        }

        if (dirty) Repaint();
    }

    void ApplyToProfile(SerialDeviceProfile profile)
    {
        if (profile == null || _pins == null) return;

        var digitalPins = new List<int>();
        var digitalOutputPins = new List<int>();
        var pwmOutputPins = new List<int>();
        var servoOutputPins = new List<int>();
        var analogChannels = new List<int>();
        var analogChannelPins = new List<int>();

        // Inverted lookup: physical pin -> analog channel. Only used for pins the
        // user actually assigned to AnalogInput.
        var pinToChannel = new Dictionary<int, int>();
        if (_analogChannelToPin != null)
        {
            foreach (var kvp in _analogChannelToPin)
                pinToChannel[kvp.Value] = kvp.Key;
        }

        int maxResolutionValue = 1023; // fallback for boards that don't report resolution
        bool sawResolution = false;
        int skippedAnalogNoMapping = 0;

        foreach (var pin in _pins)
        {
            if (!_pinRoles.TryGetValue(pin.PinNumber, out var role))
                role = PinRole.Unused;

            switch (role)
            {
                case PinRole.Unused:
                    break;

                case PinRole.DigitalInput:
                    digitalPins.Add(pin.PinNumber);
                    break;

                case PinRole.DigitalOutput:
                    digitalOutputPins.Add(pin.PinNumber);
                    break;

                case PinRole.PwmOutput:
                    pwmOutputPins.Add(pin.PinNumber);
                    break;

                case PinRole.ServoOutput:
                    servoOutputPins.Add(pin.PinNumber);
                    break;

                case PinRole.AnalogInput:
                    if (!pinToChannel.TryGetValue(pin.PinNumber, out int channel))
                    {
                        Debug.LogWarning($"[SISPro] Pin {pin.PinNumber} is set to Analog In, but no analog " +
                                         "channel mapping is known for it - click 'Re-scan Board' first. " +
                                         "Skipped for now.");
                        skippedAnalogNoMapping++;
                        break;
                    }

                    analogChannels.Add(channel);
                    analogChannelPins.Add(pin.PinNumber);

                    if (pin.SupportsAnalog && pin.AnalogResolutionBits > 0)
                    {
                        int candidateMax = (1 << pin.AnalogResolutionBits) - 1;
                        if (!sawResolution || candidateMax > maxResolutionValue)
                        {
                            maxResolutionValue = candidateMax;
                            sawResolution = true;
                        }
                    }
                    break;
            }
        }

        Undo.RecordObject(profile, "Apply Discovered Pins");
        profile.digitalPins = digitalPins;
        profile.digitalOutputPins = digitalOutputPins;
        profile.pwmOutputPins = pwmOutputPins;
        profile.servoOutputPins = servoOutputPins;
        profile.analogChannels = analogChannels;
        profile.analogChannelPins = analogChannelPins;
        profile.analogMaxValue = maxResolutionValue;
        profile.baudRate = _baudRate;
        profile.transport = SerialTransport.Firmata; // this window only ever discovers via Firmata

        EditorUtility.SetDirty(profile);
        AssetDatabase.SaveAssets();

        // Defensive check - role assignment here is overlap-free by construction,
        // so this only fires if something else mutated the profile's lists.
        if (!profile.ValidatePinAssignments(out var conflicts, out _))
        {
            foreach (var c in conflicts)
                Debug.LogError("[SISPro] Unexpected pin conflict after apply: " + c);
        }

        _statusMessage = $"Applied to '{profile.name}': {digitalPins.Count} digital in, " +
                         $"{digitalOutputPins.Count} digital out, {pwmOutputPins.Count} PWM out, " +
                         $"{servoOutputPins.Count} servo out, {analogChannels.Count} analog in " +
                         $"(max value {maxResolutionValue})" +
                         (skippedAnalogNoMapping > 0 ? $", {skippedAnalogNoMapping} analog pin(s) skipped (no mapping)." : ".");
        Debug.Log("[SISPro] " + _statusMessage);

        // Assigning fields from code doesn't fire OnValidate, so register the layout
        // now - the binding picker can see the device immediately, no Play Mode.
        SerialDeviceLayoutRegistry.EnsureRegistered(profile);

        if (_generateInputActions)
            GenerateInputActionsFor(profile);
    }

    void GenerateInputActionsFor(SerialDeviceProfile profile)
    {
        // With zero input pins, BuildFromProfile would fall back to the legacy
        // digitalCount/analogCount path and bind D0..D8 controls that don't exist.
        if (!profile.HasDiscoveredPins)
        {
            Debug.Log("[SISPro] No input pins assigned - skipped Input Actions (outputs don't use actions).");
            return;
        }

        string id = Regex.Replace(
            string.IsNullOrWhiteSpace(profile.deviceID) ? profile.name : profile.deviceID,
            "[^a-zA-Z0-9_]", "_");
        string folder = Path.GetDirectoryName(AssetDatabase.GetAssetPath(profile)).Replace('\\', '/');
        string defaultPath = $"{folder}/{id}Controls.inputactions";

        try
        {
            // Map and scheme are named after the device, so several boards can share
            // one asset without their D2/A0 actions merging into the same action.
            var asset = SISProInputActionsBuilder.BuildFromProfile(
                profile, _targetInputActions, defaultPath,
                actionMapName: id, controlSchemeName: id);

            if (asset != null)
            {
                string assetPath = AssetDatabase.GetAssetPath(asset);
                EditorGUIUtility.PingObject(asset);
                _statusMessage += $" Input Actions: {assetPath}";
                Debug.Log($"[SISPro] Input Actions generated for '{profile.name}' at {assetPath}");
            }
        }
        catch (Exception ex)
        {
            Debug.LogError($"[SISPro] Profile was applied, but Input Actions generation failed: {ex.Message}");
            _statusMessage += " (Input Actions generation failed - see Console.)";
        }
    }

    /// <summary>
    /// Two boards running the same firmware both suggest the same name (e.g.
    /// "StandardFirmata"). GenerateUniqueAssetPath only made the asset *path* unique;
    /// deviceID and layoutName stayed identical, so the second profile's layout
    /// silently replaced the first, and at runtime both boards fed one Input System
    /// device. This picks a name no existing profile, registered layout, or device
    /// folder already uses, by appending 2, 3, ...
    /// </summary>
    static string MakeUniqueDeviceName(string baseName)
    {
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var guid in AssetDatabase.FindAssets("t:SerialDeviceProfile"))
        {
            var p = AssetDatabase.LoadAssetAtPath<SerialDeviceProfile>(AssetDatabase.GUIDToAssetPath(guid));
            if (p == null) continue;
            if (!string.IsNullOrEmpty(p.deviceID)) taken.Add(p.deviceID);
            if (!string.IsNullOrEmpty(p.layoutName)) taken.Add(p.layoutName);
        }
        foreach (var layout in InputSystem.ListLayouts())
            taken.Add(layout); // e.g. a compiled <Name>InputDevice from the legacy generator

        bool IsTaken(string n) =>
            taken.Contains(n) ||
            taken.Contains(n + "InputDevice") ||
            AssetDatabase.IsValidFolder(GeneratedDevicesFolder + "/" + n);

        string name = baseName;
        for (int i = 2; IsTaken(name); i++)
            name = baseName + i;
        return name;
    }

    void CreateAndApplyNewProfile()
    {
        if (_pins == null) return;

        string requestedName = string.IsNullOrWhiteSpace(_newProfileName)
            ? "NewSerialDevice"
            : Regex.Replace(_newProfileName, "[^a-zA-Z0-9_]", "_");

        string safeName = MakeUniqueDeviceName(requestedName);
        if (safeName != requestedName)
        {
            Debug.Log($"[SISPro] '{requestedName}' is already used by another device - naming this one '{safeName}'.");
            _newProfileName = safeName;
        }

        string folder = GeneratedDevicesFolder + "/" + safeName;
        if (!AssetDatabase.IsValidFolder(folder))
        {
            Directory.CreateDirectory(folder);
            AssetDatabase.Refresh();
        }

        string assetPath = AssetDatabase.GenerateUniqueAssetPath(folder + "/" + safeName + "Profile.asset");

        var profile = ScriptableObject.CreateInstance<SerialDeviceProfile>();
        profile.deviceID = safeName;
        profile.layoutName = safeName + "InputDevice";

        AssetDatabase.CreateAsset(profile, assetPath);
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();

        _targetProfile = AssetDatabase.LoadAssetAtPath<SerialDeviceProfile>(assetPath);
        Debug.Log("[SISPro] Created new profile at " + assetPath);

        ApplyToProfile(_targetProfile); // same path as the existing-profile button, incl. Input Actions
    }
}
}
#endif