#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;

namespace SISPro
{
/// <summary>
/// "Input Monitor" - live, per-pin view of a connected board during Play Mode,
/// built to answer one question: is the problem the Arduino, or Unity?
///
/// Every pin is shown at two points in the pipeline:
///
///   BOARD  - the raw "D2:1" / "A0:512" value exactly as it came off the serial
///            port (SerialManager.OnLineReceived). Both transports produce this
///            same text form, so this works for Firmata and custom sketches alike.
///   UNITY  - the value of the matching control on the Input System device that
///            SerialToInputSystemAdapter feeds.
///
/// Digital pins are compared the way the adapter maps them: with the profile's
/// digitalActiveLow on (the default, for pull-up wiring), raw 0 = pressed and
/// raw 1 = released. Both dots show that logical pressed/released state, so they
/// light up together; the Board column's text still shows the raw HIGH/LOW.
///
/// Each pin is still diagnosed (ARDUINO side vs UNITY side), but there is no
/// per-row status column: the verdict box at the top sums it up, problem pins
/// get their name highlighted, and hovering any row shows the full explanation.
///
/// Read-only: it only listens. It never opens a port or writes to the device,
/// so it can't change the behaviour it's trying to diagnose.
///
/// GUI note: rows are snapshotted on the Layout event and drawn from that
/// snapshot, because serial lines can arrive between Layout and Repaint and a
/// changing row count mid-frame breaks IMGUI.
/// </summary>
public class SerialInputMonitorWindow : EditorWindow
{
    [MenuItem("Tools/SISPro/Input Monitor", priority = 2)]
    public static void ShowWindow()
    {
        var w = GetWindow<SerialInputMonitorWindow>("Input Monitor");
        w.minSize = new Vector2(400, 320);
        w.Show();
    }

    enum Side { Ok, Info, Arduino, Unity }

    class PinState
    {
        public string Key;
        public bool Expected;               // declared on the profile
        public bool HasRaw;
        public string RawValue;
        public double LastReceived;
        public double LastChanged = -10;
        public int ChangeCount;
        public double MismatchSince = -1;   // board vs Unity disagreement start time
        public readonly Queue<double> RecentChanges = new Queue<double>(); // for flicker detection
    }

    class Row
    {
        public PinState Pin;
        public bool Analog;
        public InputControl Control;
        public bool UnityPressed;
        public float UnityValue;
        public string Actions;
        public Side Side;
        public string Status;
        public string Tooltip;
    }

    // --- look ---
    static readonly Color OnColor = new Color(0.30f, 0.85f, 0.40f);
    static readonly Color OffColor = new Color(0.85f, 0.30f, 0.30f);
    static readonly Color NoDataColor = new Color(0.5f, 0.5f, 0.5f, 0.6f);
    static readonly Color ProblemColor = new Color(1.00f, 0.60f, 0.20f); // pin name tint when a row has a problem
    static readonly Color FlashColor = new Color(1f, 1f, 0.4f, 0.15f);
    static readonly Color StripeColor = new Color(0f, 0f, 0f, 0.06f);
    static readonly Color BarBackColor = new Color(0f, 0f, 0f, 0.25f);
    // Pull-up notice: a soft blue tint with an accent bar - informational, not a warning.
    // Text uses the skin's normal label colour, so it reads well in both light and dark.
    static Color NoticeBackColor => EditorGUIUtility.isProSkin
        ? new Color(0.35f, 0.60f, 0.95f, 0.14f)
        : new Color(0.25f, 0.50f, 0.90f, 0.12f);
    static readonly Color NoticeEdgeColor = new Color(0.35f, 0.62f, 1.00f, 0.9f);

    const float RowHeight = 20f;
    const float PinColWidth = 56f;
    const float BoardColWidth = 150f;
    const float UnityColWidth = 130f;

    // --- thresholds ---
    const double MismatchGraceSeconds = 0.3;  // an event takes a frame to be processed by the Input System
    const int FlickerPerSecond = 10;          // a human doesn't press a button 10x/s; a floating pin does
    const double NoDataWarnSeconds = 3.0;
    const float AnalogTolerance = 0.02f;

    // --- live data (fed from SerialManager's main-thread Update) ---
    readonly Dictionary<string, PinState> _pins = new Dictionary<string, PinState>(StringComparer.OrdinalIgnoreCase);
    readonly List<PinState> _order = new List<PinState>();

    SerialManager _manager;
    SerialToInputSystemAdapter _adapter;
    SerialDeviceProfile _expectedFrom;
    int _selected;
    double _attachedAt;

