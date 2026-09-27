#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;
using UnityEngine.InputSystem;

namespace SISPro.EditorTools
{
    /// <summary>
    /// Headless, idempotent builder for creating/updating an InputActionAsset
    /// for SISPro devices using supported Input System APIs only.
    ///
    /// .inputactions files are JSON, imported by the Input System's ScriptedImporter.
    /// The loaded InputActionAsset is an *import result*: editing it in memory and
    /// calling SaveAssets doesn't write anything back to the JSON, and
    /// AssetDatabase.CreateAsset writes Unity YAML that the importer can't read.
    /// So every build here works on a copy parsed from the file's JSON, then writes
    /// ToJson() back to disk and reimports - the same round trip the Input Actions
    /// editor itself does when you click Save.
    /// </summary>
    public static class SISProInputActionsBuilder
    {
        // Actions this builder owns: "D<pin>" / "A<channel>". Anything else in the map
        // (actions the user added by hand) is never touched by stale-action cleanup.
        static readonly Regex SerialActionName = new Regex(@"^[DA]\d+$");

        /// <summary>
        /// Build/update using explicit params (contiguous D0..N / A0..M). This is the
        /// legacy, index-based path used by manually-entered fields and by profiles
        /// made with older versions of the sketch generator (compiled device classes).
        /// </summary>
        public static InputActionAsset BuildOrUpdate(
            string deviceLayoutName,
            int digitalCount,
            int analogCount,
            string actionMapName = "Serial Action",
            string controlSchemeName = "Serial Device",
            InputActionAsset targetAsset = null,
            string createNewAssetPathIfNull = "Assets/SISPro/Serial/SISProControls.inputactions")
        {
            var digital = new List<int>();
            for (int i = 0; i < Mathf.Max(0, digitalCount); i++) digital.Add(i);
            var analog = new List<int>();
            for (int i = 0; i < Mathf.Max(0, analogCount); i++) analog.Add(i);

            return Build(deviceLayoutName, digital, analog, actionMapName, controlSchemeName,
                         targetAsset, createNewAssetPathIfNull);
        }

        /// <summary>
        /// Build/update using actual discovered pin numbers / analog channel indices,
        /// which may be sparse and non-contiguous (a Mega's usable digital pins are not
        /// 0..N). Bindings are named to match exactly what SerialDeviceLayoutRegistry
        /// generates for the same profile, so "D7" binds to a control that really exists
        /// even when there's no D0..D6 before it.
        /// </summary>
        public static InputActionAsset BuildOrUpdateFromPins(
            string deviceLayoutName,
            List<int> digitalPins,
            List<int> analogChannels,
            string actionMapName = "Serial Action",
            string controlSchemeName = "Serial Device",
            InputActionAsset targetAsset = null,
            string createNewAssetPathIfNull = "Assets/SISPro/Serial/SISProControls.inputactions")
        {
            return Build(deviceLayoutName, digitalPins, analogChannels, actionMapName, controlSchemeName,
                         targetAsset, createNewAssetPathIfNull);
        }

        /// <summary>
        /// Build/update using a SerialDeviceProfile (preferred: single source of truth).
        /// Automatically picks the pin-list path for profiles that have one (Set Up
        /// Arduino, and the current sketch generator), and falls back to the legacy
        /// contiguous-count path for older hand-authored / code-generated profiles.
        /// </summary>
        public static InputActionAsset BuildFromProfile(
            SerialDeviceProfile profile,
            InputActionAsset targetAsset = null,
            string createNewAssetPathIfNull = "Assets/SISPro/Serial/SISProControls.inputactions",
            string actionMapName = "Serial Action",
            string controlSchemeName = "Serial Device")
        {
            if (profile == null)
            {
                Debug.LogError("[SISPro] BuildFromProfile: profile is null.");
                return null;
            }

            string layoutName = profile.layoutName;

            if (profile.HasDiscoveredPins)
            {
                return BuildOrUpdateFromPins(
                    layoutName, profile.digitalPins, profile.analogChannels,
                    actionMapName, controlSchemeName,
                    targetAsset, createNewAssetPathIfNull
                );
            }

            int dCount = Mathf.Max(0, profile.digitalCount);
            int aCount = Mathf.Max(0, profile.analogCount);

            return BuildOrUpdate(
                layoutName, dCount, aCount,
                actionMapName, controlSchemeName,
                targetAsset, createNewAssetPathIfNull
            );
        }

        // ----------------- Core -----------------

