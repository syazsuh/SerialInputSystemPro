#if UNITY_STANDALONE_WIN || UNITY_STANDALONE_OSX || UNITY_STANDALONE_LINUX || UNITY_EDITOR
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

namespace SISPro
{
/// <summary>
/// Drop-in demo for the SISPro sample scene. Add it to the SerialManager's GameObject
/// (or anywhere in the scene), press Play, and it builds everything at runtime from
/// whichever SerialDeviceProfile the SerialManager has:
///
///   - a live status line that says what's wrong when it isn't working
///   - one row per input pin, read through Input Actions (the same way a game would),
///     so a lit row proves the whole chain: board -> serial -> device -> action
///   - a cube that turns with the first analog input and lights up on any button
///   - toggles/sliders for any output pins on the profile (Firmata only)
///   - a short getting-started panel, which folds away on the first button press
///
/// Nothing is laid out by hand, so it works for any board and never falls out of sync
/// with the profile. Uses IMGUI so it needs no Canvas, EventSystem or TextMeshPro setup,
/// and works with the Input System as the only active input handler.
/// </summary>
[AddComponentMenu("SISPro/SISPro Demo")]
public class SISProDemo : MonoBehaviour
{
    [Tooltip("Leave empty to use the SerialManager on this GameObject, or the first one in the scene.")]
    public SerialManager serialManager;

    [Header("Scene")]
    [Tooltip("Spawns a cube that rotates with the first analog input and lights up while any button is held.")]
    public bool spawnCube = true;

    [Header("Panel")]
    public bool showInstructions = true;
    [Tooltip("0 = scale automatically with screen height.")]
    public float uiScale = 0f;

    const float PanelWidth = 360f;
    const float NoDataHintSeconds = 5f;

    class DigitalPin { public int Pin; public InputAction Action; }
    class AnalogPin { public int Channel; public InputAction Action; }

    // --- input ---
    InputActionMap _map;
    readonly List<DigitalPin> _digital = new List<DigitalPin>();
    readonly List<AnalogPin> _analog = new List<AnalogPin>();
    SerialDeviceProfile _builtFor;
    string _builtLayout;
    SerialToInputSystemAdapter _adapter;

    // --- connection tracking ---
    bool _wasConnected;
    float _connectedAt = -1f;
    float _lastLineTime = -1f;
    bool _instructionsCollapsed;

    // --- outputs (last value sent, so we only write on change) ---
    readonly Dictionary<int, bool> _digitalOut = new Dictionary<int, bool>();
    readonly Dictionary<int, int> _pwmOut = new Dictionary<int, int>();
    readonly Dictionary<int, int> _servoOut = new Dictionary<int, int>();

    // --- cube ---
    GameObject _cube;
    Renderer _cubeRenderer;
    float _cubeAngle;

    // --- GUI ---
    Vector2 _scroll;
    GUIStyle _panel, _title, _status, _hint, _label, _small, _dot, _button;
    Texture2D _panelTex;

    static readonly Color Green = new Color(0.35f, 0.85f, 0.45f);
    static readonly Color Red = new Color(0.90f, 0.35f, 0.35f);
    static readonly Color Amber = new Color(1.00f, 0.75f, 0.30f);
    static readonly Color Grey = new Color(0.55f, 0.55f, 0.55f);

    // ---------------------------------------------------------------------
    // Lifecycle
    // ---------------------------------------------------------------------

    void OnEnable()
    {
        if (serialManager == null) serialManager = GetComponent<SerialManager>();
        if (serialManager == null) serialManager = FindAnyObjectByType<SerialManager>();

        if (serialManager != null)
        {
            serialManager.OnLineReceived.AddListener(OnLine);
            _adapter = serialManager.GetComponent<SerialToInputSystemAdapter>();
        }

        _instructionsCollapsed = !showInstructions;
        RebuildActions();
    }

    void Start()
    {
        if (spawnCube) SpawnCube();
    }

    void OnDisable()
    {
        if (serialManager != null)
            serialManager.OnLineReceived.RemoveListener(OnLine);

        DisposeActions();

        if (_cube != null) Destroy(_cube);
        if (_panelTex != null) Destroy(_panelTex);
    }

