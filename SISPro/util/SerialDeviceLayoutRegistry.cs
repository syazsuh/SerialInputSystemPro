using System.Text;
using UnityEngine.InputSystem;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace SISPro
{
    /// <summary>
    /// Builds and registers a dynamic Input System layout from a SerialDeviceProfile's
    /// discovered (possibly sparse) digitalPins/analogChannels.
    ///
    /// Uses Input System's JSON layout format (documented, stable across package
    /// versions - see "Layouts > JSON layouts" in the Input System manual) rather
    /// than the InputControlLayout.Builder C# fluent API, whose exact method
    /// surface has shifted between versions. RegisterLayout(json, name) is a
    /// plain runtime API, so the same call works in the Editor and in a build.
    ///
    /// Only handles profiles populated via the Capability Explorer
    /// (profile.HasDiscoveredPins == true). Legacy profiles that only set
    /// digitalCount/analogCount keep using the compiled classes that
    /// SerialDeviceCodeGenerator produces and self-registers - this registry
    /// never touches those.
    /// </summary>
    public static class SerialDeviceLayoutRegistry
    {
        /// <summary>
        /// Assembly-qualified name of the C# class the device is instantiated as.
        ///
        /// Must be given as "type", NOT as "extend": "InputDevice". There is no layout
        /// registered under the name "InputDevice" (it's only the C# base class that
        /// concrete layouts like Gamepad derive from), so "extend" fails with
        /// "Cannot find base layout 'InputDevice'" the moment anything - the Input
        /// Actions editor, AddDevice, a binding lookup - tries to load the layout.
        /// Built from typeof() so it can't drift if the Input System assembly is renamed.
        /// </summary>
        static readonly string DeviceTypeName =
            typeof(InputDevice).FullName + ", " + typeof(InputDevice).Assembly.GetName().Name;

#if UNITY_EDITOR
    /// <summary>
    /// Runs on every domain reload (script compile) so a profile's layout is
    /// visible to the Input Actions binding picker without needing Play Mode -
    /// this is the direct fix for "you don't see the device options unless you
    /// press Play".
    /// </summary>
    [InitializeOnLoadMethod]
    static void EditorInit()
    {
        ScanAndRegisterAll();
    }

    static void ScanAndRegisterAll()
    {
        var guids = AssetDatabase.FindAssets("t:SerialDeviceProfile");
        foreach (var guid in guids)
        {
            var path = AssetDatabase.GUIDToAssetPath(guid);
            var profile = AssetDatabase.LoadAssetAtPath<SerialDeviceProfile>(path);
            if (profile != null) EnsureRegistered(profile);
        }
    }
#endif

        /// <summary>
        /// Registers (or re-registers) this profile's layout. Safe to call repeatedly:
        /// RegisterLayout with the same name replaces the previous definition, which is
        /// exactly what we want when a profile's discovered pins change. Called from
        /// SerialDeviceProfile.OnValidate (editor, live-edit case), from the Capability
        /// Explorer after applying, and defensively from SerialToInputSystemAdapter right
        /// before adding a device (covers Play Mode and player builds, where there is no
        /// AssetDatabase to scan with).
        /// </summary>
        public static void EnsureRegistered(SerialDeviceProfile profile)
        {
            if (profile == null) return;
            if (string.IsNullOrEmpty(profile.layoutName)) return;
            if (!profile.HasDiscoveredPins) return; // legacy profiles stay on the codegen path

            string json = BuildLayoutJson(profile);
            InputSystem.RegisterLayout(json, profile.layoutName);
        }

        static string BuildLayoutJson(SerialDeviceProfile profile)
        {
            string displayName = string.IsNullOrEmpty(profile.deviceID)
                ? profile.layoutName
                : profile.deviceID + " (Serial)";

            var sb = new StringBuilder();
            sb.Append("{ \"name\": \"").Append(profile.layoutName).Append("\", ");
            sb.Append("\"type\": \"").Append(DeviceTypeName).Append("\", ");
            sb.Append("\"displayName\": \"").Append(EscapeJson(displayName)).Append("\", ");
            sb.Append("\"controls\": [");

            uint offset = 0;
            bool first = true;

            // Digital pins are 1 byte each (Button control).
            foreach (var pin in profile.digitalPins)
            {
                if (!first) sb.Append(", ");
                sb.Append("{ \"name\": \"D").Append(pin)
                  .Append("\", \"layout\": \"Button\", \"offset\": ").Append(offset).Append(" }");
                offset += 1;
                first = false;
            }

            offset = (offset + 3u) & ~3u; // 4-byte align before the analog block

            // Analog channels are 4-byte floats (Axis control, explicit FLT format to
            // match the float the adapter writes), named by channel index
            // (0 = A0, 1 = A1, ...) to match the "A{channel}" keys the protocol sends.
            foreach (var channel in profile.analogChannels)
            {
                if (!first) sb.Append(", ");
                sb.Append("{ \"name\": \"A").Append(channel)
                  .Append("\", \"layout\": \"Axis\", \"format\": \"FLT\", \"offset\": ").Append(offset).Append(" }");
                offset += 4;
                first = false;
            }

            sb.Append("] }");
            return sb.ToString();
        }

        static string EscapeJson(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");
    }
}