    string _lastLine;
    double _lastLineTime = -1;
    int _linesInWindow;
    double _rateWindowStart;
    float _linesPerSecond;

    readonly Dictionary<InputControl, List<string>> _actionsByControl = new Dictionary<InputControl, List<string>>();
    double _nextActionScan;
    double _nextRepaint;

    // --- snapshot (rebuilt on Layout, drawn on every event) ---
    readonly List<Row> _rows = new List<Row>();
    string[] _managerNames = new string[0];
    bool _showTable;
    bool _firmata;
    bool _activeLow = true;   // mirrors SerialDeviceProfile.digitalActiveLow (the adapter's mapping)
    int _analogMax = 1023;
    string _statusLine = "";
    string _deviceLine = "";
    string _verdict = "";
    MessageType _verdictType = MessageType.Info;

    Vector2 _scroll;
    bool _showLastLine = true;

    // ---------------------------------------------------------------------
    // Lifecycle
    // ---------------------------------------------------------------------

    void OnEnable()
    {
        wantsMouseMove = true; // keeps tooltips responsive
        EditorApplication.update += Tick;
        EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
    }

    void OnDisable()
    {
        EditorApplication.update -= Tick;
        EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
        Detach();
    }

    void OnPlayModeStateChanged(PlayModeStateChange change)
    {
        if (change == PlayModeStateChange.ExitingPlayMode || change == PlayModeStateChange.EnteredEditMode)
        {
            Detach();
            ResetData();
            Repaint();
        }
        else if (change == PlayModeStateChange.EnteredPlayMode)
        {
            Repaint();
        }
    }

    void Tick()
    {
        if (!EditorApplication.isPlaying)
        {
            if (_manager != null) Detach();
            return;
        }

        SerialHub.PruneDestroyed();
        var managers = SerialHub.Managers;
        SerialManager wanted = managers.Count == 0 ? null : managers[Mathf.Clamp(_selected, 0, managers.Count - 1)];

        if (!ReferenceEquals(wanted, _manager))
            Attach(wanted);
        else if (_manager != null && _manager.deviceProfile != _expectedFrom)
            RebuildExpected(); // profile swapped at runtime

        double now = EditorApplication.timeSinceStartup;

        if (now - _rateWindowStart >= 1.0)
        {
            _linesPerSecond = (float)(_linesInWindow / (now - _rateWindowStart));
            _linesInWindow = 0;
            _rateWindowStart = now;
        }

        if (now >= _nextActionScan)
        {
            ScanActions();
            _nextActionScan = now + 0.5;
        }

        if (now >= _nextRepaint)
        {
            Repaint();
            _nextRepaint = now + 1.0 / 20.0;
        }
    }

    void Attach(SerialManager manager)
    {
        Detach();
        ResetData();

        _manager = manager;
        _attachedAt = EditorApplication.timeSinceStartup;
        _rateWindowStart = _attachedAt;
        if (_manager == null) return;

        _manager.OnLineReceived?.AddListener(OnLineReceived);
        _adapter = _manager.GetComponent<SerialToInputSystemAdapter>();
        RebuildExpected();
    }

    void Detach()
    {
        if (!ReferenceEquals(_manager, null))
        {
            try { _manager.OnLineReceived?.RemoveListener(OnLineReceived); }
            catch { /* manager already torn down on Play Mode exit - nothing to unhook */ }
        }
        _manager = null;
        _adapter = null;
        _expectedFrom = null;
    }

    void ResetData()
    {
        _pins.Clear();
        _order.Clear();
        _actionsByControl.Clear();
        _lastLine = null;
        _lastLineTime = -1;
        _linesInWindow = 0;
        _linesPerSecond = 0;
    }

    void ClearCounters()
    {
        foreach (var p in _pins.Values)
        {
            p.ChangeCount = 0;
            p.RecentChanges.Clear();
            p.MismatchSince = -1;
        }
        // Drop keys the profile doesn't declare, so a one-off stray key doesn't linger forever.
        _order.RemoveAll(p => !p.Expected);
        foreach (var key in _pins.Keys.ToList())
            if (!_pins[key].Expected) _pins.Remove(key);
    }

    // ---------------------------------------------------------------------
    // Data in
    // ---------------------------------------------------------------------

    PinState GetOrAdd(string key)
    {
        if (!_pins.TryGetValue(key, out var p))
        {
            p = new PinState { Key = key };
            _pins[key] = p;
            _order.Add(p);
        }
        return p;
    }

