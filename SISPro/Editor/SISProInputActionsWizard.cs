#if UNITY_EDITOR
using UnityEngine;
using UnityEditor;
using UnityEngine.InputSystem;
using SISPro.EditorTools; // <- builder namespace

namespace SISPro
{
    public class SISProInputActionsWizard : EditorWindow
    {
        // Default to using an existing asset
        private bool useExistingAsset = true;
        private InputActionAsset existingAsset;
        private string createAssetPath = "Assets/SISPro/Serial/SISProControls.inputactions";

        private SerialDeviceProfile profile; // choose existing profile

        // Manual fields (only used when no profile is assigned). Defaults match a new
        // SerialDeviceProfile and the old sketch generator's Uno layout.
        private string deviceLayoutName = "UnoInputDevice";
        private int digitalCount = 8;
        private int analogCount = 6;

        private string actionMapName = "Serial Action";
        private string controlSchemeName = "Serial Device";

        [MenuItem("Tools/SISPro/Advanced/Input Actions Wizard", priority = 100)]
        public static void ShowWindow()
        {
            var w = GetWindow<SISProInputActionsWizard>("SISPro Input Actions");
            w.minSize = new Vector2(520, 360);
            w.Show();
        }

        static bool IsEditable(Object asset)
        {
            // Package assets (e.g. the Input System's DefaultInputActions) live in a
            // read-only cache - only files under Assets/ can be written.
            string path = asset != null ? AssetDatabase.GetAssetPath(asset) : null;
            return !string.IsNullOrEmpty(path) && path.StartsWith("Assets/");
        }

        // Auto-pick an existing InputActionAsset if one is selected or found in the project
        private void OnEnable()
        {
            if (existingAsset == null && useExistingAsset)
            {
                // Prefer current selection if it's an editable InputActionAsset
                var sel = Selection.activeObject as InputActionAsset;
                if (sel != null && IsEditable(sel))
                {
                    existingAsset = sel;
                }
                else
                {
                    // Fall back: first InputActionAsset under Assets/ (never a package's)
                    var guids = AssetDatabase.FindAssets("t:InputActionAsset", new[] { "Assets" });
                    if (guids != null && guids.Length > 0)
                    {
                        var path = AssetDatabase.GUIDToAssetPath(guids[0]);
                        existingAsset = AssetDatabase.LoadAssetAtPath<InputActionAsset>(path);
                    }
                }
            }
        }

        private void OnGUI()
        {
            GUILayout.Label("SISPro Input Actions Wizard", EditorStyles.boldLabel);
            EditorGUILayout.Space();

            profile = (SerialDeviceProfile)EditorGUILayout.ObjectField("SerialDeviceProfile", profile, typeof(SerialDeviceProfile), false);

            EditorGUILayout.Space(6);
            useExistingAsset = EditorGUILayout.ToggleLeft("Use Existing InputActionAsset", useExistingAsset);

            bool canGenerate = true;

            if (useExistingAsset)
            {
                existingAsset = (InputActionAsset)EditorGUILayout.ObjectField("Existing Asset", existingAsset, typeof(InputActionAsset), false);

                if (existingAsset == null)
                {
                    EditorGUILayout.HelpBox("Select an existing .inputactions asset (or untick to create a new one).", MessageType.Info);
                    canGenerate = false;
                }
                else if (!IsEditable(existingAsset))
                {
                    EditorGUILayout.HelpBox(
                        "This asset is inside a package and can't be modified. Pick one under Assets/, " +
                        "or untick to create a new one.", MessageType.Warning);
                    canGenerate = false;
                }

                EditorGUI.BeginDisabledGroup(true);
                EditorGUILayout.TextField("Create Path", "(ignored)");
                EditorGUI.EndDisabledGroup();
            }
            else
            {
                existingAsset = null;
                createAssetPath = EditorGUILayout.TextField("Create New At Path", createAssetPath);
            }

            EditorGUILayout.Space(8);
            actionMapName = EditorGUILayout.TextField("Action Map Name", actionMapName);
            controlSchemeName = EditorGUILayout.TextField("Control Scheme Name", controlSchemeName);

            EditorGUILayout.Space(4);
            // If no profile is provided, allow manual fields
            using (new EditorGUI.DisabledScope(profile != null))
            {
                deviceLayoutName = EditorGUILayout.TextField("Device Layout Name", deviceLayoutName);
                digitalCount = Mathf.Max(0, EditorGUILayout.IntField("Digital Inputs", digitalCount));
                analogCount = Mathf.Max(0, EditorGUILayout.IntField("Analog Inputs", analogCount));
            }

            EditorGUILayout.Space(10);
            using (new EditorGUI.DisabledScope(!canGenerate))
            {
                if (GUILayout.Button("Generate / Update", GUILayout.Height(32)))
                {
                    try
                    {
                        if (profile != null)
                        {
                            SISProInputActionsBuilder.BuildFromProfile(
                                profile,
                                useExistingAsset ? existingAsset : null,
                                createAssetPath,
                                actionMapName,
                                controlSchemeName
                            );
                        }
                        else
                        {
                            SISProInputActionsBuilder.BuildOrUpdate(
                                deviceLayoutName,
                                digitalCount,
                                analogCount,
                                actionMapName,
                                controlSchemeName,
                                useExistingAsset ? existingAsset : null,
                                createAssetPath
                            );
                        }
                    }
                    catch (System.Exception ex)
                    {
                        Debug.LogError("[SISPro] Input Actions generation failed: " + ex.Message);
                    }
                }
            }
        }
    }
}
#endif