    void OnLine(string _) => _lastLineTime = Time.unscaledTime;

    void Update()
    {
        if (serialManager == null) return;

        var profile = serialManager.deviceProfile;
        if (profile != _builtFor || (profile != null && profile.layoutName != _builtLayout))
            RebuildActions();

        bool connected = serialManager.serial_connected;
        if (connected && !_wasConnected)
        {
            _connectedAt = Time.unscaledTime;
            _lastLineTime = -1f;
            // SerialManager drives outputs LOW on disconnect and the board resets on
            // connect, so every output starts from off/0 again.
            _digitalOut.Clear();
            _pwmOut.Clear();
            _servoOut.Clear();
        }
        _wasConnected = connected;

        bool anyPressed = false;
        foreach (var d in _digital)
            if (d.Action.IsPressed()) { anyPressed = true; break; }

        if (anyPressed) _instructionsCollapsed = true; // they've got it working

        UpdateCube(connected, anyPressed);
    }

    // ---------------------------------------------------------------------
    // Input Actions, built from the profile
    // ---------------------------------------------------------------------

    void RebuildActions()
    {
        DisposeActions();

        var profile = serialManager != null ? serialManager.deviceProfile : null;
        _builtFor = profile;
        _builtLayout = profile != null ? profile.layoutName : null;
        if (profile == null || string.IsNullOrEmpty(profile.layoutName)) return;

        // Same pin naming as SerialDeviceLayoutRegistry / SISProInputActionsBuilder:
        // explicit pin lists when present, legacy contiguous counts otherwise.
        var digitalPins = new List<int>();
        var analogChannels = new List<int>();
        if (profile.HasDiscoveredPins)
        {
            digitalPins.AddRange(profile.digitalPins);
            analogChannels.AddRange(profile.analogChannels);
        }
        else
        {
            for (int i = 0; i < profile.digitalCount; i++) digitalPins.Add(i);
            for (int i = 0; i < profile.analogCount; i++) analogChannels.Add(i);
        }

        _map = new InputActionMap("SISPro Demo");
        string layout = profile.layoutName;

        foreach (var pin in digitalPins)
        {
            var action = _map.AddAction("D" + pin, InputActionType.Button, $"<{layout}>/D{pin}");
            _digital.Add(new DigitalPin { Pin = pin, Action = action });
        }

        foreach (var channel in analogChannels)
        {
            // PassThrough: report every value, including 0, with no press threshold.
            var action = _map.AddAction("A" + channel, InputActionType.PassThrough, $"<{layout}>/A{channel}");
            _analog.Add(new AnalogPin { Channel = channel, Action = action });
        }

        _map.Enable(); // bindings resolve by themselves once the adapter adds the device
    }

    void DisposeActions()
    {
        if (_map != null)
        {
            _map.Disable();
            _map.Dispose();
            _map = null;
        }
        _digital.Clear();
        _analog.Clear();
    }

    InputDevice CurrentDevice()
    {
        var device = _adapter != null ? _adapter.targetDevice : null;
        return device != null && device.added ? device : null;
    }

    // ---------------------------------------------------------------------
    // Cube
    // ---------------------------------------------------------------------

    void SpawnCube()
    {
        var cam = Camera.main;
        _cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
        _cube.name = "SISPro Demo Cube";

        if (cam != null)
        {
            // Right of centre, so the panel on the left doesn't cover it.
            _cube.transform.position = cam.ViewportToWorldPoint(new Vector3(0.65f, 0.5f, 5f));
            _cube.transform.rotation = Quaternion.LookRotation(cam.transform.forward, Vector3.up);
        }
        else
        {
            _cube.transform.position = new Vector3(1.5f, 1f, 5f);
        }

        _cube.transform.localScale = Vector3.one * 1.2f;
        _cubeRenderer = _cube.GetComponent<Renderer>();
    }