    /// <summary>
    /// Pins the profile says should exist, in profile order, so they show up
    /// (grey, "no data") even before - or if never - the board sends them.
    /// Mirrors how SerialDeviceLayoutRegistry / the codegen name their controls.
    /// </summary>
    static IEnumerable<string> ExpectedKeys(SerialDeviceProfile profile)
    {
        if (profile == null) yield break;

        if (profile.HasDiscoveredPins)
        {
            foreach (var pin in profile.digitalPins) yield return "D" + pin;
            foreach (var channel in profile.analogChannels) yield return "A" + channel;
        }
        else
        {
            for (int i = 0; i < profile.digitalCount; i++) yield return "D" + i;
            for (int i = 0; i < profile.analogCount; i++) yield return "A" + i;
        }
    }

    void RebuildExpected()
    {
        _expectedFrom = _manager != null ? _manager.deviceProfile : null;

        foreach (var p in _pins.Values) p.Expected = false;

        var expected = new List<PinState>();
        foreach (var key in ExpectedKeys(_expectedFrom))
        {
            var p = GetOrAdd(key);
            if (p.Expected) continue; // duplicate entry in the profile
            p.Expected = true;
            expected.Add(p);
        }

        var extras = _order.Where(p => !p.Expected).ToList();
        _order.Clear();
        _order.AddRange(expected);
        _order.AddRange(extras);
    }

    /// <summary>
    /// Same split rules as SerialToInputSystemAdapter.OnSerialLineReceived, so the
    /// BOARD column shows exactly what the adapter was handed.
    /// </summary>
    void OnLineReceived(string line)
    {
        double now = EditorApplication.timeSinceStartup;
        _lastLine = line;
        _lastLineTime = now;
        _linesInWindow++;

        foreach (var segment in line.Split(';'))
        {
            if (string.IsNullOrWhiteSpace(segment)) continue;
            var kv = segment.Split(':');
            if (kv.Length != 2) continue;

            string key = kv[0].Trim();
            string value = kv[1].Trim();
            if (key.Length == 0) continue;

            var p = GetOrAdd(key);
            if (p.HasRaw && p.RawValue != value)
            {
                p.ChangeCount++;
                p.LastChanged = now;
                p.RecentChanges.Enqueue(now);
            }
            else if (!p.HasRaw)
            {
                p.LastChanged = now;
            }

            p.HasRaw = true;
            p.RawValue = value;
            p.LastReceived = now;
        }
    }

    InputDevice CurrentDevice()
    {
        if (_adapter == null) return null;
        var device = _adapter.targetDevice;
        return device != null && device.added ? device : null;
    }

    /// <summary>Which enabled actions currently resolve to each control on our device.</summary>
    void ScanActions()
    {
        _actionsByControl.Clear();
        var device = CurrentDevice();
        if (device == null) return;

        var actions = new List<InputAction>();
        InputSystem.ListEnabledActions(actions);

        foreach (var action in actions)
        {
            try
            {
                foreach (var control in action.controls)
                {
                    if (control.device != device) continue;
                    if (!_actionsByControl.TryGetValue(control, out var names))
                        _actionsByControl[control] = names = new List<string>();

                    string name = action.actionMap != null ? action.actionMap.name + "/" + action.name : action.name;
                    if (!names.Contains(name)) names.Add(name);
                }
            }
            catch
            {
                // A binding that fails to resolve shouldn't take the monitor down with it.
            }
        }
    }

    // ---------------------------------------------------------------------
    // Pressed/released mapping (must match SerialToInputSystemAdapter)
    // ---------------------------------------------------------------------

    /// <summary>
    /// What the adapter makes of a raw digital value: it parses an int, treats any
    /// non-zero as HIGH, and with active-low wiring HIGH means released.
    /// Returns false if the adapter would drop the value (not an integer).
    /// </summary>
    bool TryRawToPressed(string raw, out bool pressed)
    {
        pressed = false;
        if (!int.TryParse(raw, out int value)) return false;
        bool high = value != 0;
        pressed = high != _activeLow;
        return true;
    }

    // ---------------------------------------------------------------------
    // Snapshot + diagnosis
    // ---------------------------------------------------------------------

