#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

namespace SISPro
{
/// <summary>
/// Project-setting checks that silently break serial ports, shared by the
/// Set Up Arduino window (as a banner that only appears when something is wrong)
/// and the Control Panel (always-visible status rows).
///
/// Kept in one place so the two windows can never disagree about what "OK" means.
///
/// IMPORTANT - this file must live in its own folder next to SISPro.SetupChecks.asmdef
/// (e.g. Assets/SISPro/Editor/SetupChecks/). Under a .NET Standard API level,
/// System.IO.Ports doesn't exist, so every script that uses SerialPort fails to
/// compile - and with them every window in the same assembly. Only a separate
/// assembly that doesn't touch System.IO.Ports still compiles in that state, which
/// is what lets ApiCompatibilityPrompt below offer the one-click fix.
/// </summary>
public static class SISProSetupChecks
{
    /// <summary>
    /// System.IO.Ports (used by SerialManager and FirmataCapabilityClient) only exists
    /// in Unity's .NET Framework profile. Under .NET Standard the SISPro scripts don't
    /// compile at all (CS0234: 'Ports' does not exist in namespace 'System.IO').
    /// </summary>
    public static bool IsApiCompatibilityOk(out BuildTargetGroup group, out ApiCompatibilityLevel level)
    {
#pragma warning disable CS0618 // BuildTargetGroup API is obsolete in newer Editors but still functional
                               // across the 2019-6000 range; avoids branching on Editor version.
        group = EditorUserBuildSettings.selectedBuildTargetGroup;
        level = PlayerSettings.GetApiCompatibilityLevel(group);
#pragma warning restore CS0618
        return IsNetFramework(level);
    }

    /// <summary>
    /// NET_4_6 is ".NET Framework" in the Player Settings UI; newer Editors also have
    /// NET_Unity_4_8. Compared by name so this compiles on Editors that lack the latter.
    /// </summary>
    static bool IsNetFramework(ApiCompatibilityLevel level)
    {
        string n = level.ToString();
        return n == "NET_4_6" || n == "NET_Unity_4_8";
    }

    public static void FixApiCompatibility()
    {
#pragma warning disable CS0618
        var group = EditorUserBuildSettings.selectedBuildTargetGroup;
        PlayerSettings.SetApiCompatibilityLevel(group, ApiCompatibilityLevel.NET_4_6);
#pragma warning restore CS0618
        Debug.Log("[SISPro] API Compatibility Level for " + group + " set to .NET Framework (NET_4_6). " +
                  "Scripts will recompile now.");
    }

    /// <summary>
    /// Physical serial ports only exist on desktop. WebGL, mobile and consoles have
    /// no System.IO.Ports at all - no setting or plugin can work around that.
    /// </summary>
    public static bool IsDesktopTarget(out BuildTarget target)
    {
        target = EditorUserBuildSettings.activeBuildTarget;
        return target == BuildTarget.StandaloneWindows ||
               target == BuildTarget.StandaloneWindows64 ||
               target == BuildTarget.StandaloneOSX ||
               target == BuildTarget.StandaloneLinux64;
    }

    public static bool AllOk => IsApiCompatibilityOk(out _, out _) && IsDesktopTarget(out _);

    /// <summary>
    /// Draws nothing when everything is fine - so beginners only ever see this when
    /// it's the reason their board won't connect, with a one-click fix.
    /// </summary>
    public static void DrawProblemsBanner()
    {
        if (!IsApiCompatibilityOk(out var group, out var level))
        {
            EditorGUILayout.HelpBox(
                "Serial ports need the API Compatibility Level set to .NET Framework " +
                "(" + group + " is currently " + level + "). Without it, the serial scripts " +
                "don't compile.",
                MessageType.Warning);
            if (GUILayout.Button("Fix it: switch to .NET Framework", GUILayout.Height(24)))
                FixApiCompatibility();
            EditorGUILayout.Space(4);
        }

        if (!IsDesktopTarget(out var target))
        {
            EditorGUILayout.HelpBox(
                "Active build target is " + target + ". Setup and Play Mode work here in the Editor, but " +
                "serial ports only exist in Windows/Mac/Linux builds - in a " + target + " build the serial " +
                "components are compiled out and nothing will connect.",
                MessageType.Info);
            EditorGUILayout.Space(4);
        }
    }