    void UpdateCube(bool connected, bool anyPressed)
    {
        if (_cube == null) return;

        if (_analog.Count > 0 && connected)
        {
            // First analog input sets the angle: 0 -> 0deg, full scale -> 360deg.
            float target = Mathf.Clamp01(_analog[0].Action.ReadValue<float>()) * 360f;
            _cubeAngle = Mathf.LerpAngle(_cubeAngle, target, 1f - Mathf.Exp(-12f * Time.deltaTime));
        }
        else
        {
            _cubeAngle += 25f * Time.deltaTime; // idle spin: no analog input to follow
        }

        var cam = Camera.main;
        Vector3 axis = cam != null ? cam.transform.up : Vector3.up;
        Vector3 fwd = cam != null ? cam.transform.forward : Vector3.forward;
        _cube.transform.rotation = Quaternion.AngleAxis(_cubeAngle, axis) *
                                   Quaternion.LookRotation(fwd, axis) *
                                   Quaternion.Euler(20f, 0f, 0f);

        float scale = anyPressed ? 1.4f : 1.2f;
        _cube.transform.localScale = Vector3.Lerp(_cube.transform.localScale, Vector3.one * scale,
                                                  1f - Mathf.Exp(-15f * Time.deltaTime));

        if (_cubeRenderer != null)
        {
            Color c = !connected ? new Color(0.35f, 0.35f, 0.38f)
                    : anyPressed ? Green
                    : new Color(0.75f, 0.78f, 0.85f);
            _cubeRenderer.material.color = c; // material.color maps to the pipeline's main color (_Color / _BaseColor)
        }
    }

    // ---------------------------------------------------------------------
    // Status
    // ---------------------------------------------------------------------

    void GetStatus(out string text, out Color color, out string hint)
    {
        hint = null;

        if (serialManager == null)
        {
            text = "No SerialManager in the scene";
            color = Red;
            hint = "Add a GameObject with SerialManager and SerialToInputSystemAdapter.";
            return;
        }

        var profile = serialManager.deviceProfile;
        bool firmata = profile != null && profile.transport == SerialTransport.Firmata;
        string port = serialManager.ActivePortName ?? (string.IsNullOrEmpty(serialManager.portName) ? "auto" : serialManager.portName);

        if (profile == null)
        {
            text = "No device profile assigned";
            color = Red;
            hint = "Tools > SISPro > Set Up Arduino > Find My Arduino > Create Profile, then drag the profile " +
                   "onto the SerialManager's Device Profile field.";
            return;
        }

        if (!serialManager.serial_connected)
        {
            text = "Not connected";
            color = Red;
            hint = "Plug the board in and click Connect. If it still fails, close the Arduino IDE's Serial " +
                   "Monitor and the Set Up Arduino window - only one program can use the port at a time.";
            return;
        }

        if (firmata && !serialManager.IsFirmataReady)
        {
            text = $"Waiting for the board on {port}...";
            color = Amber;
            hint = "The board restarts when the port opens. This takes 2-3 seconds.";
            return;
        }

        if (CurrentDevice() == null)
        {
            text = "Connected - but no Input System device";
            color = Amber;
            hint = "Add a SerialToInputSystemAdapter to the SerialManager's GameObject, and check the Console " +
                   "for 'Layout ... not found'.";
            return;
        }

        if (_lastLineTime < 0f && Time.unscaledTime - _connectedAt > NoDataHintSeconds)
        {
            text = $"Connected on {port} - no data yet";
            color = Amber;
            hint = firmata
                ? "Is StandardFirmata uploaded, and is the profile's baud rate 57600? Try pressing a button."
                : $"Is the sketch uploaded, and does its Serial.begin() use {serialManager.baudRate}?";
            return;
        }

        text = $"Connected on {port} ({(firmata ? "Firmata" : "custom sketch")})";
        color = Green;
    }

    // ---------------------------------------------------------------------
    // GUI
    // ---------------------------------------------------------------------