    void BuildSnapshot()
    {
        double now = EditorApplication.timeSinceStartup;
        _rows.Clear();

        var managers = SerialHub.Managers.Where(m => m != null).ToList();
        _managerNames = managers.Select(DescribeManager).ToArray();

        bool playing = EditorApplication.isPlaying;
        bool haveManager = playing && _manager != null;
        _showTable = haveManager;

        var profile = haveManager ? _manager.deviceProfile : null;
        _firmata = profile != null && profile.transport == SerialTransport.Firmata;
        _activeLow = profile == null || profile.digitalActiveLow; // same default as the adapter
        _analogMax = profile != null && profile.analogMaxValue > 0 ? profile.analogMaxValue : 1023;

        var device = haveManager ? CurrentDevice() : null;

        if (haveManager)
        {
            string transport = profile == null ? "no profile" : _firmata ? "Firmata" : "Custom text";
            string port = PortLabel(_manager);
            string last = _lastLineTime < 0 ? "never" : (now - _lastLineTime).ToString("0.0") + "s ago";
            _statusLine = $"Port {port} @ {_manager.baudRate}   |   {transport}   |   {_linesPerSecond:0} msgs/s   |   last message {last}";
            _deviceLine = "Unity device: " + (device != null ? device.layout : "none");

            foreach (var p in _order)
            {
                while (p.RecentChanges.Count > 0 && now - p.RecentChanges.Peek() > 1.0)
                    p.RecentChanges.Dequeue();

                var row = new Row { Pin = p };
                row.Control = device?.TryGetChildControl(p.Key);

                if (row.Control is ButtonControl button)
                {
                    row.Analog = false;
                    row.UnityPressed = button.isPressed;
                }
                else if (row.Control is AxisControl axis) // ButtonControl derives from AxisControl, so this order matters
                {
                    row.Analog = true;
                    row.UnityValue = axis.ReadValue();
                }
                else
                {
                    row.Analog = p.Key.Length > 0 && char.ToUpperInvariant(p.Key[0]) == 'A';
                }

                if (row.Control != null && _actionsByControl.TryGetValue(row.Control, out var names))
                    row.Actions = string.Join(", ", names.ToArray());

                Diagnose(row, device, now);
                _rows.Add(row);
            }
        }

        BuildVerdict(now, playing, managers.Count, device, profile);
    }

    static string PortLabel(SerialManager m)
    {
        if (!string.IsNullOrEmpty(m.ActivePortName))
            return string.IsNullOrEmpty(m.portName) ? m.ActivePortName + " (auto)" : m.ActivePortName;
        return string.IsNullOrEmpty(m.portName) ? "(auto)" : m.portName;
    }

    static string DescribeManager(SerialManager m)
    {
        string port = PortLabel(m);
        string profile = m.deviceProfile != null ? m.deviceProfile.name : "no profile";
        return $"{m.name}  ({port}, {profile})";
    }