    /// <summary>Always-visible status rows, for the Control Panel.</summary>
    public static void DrawStatusRows()
    {
        bool apiOk = IsApiCompatibilityOk(out var group, out var level);
        DrawRow(apiOk, "API Compatibility Level (" + group + "): " + level);
        if (!apiOk)
        {
            EditorGUILayout.HelpBox(
                "System.IO.Ports.SerialPort only exists under .NET Framework - with anything else the " +
                "serial scripts fail to compile. Unity will recompile scripts after switching - no " +
                "restart needed.",
                MessageType.Warning);
            if (GUILayout.Button("Set API Compatibility Level to .NET Framework", GUILayout.Height(26)))
                FixApiCompatibility();
        }

        EditorGUILayout.Space(6);

        bool desktop = IsDesktopTarget(out var target);
        DrawRow(desktop, "Active build target: " + target);
        if (!desktop)
        {
            EditorGUILayout.HelpBox(
                "Physical serial ports only exist on desktop (Windows/Mac/Linux standalone or the " +
                "Editor itself). WebGL, mobile, and console builds have no System.IO.Ports at all.",
                MessageType.Info);
        }
    }

    static void DrawRow(bool ok, string text)
    {
        EditorGUILayout.BeginHorizontal();
        EditorGUILayout.LabelField(ok ? "✅" : "⚠️", GUILayout.Width(20));
        EditorGUILayout.LabelField(text);
        EditorGUILayout.EndHorizontal();
    }
}

/// <summary>
/// Runs after every script compile. When the API level is wrong, the SISPro windows
/// themselves can't compile, so this (in its own assembly) is the only place that can
/// still explain the problem and fix it. Asks once per Editor session.
/// </summary>
[InitializeOnLoad]
static class ApiCompatibilityPrompt
{
    const string AskedKey = "SISPro.ApiCompatibilityPrompt.Asked";

    static ApiCompatibilityPrompt()
    {
        // Wait until the Editor is idle - dialogs during a domain reload are unreliable.
        EditorApplication.delayCall += Check;
    }

    static void Check()
    {
        if (Application.isBatchMode) return;
        if (SISProSetupChecks.IsApiCompatibilityOk(out var group, out var level)) return;

        Debug.LogError("[SISPro] API Compatibility Level for " + group + " is " + level + ". SISPro needs " +
                       ".NET Framework for System.IO.Ports - errors like \"'Ports' does not exist in the " +
                       "namespace 'System.IO'\" come from this. Fix: Edit > Project Settings > Player > " +
                       "Other Settings > Api Compatibility Level = .NET Framework, or Tools/SISPro/Fix API " +
                       "Compatibility Level.");

        if (SessionState.GetBool(AskedKey, false)) return;
        SessionState.SetBool(AskedKey, true);

        if (EditorUtility.DisplayDialog(
                "SISPro: serial ports need .NET Framework",
                "The API Compatibility Level for " + group + " is " + level + ".\n\n" +
                "System.IO.Ports only exists under .NET Framework, so the SISPro serial scripts can't " +
                "compile until it's changed. Switch now? (Scripts recompile, no restart needed.)",
                "Switch to .NET Framework", "Not now"))
        {
            SISProSetupChecks.FixApiCompatibility();
        }
    }

    [MenuItem("Tools/SISPro/Fix API Compatibility Level", priority = 200)]
    static void FixFromMenu() => SISProSetupChecks.FixApiCompatibility();

    [MenuItem("Tools/SISPro/Fix API Compatibility Level", true)]
    static bool FixFromMenuValidate() => !SISProSetupChecks.IsApiCompatibilityOk(out _, out _);
}
}
#endif