    void EnsureStyles()
    {
        if (_panel != null) return;

        _panelTex = new Texture2D(1, 1);
        _panelTex.SetPixel(0, 0, new Color(0.08f, 0.09f, 0.11f, 0.88f));
        _panelTex.Apply();

        _panel = new GUIStyle(GUI.skin.box)
        {
            padding = new RectOffset(14, 14, 12, 12),
            normal = { background = _panelTex }
        };
        _title = new GUIStyle(GUI.skin.label) { fontSize = 18, fontStyle = FontStyle.Bold, normal = { textColor = Color.white } };
        _status = new GUIStyle(GUI.skin.label) { fontSize = 14, fontStyle = FontStyle.Bold, wordWrap = true };
        _hint = new GUIStyle(GUI.skin.label) { fontSize = 12, wordWrap = true, normal = { textColor = new Color(0.85f, 0.85f, 0.85f) } };
        _label = new GUIStyle(GUI.skin.label) { fontSize = 13, normal = { textColor = Color.white } };
        _small = new GUIStyle(GUI.skin.label) { fontSize = 11, wordWrap = true, normal = { textColor = new Color(0.7f, 0.7f, 0.7f) } };
        _dot = new GUIStyle(GUI.skin.label) { fontSize = 16, alignment = TextAnchor.MiddleCenter };
        _button = new GUIStyle(GUI.skin.button) { fontSize = 13 };
    }

    void OnGUI()
    {
        EnsureStyles();

        float scale = uiScale > 0f ? uiScale : Mathf.Clamp(Screen.height / 800f, 1f, 2.5f);
        GUI.matrix = Matrix4x4.Scale(new Vector3(scale, scale, 1f));
        float height = Screen.height / scale - 24f;

        GUILayout.BeginArea(new Rect(12f, 12f, PanelWidth, height), _panel);
        _scroll = GUILayout.BeginScrollView(_scroll);

        GUILayout.Label("SISPro Demo", _title);
        GUILayout.Space(6);

        DrawStatus();
        DrawInstructions();
        DrawInputs();
        DrawOutputs();

        GUILayout.EndScrollView();
        GUILayout.EndArea();
    }

    void DrawStatus()
    {
        GetStatus(out string text, out Color color, out string hint);

        var prev = _status.normal.textColor;
        _status.normal.textColor = color;
        GUILayout.Label("● " + text, _status);
        _status.normal.textColor = prev;

        if (hint != null) GUILayout.Label(hint, _hint);

        if (serialManager != null)
        {
            GUILayout.Space(4);
            string buttonText = serialManager.serial_connected ? "Disconnect" : "Connect";
            if (GUILayout.Button(buttonText, _button, GUILayout.Height(26)))
            {
                if (serialManager.serial_connected) serialManager.StopSerial();
                else serialManager.StartSerial();
            }
        }

        GUILayout.Space(10);
    }

    void DrawInstructions()
    {
        string header = _instructionsCollapsed ? "▶ Getting started" : "▼ Getting started";
        if (GUILayout.Button(header, _label)) _instructionsCollapsed = !_instructionsCollapsed;
        if (_instructionsCollapsed) { GUILayout.Space(8); return; }

        GUILayout.Label(
            "1. Upload StandardFirmata to the board once\n" +
            "    (Arduino IDE: File > Examples > Firmata > StandardFirmata).\n" +
            "2. In Unity: Tools > SISPro > Set Up Arduino >\n" +
            "    Find My Arduino > Create Profile.\n" +
            "3. Drag the new profile onto the SerialManager's\n" +
            "    Device Profile field, then press Play.\n" +
            "4. Press a button or turn a knob - its row lights up\n" +
            "    below and the cube reacts.\n\n" +
            "Something not working? Tools > SISPro > Input Monitor shows whether " +
            "the problem is on the Arduino side or the Unity side.",
            _hint);
        GUILayout.Space(10);
    }

