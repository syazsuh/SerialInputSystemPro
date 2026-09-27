// Same platform guard as SerialManager: that class only exists on desktop and in the
// Editor, so anything referencing it must be excluded from WebGL/mobile/console builds
// or the player build fails to compile.
#if UNITY_STANDALONE_WIN || UNITY_STANDALONE_OSX || UNITY_STANDALONE_LINUX || UNITY_EDITOR
using System;
using System.Collections.Generic;

namespace SISPro
{
/// <summary>
/// Tracks every SerialManager currently active in the scene, so a rig with several
/// physical boards has one place to ask "what's connected right now" instead of each
/// SerialManager being an island. Registration is automatic (SerialManager.OnEnable/
/// OnDisable) - nothing else needs to call Register/Unregister directly.
///
/// This is intentionally minimal: a list, a lookup by deviceID, and events for anything
/// that wants to build a live "connected devices" panel later. It doesn't own connection
/// logic or duplicate anything SerialManager already does.
/// </summary>
public static class SerialHub
{
    static readonly List<SerialManager> _managers = new List<SerialManager>();

    public static IReadOnlyList<SerialManager> Managers => _managers.AsReadOnly();

    public static event Action<SerialManager> OnManagerRegistered;
    public static event Action<SerialManager> OnManagerUnregistered;

    public static void Register(SerialManager manager)
    {
        if (manager == null || _managers.Contains(manager)) return;
        _managers.Add(manager);
        OnManagerRegistered?.Invoke(manager);
    }

    public static void Unregister(SerialManager manager)
    {
        if (manager == null) return;
        if (_managers.Remove(manager))
            OnManagerUnregistered?.Invoke(manager);
    }

    /// <summary>Finds the first registered manager whose assigned profile has this deviceID.</summary>
    public static SerialManager FindByDeviceID(string deviceID)
    {
        if (string.IsNullOrEmpty(deviceID)) return null;

        foreach (var m in _managers)
            if (m != null && m.deviceProfile != null && m.deviceProfile.deviceID == deviceID)
                return m;

        return null;
    }

    /// <summary>
    /// Finds the first registered manager using this COM/tty port name - either the
    /// port it actually opened (which covers auto-selected ports) or its configured
    /// Port Name.
    /// </summary>
    public static SerialManager FindByPortName(string portName)
    {
        if (string.IsNullOrEmpty(portName)) return null;

        foreach (var m in _managers)
            if (m != null && (m.ActivePortName == portName || m.portName == portName))
                return m;

        return null;
    }

    public static int ConnectedCount
    {
        get
        {
            int n = 0;
            foreach (var m in _managers)
                if (m != null && m.serial_connected) n++;
            return n;
        }
    }

    /// <summary>
    /// Editor/runtime domain reload can leave stale (destroyed) entries behind if a
    /// manager's OnDisable somehow didn't run. Cheap to call defensively before
    /// iterating Managers from a UI that wants to be robust against that edge case.
    /// </summary>
    public static void PruneDestroyed()
    {
        _managers.RemoveAll(m => m == null);
    }
}
}
#endif