    void Diagnose(Row r, InputDevice device, double now)
    {
        var p = r.Pin;

        // ---------- Board side ----------
        if (!p.HasRaw)
        {
            r.Side = Side.Arduino;
            r.Status = "Board hasn't sent this pin";
            r.Tooltip = _firmata
                ? "Firmata reports every enabled pin's value once when it connects, so this should show up " +
                  "straight away. If it never does: the pin may have been skipped for a role conflict (check " +
                  "the Console for red [SISPro] errors), or the board isn't running StandardFirmata."
                : $"No line from the board has contained '{p.Key}:' yet. Check the sketch prints this pin, " +
                  "spelled exactly the same, in the 'D2:1;A0:512;' format.";
            p.MismatchSince = -1;
            return;
        }

        if (!r.Analog && p.RawValue != "0" && p.RawValue != "1")
        {
            r.Side = Side.Arduino;
            r.Status = $"Odd value '{p.RawValue}' (expected 0 or 1)";
            r.Tooltip = "Digital pins must be sent as 0 or 1. Unity drops values that aren't whole numbers, " +
                        "and treats any other number as HIGH (1).";
            p.MismatchSince = -1;
            return;
        }

        float rawAnalog = 0f;
        if (r.Analog && !float.TryParse(p.RawValue, out rawAnalog))
        {
            r.Side = Side.Arduino;
            r.Status = $"Odd value '{p.RawValue}' (not a number)";
            r.Tooltip = "Analog values must be plain numbers (e.g. A0:512). Unity ignores anything else.";
            p.MismatchSince = -1;
            return;
        }

        if (!r.Analog && p.RecentChanges.Count >= FlickerPerSecond)
        {
            r.Side = Side.Arduino;
            r.Status = $"Flickering ({p.RecentChanges.Count}/s) - floating pin?";
            r.Tooltip = "The value changes by itself many times a second. That almost always means the input " +
                        "isn't connected to anything solid: a loose wire, a missing ground, or a switch with no " +
                        "pull-up/pull-down. In a custom sketch, use pinMode(pin, INPUT_PULLUP).";
            return;
        }

        // ---------- Unity side ----------
        if (device == null)
        {
            r.Side = Side.Unity;
            r.Status = "No Input System device";
            r.Tooltip = "The board is sending data but there's no Input System device to feed. Check the " +
                        "SerialManager's GameObject has a SerialToInputSystemAdapter, the Console for " +
                        "'Layout ... not found', and that the SerialManager has a profile assigned.";
            p.MismatchSince = -1;
            return;
        }

        if (r.Control == null)
        {
            r.Side = Side.Unity;
            r.Status = $"No control '{p.Key}' on the device";
            r.Tooltip = $"The board sends {p.Key}, but device layout '{device.layout}' has no control by that name, " +
                        "so Unity throws the value away. The profile doesn't match the board: re-run Set Up Arduino, " +
                        "or add this pin to the profile.";
            p.MismatchSince = -1;
            return;
        }

        bool mismatch;
        string boardSays, unitySays;
        if (r.Analog)
        {
            float expected = Mathf.Clamp01(rawAnalog / _analogMax); // same normalisation as the adapter
            mismatch = Mathf.Abs(expected - r.UnityValue) > AnalogTolerance;
            boardSays = expected.ToString("0.00");
            unitySays = r.UnityValue.ToString("0.00");
        }
        else
        {
            // Compare against what the adapter should have produced, not the raw bit:
            // with pull-up wiring an idle pin reads 1 and must show as released.
            TryRawToPressed(p.RawValue, out bool expectedPressed);
            mismatch = expectedPressed != r.UnityPressed;
            boardSays = p.RawValue + (expectedPressed ? " (pressed)" : " (released)");
            unitySays = r.UnityPressed ? "pressed" : "released";
        }

        if (mismatch)
        {
            if (p.MismatchSince < 0) p.MismatchSince = now;
            if (now - p.MismatchSince > MismatchGraceSeconds)
            {
                r.Side = Side.Unity;
                r.Status = $"Board says {boardSays}, Unity says {unitySays}";
                r.Tooltip = "The value arrived in Unity but the device control didn't follow it. The device may be " +
                            "disabled, or its layout's state format doesn't match what the adapter writes.";
                return;
            }
        }
        else
        {
            p.MismatchSince = -1;
        }

        // ---------- Working ----------
        if (!p.Expected)
        {
            r.Side = Side.Info;
            r.Status = "Works, but not listed in the profile";
            r.Tooltip = "The board sends this pin and the device accepts it, but the profile doesn't declare it.";
        }
        else if (string.IsNullOrEmpty(r.Actions))
        {
            r.Side = Side.Info;
            r.Status = "Works - no enabled action uses it";
            r.Tooltip = "Board to Unity is fine. Nothing in the scene is listening, though: no enabled Input " +
                        "Action is bound to this control. If a script should react, check its " +
                        "InputActionReference and that the action gets enabled.";
        }
        else
        {
            r.Side = Side.Ok;
            r.Status = "Works -> " + r.Actions;
            r.Tooltip = "Board, device and actions all agree. Used by: " + r.Actions;
        }
    }