        static InputActionAsset Build(
            string deviceLayoutName,
            List<int> digitalPins,
            List<int> analogChannels,
            string actionMapName,
            string controlSchemeName,
            InputActionAsset targetAsset,
            string createNewAssetPathIfNull)
        {
            var asset = LoadWorkingCopy(targetAsset, createNewAssetPathIfNull, out string path, out bool isNew);

            try
            {
                EnsureControlScheme(asset, controlSchemeName, deviceLayoutName);
                var map = GetOrCreateSerialMapEnsuringTypes(asset, actionMapName, digitalPins, analogChannels);

                var expected = new HashSet<string>();
                foreach (var pin in digitalPins) expected.Add($"D{pin}");
                foreach (var channel in analogChannels) expected.Add($"A{channel}");
                RemoveStaleSerialActions(map, expected);

                foreach (var pin in digitalPins)
                {
                    var action = AddOrReuseAction(map, $"D{pin}", InputActionType.Button, "Button");
                    AddBindingIfMissing(action, $"<{deviceLayoutName}>/D{pin}");
                }

                foreach (var channel in analogChannels)
                {
                    var action = AddOrReuseAction(map, $"A{channel}", InputActionType.Value, "Axis");
                    AddBindingIfMissing(action, $"<{deviceLayoutName}>/A{channel}");
                }
            }
            catch
            {
                UnityEngine.Object.DestroyImmediate(asset);
                throw;
            }

            return SaveWorkingCopy(asset, path, isNew);
        }

        // ----------------- File round trip -----------------

        /// <summary>
        /// Returns an editable in-memory copy of the target .inputactions file (parsed
        /// from its JSON), or a fresh empty asset if the file doesn't exist yet.
        /// </summary>
        static InputActionAsset LoadWorkingCopy(InputActionAsset target, string createPath,
                                                out string path, out bool isNew)
        {
            path = target != null ? AssetDatabase.GetAssetPath(target) : createPath;

            if (string.IsNullOrEmpty(path))
                throw new InvalidOperationException(
                    "The target InputActionAsset isn't saved in the project, so there's no .inputactions file to update.");

            if (!path.Replace('\\', '/').StartsWith("Assets/", StringComparison.Ordinal))
                throw new InvalidOperationException(
                    $"'{path}' is outside the Assets folder (e.g. inside a package), so it can't be modified. " +
                    "Pick an .inputactions asset under Assets/, or leave the target blank to create one.");

            if (!path.EndsWith(".inputactions", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    $"'{path}' isn't an .inputactions file. Pick an .inputactions asset, or leave the target blank to create one.");

            if (File.Exists(path))
            {
                InputActionAsset copy;
                try
                {
                    copy = InputActionAsset.FromJson(File.ReadAllText(path));
                }
                catch (Exception ex)
                {
                    throw new InvalidOperationException(
                        $"Couldn't read '{path}' as Input Actions JSON ({ex.Message}). If an older SISPro " +
                        "version created it, delete the file and generate again.");
                }
                copy.name = Path.GetFileNameWithoutExtension(path);
                isNew = false;
                return copy;
            }

            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            var fresh = ScriptableObject.CreateInstance<InputActionAsset>();
            fresh.name = Path.GetFileNameWithoutExtension(path);
            isNew = true;
            return fresh;
        }

        /// <summary>
        /// Writes the working copy's JSON to disk, reimports, and returns the real
        /// imported asset (same GUID as before for an existing file, so every
        /// InputActionReference pointing into it stays valid).
        /// </summary>
        static InputActionAsset SaveWorkingCopy(InputActionAsset working, string path, bool isNew)
        {
            string json = working.ToJson();
            UnityEngine.Object.DestroyImmediate(working);

            File.WriteAllText(path, json);
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);

            Debug.Log(isNew
                ? $"[SISPro] Created InputActionAsset at: {path}"
                : $"[SISPro] Updated InputActionAsset at: {path}");

            return AssetDatabase.LoadAssetAtPath<InputActionAsset>(path);
        }

        // ----------------- Internal helpers -----------------