    void DrawInputs()
    {
        if (_digital.Count == 0 && _analog.Count == 0)
        {
            if (serialManager != null && serialManager.deviceProfile != null)
                GUILayout.Label("This profile has no input pins.", _small);
            return;
        }

        GUILayout.Label("Inputs (read through Input Actions)", _label);
        GUILayout.Space(2);

        foreach (var d in _digital)
        {
            bool bound = d.Action.controls.Count > 0;
            bool pressed = bound && d.Action.IsPressed();

            GUILayout.BeginHorizontal();
            DrawDot(!bound ? Grey : pressed ? Green : new Color(0.35f, 0.35f, 0.38f));
            GUILayout.Label("D" + d.Pin, _label, GUILayout.Width(44));
            GUILayout.Label(!bound ? "not bound" : pressed ? "pressed" : "released", _small);
            GUILayout.EndHorizontal();
        }

        foreach (var a in _analog)
        {
            bool bound = a.Action.controls.Count > 0;
            float value = bound ? Mathf.Clamp01(a.Action.ReadValue<float>()) : 0f;

            GUILayout.BeginHorizontal();
            DrawDot(bound ? Green : Grey);
            GUILayout.Label("A" + a.Channel, _label, GUILayout.Width(44));
            Rect bar = GUILayoutUtility.GetRect(150f, 12f, GUILayout.ExpandWidth(true));
            bar.y += 5f;
            DrawBar(bar, value, bound);
            GUILayout.Label(bound ? value.ToString("0.00") : "-", _small, GUILayout.Width(36));
            GUILayout.EndHorizontal();
        }

        if (_analog.Count > 0)
            GUILayout.Label($"The cube follows A{_analog[0].Channel}.", _small);

        GUILayout.Label("\"not bound\" means the Input System has no matching control yet - check the status above.", _small);
        GUILayout.Space(10);
    }

    void DrawOutputs()
    {
        var profile = serialManager != null ? serialManager.deviceProfile : null;
        if (profile == null) return;

        bool firmata = profile.transport == SerialTransport.Firmata;
        if (!firmata) return; // outputs are Firmata-only

        GUILayout.Label("Outputs", _label);

        if (!profile.HasOutputPins)
        {
            GUILayout.Label("No output pins on this profile. To try one: in Set Up Arduino, set pin 13 to " +
                            "'Digital Out' (it's the LED on the board), click Write Pins Into Profile, then " +
                            "reconnect.", _small);
            return;
        }

        bool ready = serialManager.serial_connected && serialManager.IsFirmataReady;
        if (!ready) GUILayout.Label("Available once the board is connected.", _small);

        GUI.enabled = ready;

        foreach (var pin in profile.digitalOutputPins)
        {
            _digitalOut.TryGetValue(pin, out bool on);
            bool newOn = GUILayout.Toggle(on, $"  D{pin}  {(on ? "ON" : "off")}" + (pin == 13 ? "  (board LED)" : ""));
            if (ready && newOn != on)
            {
                _digitalOut[pin] = newOn;
                serialManager.SetDigitalOutput(pin, newOn);
            }
        }

        foreach (var pin in profile.pwmOutputPins)
            DrawSliderOutput("PWM", pin, 255, _pwmOut, ready, v => serialManager.SetPwmOutput(pin, v));

        foreach (var pin in profile.servoOutputPins)
            DrawSliderOutput("Servo", pin, 180, _servoOut, ready, v => serialManager.SetServoOutput(pin, v));

        GUI.enabled = true;
    }

    void DrawSliderOutput(string kind, int pin, int max, Dictionary<int, int> state, bool ready, System.Action<int> send)
    {
        state.TryGetValue(pin, out int current);

        GUILayout.BeginHorizontal();
        GUILayout.Label($"{kind} D{pin}", _small, GUILayout.Width(72));
        int value = Mathf.RoundToInt(GUILayout.HorizontalSlider(current, 0, max, GUILayout.ExpandWidth(true)));
        GUILayout.Label(kind == "Servo" ? value + "°" : value.ToString(), _small, GUILayout.Width(36));
        GUILayout.EndHorizontal();

        if (ready && value != current)
        {
            state[pin] = value;
            send(value);
        }
    }

    void DrawDot(Color c)
    {
        var prev = _dot.normal.textColor;
        _dot.normal.textColor = c;
        GUILayout.Label("●", _dot, GUILayout.Width(20));
        _dot.normal.textColor = prev;
    }

    void DrawBar(Rect r, float t, bool active)
    {
        if (Event.current.type != EventType.Repaint) return;
        var prev = GUI.color;
        GUI.color = new Color(1f, 1f, 1f, 0.12f);
        GUI.DrawTexture(r, Texture2D.whiteTexture);
        GUI.color = active ? Green : Grey;
        GUI.DrawTexture(new Rect(r.x, r.y, r.width * t, r.height), Texture2D.whiteTexture);
        GUI.color = prev;
    }
}
}
#endif