    void BuildVerdict(double now, bool playing, int managerCount, InputDevice device, SerialDeviceProfile profile)
    {
        if (!playing)
        {
            _verdict = "Enter Play Mode to see live input. The monitor listens to the SerialManager in your scene - " +
                       "it never opens the port itself, so it can't interfere with it.";
            _verdictType = MessageType.Info;
            return;
        }

        if (managerCount == 0 || _manager == null)
        {
            _verdict = "No SerialManager in the scene. Add one (with its SerialToInputSystemAdapter) and assign a profile.";
            _verdictType = MessageType.Warning;
            return;
        }

        if (!_manager.serial_connected)
        {
            _verdict = "The serial port isn't open, so nothing can reach Unity yet. Check: the USB cable is plugged in " +
                       "(and carries data), the right port is selected, the Arduino IDE's Serial Monitor is closed, and " +
                       "the Set Up Arduino window isn't still connected.";
            _verdictType = MessageType.Warning;
            return;
        }

        if (_lastLineTime < 0)
        {
            if (now - _attachedAt < NoDataWarnSeconds)
            {
                _verdict = "Port open - waiting for the first message from the board...";
                _verdictType = MessageType.Info;
            }
            else
            {
                _verdict = _firmata
                    ? "ARDUINO SIDE: the port is open but the board has sent nothing. Is StandardFirmata uploaded? " +
                      "Its baud rate is 57600 - the profile must match."
                    : "ARDUINO SIDE: the port is open but the board has sent nothing. Is the sketch uploaded and " +
                      $"printing lines? Serial.begin() must match {_manager.baudRate}. 'Gibberish' warnings in the " +
                      "Console mean a baud rate mismatch.";
                _verdictType = MessageType.Error;
            }
            return;
        }

        // Custom sketches stream every loop; Firmata only streams analog continuously
        // (digital is sent on change), so silence is only suspicious in those cases.
        bool streams = !_firmata || (profile != null && profile.analogChannels.Count > 0);
        if (streams && now - _lastLineTime > NoDataWarnSeconds)
        {
            _verdict = $"ARDUINO SIDE: the board was sending but has gone quiet for {now - _lastLineTime:0}s. " +
                       "It may have reset, crashed, or been unplugged.";
            _verdictType = MessageType.Error;
            return;
        }

        int arduino = _rows.Count(r => r.Side == Side.Arduino);
        int unity = _rows.Count(r => r.Side == Side.Unity);

        if (arduino > 0 && unity > 0)
        {
            _verdict = $"Problems on both sides: {arduino} pin(s) ARDUINO, {unity} pin(s) UNITY. " +
                       "Problem pins are highlighted - hover one for details.";
            _verdictType = MessageType.Warning;
        }
        else if (arduino > 0)
        {
            _verdict = $"ARDUINO SIDE: the board is talking, but {arduino} pin(s) look wrong (wiring, sketch or firmware). " +
                       "Unity is handling everything it receives correctly. Problem pins are highlighted - hover one for details.";
            _verdictType = MessageType.Warning;
        }
        else if (unity > 0)
        {
            _verdict = $"UNITY SIDE: the board is sending fine, but {unity} pin(s) don't reach the Input System correctly " +
                       "(profile, layout or device). Problem pins are highlighted - hover one for details.";
            _verdictType = MessageType.Warning;
        }
        else
        {
            _verdict = "Everything the board sends is reaching Unity. Press buttons / turn knobs and watch both dots change together.";
            _verdictType = MessageType.Info;
        }
    }

    // ---------------------------------------------------------------------
    // Drawing
    // ---------------------------------------------------------------------

    void OnGUI()
    {
        if (Event.current.type == EventType.Layout)
            BuildSnapshot();

        _scroll = EditorGUILayout.BeginScrollView(_scroll);

        EditorGUILayout.LabelField("Input Monitor", EditorStyles.boldLabel);
        EditorGUILayout.LabelField(
            "Each pin is shown twice: what the BOARD sent, and what UNITY's Input System sees. " +
            "If the board column changes when you press a button, the Arduino side is working.",
            EditorStyles.wordWrappedMiniLabel);
        EditorGUILayout.Space(4);

        if (_managerNames.Length > 1)
        {
            int clamped = Mathf.Clamp(_selected, 0, _managerNames.Length - 1);
            int picked = EditorGUILayout.Popup("Board", clamped, _managerNames);
            if (picked != clamped) _selected = picked; // Tick() re-attaches outside OnGUI
        }

        EditorGUILayout.HelpBox(_verdict, _verdictType);

        if (_showTable)
        {
            EditorGUILayout.LabelField(_statusLine, EditorStyles.miniLabel);
            EditorGUILayout.LabelField(_deviceLine, EditorStyles.miniLabel);

            if (_activeLow)
            {
                EditorGUILayout.Space(6);
                DrawPullupNotice();
            }

            EditorGUILayout.Space(6);
            DrawLegend();
            DrawHeader();

            double now = EditorApplication.timeSinceStartup;
            for (int i = 0; i < _rows.Count; i++)
                DrawRow(_rows[i], i, now);

            if (_rows.Count == 0)
                EditorGUILayout.LabelField("No pins yet - the profile declares none and nothing has arrived.", EditorStyles.miniLabel);

            EditorGUILayout.Space(8);
            DrawFooter();
        }

        EditorGUILayout.EndScrollView();
    }

    static GUIStyle _noticeTitleStyle;
    static GUIStyle _noticeBodyStyle;