        /// <summary>
        /// Ensures a scheme named 'schemeName' exists and requires &lt;layoutName&gt;.
        /// If missing, creates it; if present but missing the requirement, removes and
        /// re-adds it with the old requirements (keeping their optional/OR flags and the
        /// scheme's binding group) plus the new one.
        /// </summary>
        private static void EnsureControlScheme(InputActionAsset asset, string schemeName, string layoutName)
        {
            var requiredPath = $"<{layoutName}>";
            int idx = -1;
            var schemes = asset.controlSchemes;
            for (int i = 0; i < schemes.Count; i++)
            {
                if (schemes[i].name == schemeName) { idx = i; break; }
            }

            if (idx < 0)
            {
                asset.AddControlScheme(schemeName).WithRequiredDevice(requiredPath);
                return;
            }

            var existing = schemes[idx];
            foreach (var req in existing.deviceRequirements)
                if (req.controlPath == requiredPath) return; // already required

            // Copy what we need before removing - the struct's arrays belong to the asset.
            string bindingGroup = existing.bindingGroup;
            var oldRequirements = new List<InputControlScheme.DeviceRequirement>(existing.deviceRequirements);

            asset.RemoveControlScheme(existing.name);
            var builder = asset.AddControlScheme(schemeName);
            if (!string.IsNullOrEmpty(bindingGroup))
                builder = builder.WithBindingGroup(bindingGroup);

            foreach (var req in oldRequirements)
            {
                if (req.isOR)
                    builder = req.isOptional ? builder.OrWithOptionalDevice(req.controlPath)
                                             : builder.OrWithRequiredDevice(req.controlPath);
                else
                    builder = req.isOptional ? builder.WithOptionalDevice(req.controlPath)
                                             : builder.WithRequiredDevice(req.controlPath);
            }

            builder.WithRequiredDevice(requiredPath);
        }

        /// <summary>
        /// Returns an action map ready for us to (re)fill.
        /// If any expected action has a wrong type, we remove the whole map and recreate it.
        /// </summary>
        private static InputActionMap GetOrCreateSerialMapEnsuringTypes(
            InputActionAsset asset,
            string mapName,
            List<int> digitalPins,
            List<int> analogChannels)
        {
            var map = asset.FindActionMap(mapName, throwIfNotFound: false);
            if (map == null)
                return asset.AddActionMap(mapName);

            bool needsRebuild = false;

            foreach (var pin in digitalPins)
            {
                var a = map.FindAction($"D{pin}", throwIfNotFound: false);
                if (a != null && a.type != InputActionType.Button) { needsRebuild = true; break; }
            }

            if (!needsRebuild)
            {
                foreach (var channel in analogChannels)
                {
                    var a = map.FindAction($"A{channel}", throwIfNotFound: false);
                    if (a != null && a.type != InputActionType.Value) { needsRebuild = true; break; }
                }
            }

            if (!needsRebuild)
                return map;

            InputActionSetupExtensions.RemoveActionMap(asset, mapName);
            return asset.AddActionMap(mapName);
        }

        /// <summary>
        /// Removes D#/A# actions whose pin is no longer an input on the profile (e.g.
        /// changed to an output or Unused on a re-scan), so they don't accumulate and
        /// sit there bound to controls that no longer exist. Hand-added actions with
        /// any other name are left alone.
        /// </summary>
        private static void RemoveStaleSerialActions(InputActionMap map, HashSet<string> expected)
        {
            var stale = new List<InputAction>();
            foreach (var action in map.actions)
                if (SerialActionName.IsMatch(action.name) && !expected.Contains(action.name))
                    stale.Add(action);

            foreach (var action in stale)
            {
                Debug.Log($"[SISPro] Removed action '{map.name}/{action.name}' - that pin is no longer an input on the profile.");
                InputActionSetupExtensions.RemoveAction(action);
            }
        }

        /// <summary>
        /// Adds a new action if missing; reuses if present (expects caller to have rebuilt the map if type mismatched).
        /// </summary>
        private static InputAction AddOrReuseAction(
            InputActionMap map,
            string actionName,
            InputActionType type,
            string expectedControlType = null)
        {
            var action = map.FindAction(actionName, throwIfNotFound: false);
            if (action == null)
            {
                action = map.AddAction(actionName, type: type);
                if (!string.IsNullOrEmpty(expectedControlType))
                    action.expectedControlType = expectedControlType;
                return action;
            }

            if (!string.IsNullOrEmpty(expectedControlType))
                action.expectedControlType = expectedControlType;

            return action;
        }

        private static void AddBindingIfMissing(InputAction action, string bindingPath)
        {
            for (int i = 0; i < action.bindings.Count; i++)
                if (action.bindings[i].path == bindingPath)
                    return;
            action.AddBinding(bindingPath);
        }
    }
}
#endif