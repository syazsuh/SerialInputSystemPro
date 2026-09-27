#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

namespace SISPro
{
/// <summary>
/// Landing window: project-setting checks, one obvious primary action (Set Up
/// Arduino), and the advanced tools set visibly apart underneath it.
///
/// The checks themselves live in SISProSetupChecks, shared with the Set Up Arduino
/// window's warning banner so the two can never disagree about what "OK" means.
/// </summary>
public class SISProControlPanelWindow : EditorWindow
{
    [MenuItem("Tools/SISPro/Control Panel", priority = 1)]
    public static void ShowWindow()
    {
        var w = GetWindow<SISProControlPanelWindow>("SISPro");
        w.minSize = new Vector2(420, 380);
        w.Show();
    }

    static readonly Color PrimaryGreen = new Color(0.45f, 0.8f, 0.5f); // same green as Set Up Arduino's main buttons

    Vector2 _scroll;

    void OnGUI()
    {
        _scroll = EditorGUILayout.BeginScrollView(_scroll);

        EditorGUILayout.LabelField("SISPro - Arduino/Serial Input Toolkit", EditorStyles.boldLabel);
        EditorGUILayout.Space(8);

        EditorGUILayout.LabelField("Setup Checks", EditorStyles.boldLabel);
        SISProSetupChecks.DrawStatusRows();

        EditorGUILayout.Space(16);
        DrawGetStarted();

        EditorGUILayout.Space(28);
        DrawAdvancedTools();

        EditorGUILayout.EndScrollView();
    }

    void DrawGetStarted()
    {
        EditorGUILayout.LabelField("Get Started", EditorStyles.boldLabel);

        var prev = GUI.backgroundColor;
        GUI.backgroundColor = PrimaryGreen;
        bool clicked = GUILayout.Button("Set Up Arduino", GUILayout.Height(40));
        GUI.backgroundColor = prev;
        if (clicked)
            SerialCapabilityExplorerWindow.ShowWindow();

        EditorGUILayout.LabelField(
            "Find your board, choose what each pin does, and create its profile + Input Actions in one go.",
            EditorStyles.wordWrappedMiniLabel);
    }

    void DrawAdvancedTools()
    {
        // Thin divider so the advanced block reads as a separate, optional section.
        var line = GUILayoutUtility.GetRect(1f, 1f, GUILayout.ExpandWidth(true));
        EditorGUI.DrawRect(line, new Color(0.5f, 0.5f, 0.5f, 0.35f));
        EditorGUILayout.Space(8);

        EditorGUILayout.LabelField("Advanced Tools", EditorStyles.boldLabel);
        EditorGUILayout.LabelField("You don't need these if you used Set Up Arduino.",
            EditorStyles.wordWrappedMiniLabel);
        EditorGUILayout.Space(6);

        if (GUILayout.Button("Input Actions Wizard", GUILayout.Height(24)))
            SISProInputActionsWizard.ShowWindow();
        EditorGUILayout.LabelField(
            "Regenerate Input Actions for a profile into a specific asset, action map or control scheme.",
            EditorStyles.wordWrappedMiniLabel);

        EditorGUILayout.Space(8);

        if (GUILayout.Button("Generate Custom Sketch + Profile", GUILayout.Height(24)))
            SerialDeviceCodeGenerator.Init();
        EditorGUILayout.LabelField(
            "Without Firmata: generates an .ino sketch (inputs from pin D2 up) and a matching profile.",
            EditorStyles.wordWrappedMiniLabel);
    }
}
}
#endif