    /// <summary>
    /// Pull-up wiring (Firmata PULLUP mode, or INPUT_PULLUP in the generated sketch)
    /// makes the raw values the opposite of what most people expect (idle = 1).
    /// Drawn as a softly tinted callout with an accent bar - noticeable, but clearly
    /// info rather than a warning. Only shown while the profile's Digital Active Low
    /// is on, which is when the adapter applies this inversion.
    /// </summary>
    void DrawPullupNotice()
    {
        if (_noticeTitleStyle == null)
        {
            _noticeTitleStyle = new GUIStyle(EditorStyles.boldLabel)
            {
                wordWrap = true,
                padding = new RectOffset(12, 10, 6, 0)
            };

            _noticeBodyStyle = new GUIStyle(EditorStyles.wordWrappedMiniLabel)
            {
                padding = new RectOffset(12, 10, 2, 6)
            };
        }

        var title = new GUIContent("Pull-up inputs:  1 / HIGH = NOT pressed     0 / LOW = pressed");
        var body = new GUIContent(
            "The board's internal pull-ups make a pin with nothing connected read HIGH, and a button wired " +
            "between the pin and GND pulls it LOW when pressed. The profile's 'Digital Active Low' setting " +
            "tells Unity this, so an idle pin reads 1 on the board and \"released\" in Unity. Both dots show " +
            "pressed (green) / released (red), so they should always match.");

        EditorGUILayout.BeginVertical();
        var titleRect = GUILayoutUtility.GetRect(title, _noticeTitleStyle, GUILayout.ExpandWidth(true));
        var bodyRect = GUILayoutUtility.GetRect(body, _noticeBodyStyle, GUILayout.ExpandWidth(true));
        EditorGUILayout.EndVertical();

        if (Event.current.type == EventType.Repaint)
        {
            var box = new Rect(titleRect.x, titleRect.y, titleRect.width, bodyRect.yMax - titleRect.y);
            EditorGUI.DrawRect(box, NoticeBackColor);
            EditorGUI.DrawRect(new Rect(box.x, box.y, 3, box.height), NoticeEdgeColor);
        }

        GUI.Label(titleRect, title, _noticeTitleStyle);
        GUI.Label(bodyRect, body, _noticeBodyStyle);
    }

    void DrawLegend()
    {
        var rect = GUILayoutUtility.GetRect(0, 16, GUILayout.ExpandWidth(true));
        float x = rect.x;
        x = LegendItem(rect, x, OnColor, "pressed");
        x = LegendItem(rect, x, OffColor, "released");
        LegendItem(rect, x, NoDataColor, "no data");
    }

    float LegendItem(Rect row, float x, Color c, string text)
    {
        DrawDot(new Rect(x, row.y + 3, 10, 10), c);
        var size = EditorStyles.miniLabel.CalcSize(new GUIContent(text));
        GUI.Label(new Rect(x + 13, row.y, size.x, row.height), text, EditorStyles.miniLabel);
        return x + 13 + size.x + 10;
    }

    void DrawHeader()
    {
        var rect = GUILayoutUtility.GetRect(0, 18, GUILayout.ExpandWidth(true));
        float x = rect.x + 4;
        GUI.Label(new Rect(x, rect.y, PinColWidth, rect.height), "Pin", EditorStyles.boldLabel);
        x += PinColWidth;
        GUI.Label(new Rect(x, rect.y, BoardColWidth, rect.height),
            new GUIContent("Board", "Raw value exactly as it came off the serial port."), EditorStyles.boldLabel);
        x += BoardColWidth;
        GUI.Label(new Rect(x, rect.y, UnityColWidth, rect.height),
            new GUIContent("Unity", "Value of the matching control on the Input System device."), EditorStyles.boldLabel);
    }

    static GUIStyle _problemPinStyle;

