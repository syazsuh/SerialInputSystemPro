#if UNITY_STANDALONE_WIN || UNITY_STANDALONE_OSX || UNITY_STANDALONE_LINUX || UNITY_EDITOR
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;
using UnityEngine.InputSystem.Layouts;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace SISPro
{
[RequireComponent(typeof(SerialManager))]
public class SerialToInputSystemAdapter : MonoBehaviour
{
    public SerialManager serialManager;
    public InputDevice targetDevice;

    [Header("Debugging")]
    [Tooltip("Logs a warning the first time an incoming key doesn't match any control on the " +
             "device (typo in firmware, protocol drift, wrong profile assigned, etc). Each " +
             "distinct key is only logged once per connection, so a board stuck sending a bad " +
             "key doesn't spam the console every frame.")]
    public bool warnOnUnmatchedKeys = true;

    /// <summary>
    /// Fired every time an incoming key has no matching control on the target device -
    /// every occurrence, not deduped, so a UI (e.g. SerialMonitorUI) can show a live
    /// "unmatched keys" panel or counter rather than relying on the console.
    /// </summary>
    public event Action<string> OnUnmatchedKey;

    readonly HashSet<string> _warnedUnmatchedKeys = new HashSet<string>();

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    static void RegisterKnownLayouts()
    {
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            Type[] types;
            try { types = assembly.GetTypes(); }
            catch (ReflectionTypeLoadException ex) { types = ex.Types; }

            foreach (var type in types)
            {
                if (type == null || !typeof(InputDevice).IsAssignableFrom(type)) continue;
                if (!type.Name.EndsWith("InputDevice")) continue;

                string layoutName = type.Name;
                if (!InputSystem.ListLayouts().Contains(layoutName))
                {
                    InputSystem.RegisterLayout(type, layoutName);
                    Debug.Log($"✅ Registered generated layout: {layoutName}");
                }
            }
        }
    }

    void OnEnable()
    {
        if (serialManager == null)
            serialManager = GetComponent<SerialManager>();

        if (serialManager != null)
            serialManager.OnLineReceived.AddListener(OnSerialLineReceived);

        RegisterDevice();
    }

    void OnDisable()
    {
        if (serialManager != null)
            serialManager.OnLineReceived.RemoveListener(OnSerialLineReceived);

        if (targetDevice != null && InputSystem.devices.Contains(targetDevice))
        {
            InputSystem.RemoveDevice(targetDevice);
            targetDevice = null;
            Debug.Log("🛑 Removed device from Input System.");
        }
    }

    void RegisterDevice()
    {
        if (serialManager == null || serialManager.deviceProfile == null)
        {
            Debug.LogWarning("⚠️ Cannot register device: Missing SerialManager or device profile.");
            return;
        }

        // Fresh device (or profile reassignment) means past mismatches are no longer
        // relevant - don't let a stale key from a previous board suppress a real warning.
        _warnedUnmatchedKeys.Clear();

        // Defensive: makes sure a profile populated via the Capability Explorer has its
        // dynamic layout registered even if this is a fresh Play session or a player
        // build where the Editor's domain-reload scan never ran. Cheap no-op for
        // legacy (non-discovered) profiles and safe to call every time.
        SerialDeviceLayoutRegistry.EnsureRegistered(serialManager.deviceProfile);

        string layoutName = serialManager.deviceProfile.layoutName;
        if (string.IsNullOrWhiteSpace(layoutName))
        {
            Debug.LogWarning("⚠️ Device profile layout name is empty.");
            return;
        }

        if (!InputSystem.ListLayouts().Contains(layoutName))
        {
            Debug.LogWarning($"⚠️ Layout '{layoutName}' not found in registered Input System layouts.");
            return;
        }

        try
        {
            var existingDevice = InputSystem.GetDevice(layoutName);
            if (existingDevice == null)
            {
                targetDevice = InputSystem.AddDevice(layoutName);
                Debug.Log($"➕ Added device to Input System: {layoutName}");
            }
            else
            {
                targetDevice = existingDevice;
                Debug.Log($"🎮 Reusing existing device: {layoutName}");
            }
        }
        catch (Exception ex)
        {
            Debug.LogError($"❌ Failed to add device: {ex.Message}");
        }
    }

    void OnSerialLineReceived(string line)
    {
        if (targetDevice == null) return;

        var profile = serialManager.deviceProfile;

        // 10-bit (1023) is only correct for classic AVR boards (Uno/Mega/Nano).
        // ESP32/Due-class boards use a 12-bit ADC (4095), which silently produced
        // half-scale values before this used the profile's discovered value.
        int analogMax = (profile != null && profile.analogMaxValue > 0)
            ? profile.analogMaxValue
            : 1023;

        // Both transports wire inputs with the internal pull-up (Firmata: PULLUP mode,
        // generated sketch: INPUT_PULLUP), so an unpressed button reads 1 and a pressed
        // one reads 0. Without this, every button reports "held" at rest.
        bool digitalActiveLow = profile == null || profile.digitalActiveLow;

        string[] segments = line.Split(';');
        foreach (var segment in segments)
        {
            if (string.IsNullOrWhiteSpace(segment)) continue;

            var kv = segment.Split(':');
            if (kv.Length != 2) continue;

            string key = kv[0].Trim();
            string value = kv[1].Trim();

            var control = targetDevice.TryGetChildControl(key);
            if (control == null)
            {
                OnUnmatchedKey?.Invoke(key);

                if (warnOnUnmatchedKeys && _warnedUnmatchedKeys.Add(key))
                {
                    // _warnedUnmatchedKeys.Add returns false if key was already present,
                    // so this branch (and the log) only runs the first time we see it.
                    Debug.LogWarning(
                        $"⚠️ Unmatched input key '{key}' on device '{targetDevice.layout}' - " +
                        "no control by that name. Check the firmware protocol against the " +
                        "assigned SerialDeviceProfile (typo, wrong pin, or stale profile).");
                }

                continue;
            }

            try
            {
                switch (control)
                {
                    case ButtonControl button when int.TryParse(value, out int rawDigital):
                        bool pinHigh = rawDigital != 0;
                        bool pressed = pinHigh != digitalActiveLow;
                        InputSystem.QueueDeltaStateEvent(button, pressed ? (byte)1 : (byte)0);
                        break;

                    case AxisControl axis when float.TryParse(value, out float floatVal):
                        float normalized = Mathf.Clamp01(floatVal / analogMax);
                        InputSystem.QueueDeltaStateEvent(axis, normalized);
                        break;

                    case ButtonControl _:
                    case AxisControl _:
                        // Right control, unparseable value (line corruption) - drop it quietly.
                        break;

                    default:
                        Debug.LogWarning($"⚠️ Unsupported control type: {control.GetType().Name} for key {key}");
                        break;
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"❌ Failed to process control '{key}': {ex.Message}");
            }
        }
    }
}
}


#endif