    void DrawRow(Row r, int index, double now)
    {
        var rect = GUILayoutUtility.GetRect(0, RowHeight, GUILayout.ExpandWidth(true));
        var p = r.Pin;
        bool problem = r.Side == Side.Arduino || r.Side == Side.Unity;

        if (index % 2 == 1) EditorGUI.DrawRect(rect, StripeColor);
        if (!r.Analog && p.HasRaw && now - p.LastChanged < 0.3) EditorGUI.DrawRect(rect, FlashColor);

        float x = rect.x + 4;
        float midY = rect.y + (rect.height - 10) * 0.5f;

        // Pin - tinted when this row has a problem, so it's findable without a status column.
        GUIStyle pinStyle;
        if (problem)
        {
            if (_problemPinStyle == null)
            {
                _problemPinStyle = new GUIStyle(EditorStyles.boldLabel);
                _problemPinStyle.normal.textColor = ProblemColor;
            }
            pinStyle = _problemPinStyle;
        }
        else
        {
            pinStyle = p.Expected ? EditorStyles.label : EditorStyles.miniLabel;
        }
        GUI.Label(new Rect(x, rect.y, PinColWidth, rect.height), p.Key, pinStyle);
        x += PinColWidth;

        // Board
        var boardRect = new Rect(x, rect.y, BoardColWidth - 8, rect.height);
        if (!p.HasRaw)
        {
            DrawDot(new Rect(x, midY, 10, 10), NoDataColor);
            GUI.Label(new Rect(x + 16, rect.y, boardRect.width - 16, rect.height), "-", EditorStyles.miniLabel);
        }
        else if (r.Analog)
        {
            float.TryParse(p.RawValue, out float raw);
            DrawBar(new Rect(x, rect.y + 5, 60, rect.height - 10), Mathf.Clamp01(raw / _analogMax));
            GUI.Label(new Rect(x + 66, rect.y, boardRect.width - 66, rect.height), p.RawValue, EditorStyles.miniLabel);
        }
        else
        {
            // Dot shows the logical state the adapter derives (same meaning as the
            // Unity dot); the text shows the raw electrical level.
            bool parsed = TryRawToPressed(p.RawValue, out bool pressed);
            DrawDot(new Rect(x, midY, 10, 10), !parsed ? NoDataColor : pressed ? OnColor : OffColor);

            string text;
            if (p.RawValue == "1") text = _activeLow ? "1  HIGH  (not pressed)" : "1  HIGH  (pressed)";
            else if (p.RawValue == "0") text = _activeLow ? "0  LOW  (pressed)" : "0  LOW  (not pressed)";
            else text = p.RawValue;

            GUI.Label(new Rect(x + 16, rect.y, boardRect.width - 16, rect.height), text, EditorStyles.miniLabel);
        }
        x += BoardColWidth;

        // Unity
        if (r.Control == null)
        {
            DrawDot(new Rect(x, midY, 10, 10), NoDataColor);
            GUI.Label(new Rect(x + 16, rect.y, UnityColWidth - 24, rect.height), "-", EditorStyles.miniLabel);
        }
        else if (r.Analog)
        {
            DrawBar(new Rect(x, rect.y + 5, 60, rect.height - 10), r.UnityValue);
            GUI.Label(new Rect(x + 66, rect.y, UnityColWidth - 74, rect.height), r.UnityValue.ToString("0.00"), EditorStyles.miniLabel);
        }
        else
        {
            DrawDot(new Rect(x, midY, 10, 10), r.UnityPressed ? OnColor : OffColor);
            GUI.Label(new Rect(x + 16, rect.y, UnityColWidth - 24, rect.height),
                r.UnityPressed ? "pressed" : "released", EditorStyles.miniLabel);
        }

        // Whole-row tooltip: the diagnosis that used to live in the Status column.
        string tip = r.Status + "\n\n" + r.Tooltip;
        if (p.HasRaw) tip += $"\n\nChanged {p.ChangeCount} time(s) since this window started watching.";
        GUI.Label(rect, new GUIContent(string.Empty, tip), GUIStyle.none);
    }

    void DrawFooter()
    {
        _showLastLine = EditorGUILayout.Foldout(_showLastLine, "Last raw message from the board", true);
        if (_showLastLine)
        {
            string text = string.IsNullOrEmpty(_lastLine) ? "(nothing yet)" : _lastLine;
            if (text.Length > 500) text = text.Substring(0, 500) + " ...";
            EditorGUILayout.SelectableLabel(text, EditorStyles.textField, GUILayout.Height(EditorGUIUtility.singleLineHeight * 2));
        }

        EditorGUILayout.Space(4);
        if (GUILayout.Button("Clear Counters", GUILayout.Width(130)))
            ClearCounters();
    }

    static void DrawDot(Rect r, Color c)
    {
        if (Event.current.type != EventType.Repaint) return;
        var prev = Handles.color;
        Handles.color = c;
        Handles.DrawSolidDisc(r.center, Vector3.forward, Mathf.Min(r.width, r.height) * 0.5f);
        Handles.color = prev;
    }

    static void DrawBar(Rect r, float t)
    {
        EditorGUI.DrawRect(r, BarBackColor);
        var fill = new Rect(r.x, r.y, r.width * Mathf.Clamp01(t), r.height);
        EditorGUI.DrawRect(fill, OnColor);
    }
}
}
#endif