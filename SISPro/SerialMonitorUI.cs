// Same platform guard as SerialManager, which this depends on - without it,
// WebGL/mobile/console player builds fail to compile.
#if UNITY_STANDALONE_WIN || UNITY_STANDALONE_OSX || UNITY_STANDALONE_LINUX || UNITY_EDITOR
using UnityEngine;
using UnityEngine.UI;
using TMPro;
using System.IO;
using System.Text;
using System;
using System.Collections.Generic;

namespace SISPro
{
    public class SerialMonitorUI : MonoBehaviour
    {
        [Header("Dependencies")]
        public SerialManager serialManager;
        public TextMeshProUGUI statusText;
        public TextMeshProUGUI logText;
        public TMP_InputField inputField;
        public Button sendButton;
        public Button saveLogButton;
        public Button clearLogButton;
        public Button reconnectButton;
        public TextMeshProUGUI reconnectButtonText;
        public Toggle autoScrollToggle;
        public Toggle gibberishFilterToggle;
        public TMP_Dropdown baudRateDropdown;
        public TextMeshProUGUI summaryText;

        [Header("Unmatched Keys (optional)")]
        [Tooltip("If assigned, shows keys the adapter received that didn't match any control on " +
                 "the target device - a typo in firmware, wrong pin, or a stale profile. Populated " +
                 "from SerialToInputSystemAdapter.OnUnmatchedKey. Cleared by the same Clear button " +
                 "as the log.")]
        public TextMeshProUGUI unmatchedKeysText;

        [Header("Settings")]
        public int maxLines = 200;

        static readonly List<string> BaudOptions = new List<string> { "9600", "57600", "115200", "250000" };

        private readonly StringBuilder logBuffer = new();
        private SerialToInputSystemAdapter _adapter;
        private readonly Dictionary<string, int> _unmatchedKeyCounts = new();

        // Rebuilding a 200-line string every frame is a steady stream of garbage -
        // especially with Firmata streaming analog values every ~19ms. Only rebuild
        // when the log, the filter, or the unmatched-key counts actually changed.
        private int _renderedLinesVersion = -1;
        private bool _renderedFilterState;
        private bool _unmatchedDirty = true;
        private bool? _renderedConnected;

        void Start()
        {
            if (serialManager == null)
                serialManager = FindAnyObjectByType<SerialManager>();

            sendButton.onClick.AddListener(SendInputLine);
            saveLogButton.onClick.AddListener(SaveLogToFile);
            clearLogButton.onClick.AddListener(ClearLogs);
            reconnectButton.onClick.AddListener(ReconnectSerial);

            // Populate and select BEFORE listening for changes. Setting .value with the
            // listener already attached fired OnBaudRateChanged at startup, which
            // restarted the connection (and reset the board) for any baud but 9600.
            baudRateDropdown.ClearOptions();
            baudRateDropdown.AddOptions(BaudOptions);
            SyncBaudDropdown();
            baudRateDropdown.onValueChanged.AddListener(OnBaudRateChanged);

            UpdateReconnectButtonLabel();
        }

        void OnEnable()
        {
            if (serialManager == null)
                serialManager = FindAnyObjectByType<SerialManager>();

            if (serialManager == null)
            {
                Debug.LogWarning($"{nameof(SerialMonitorUI)}: no SerialManager in the scene - disabling.");
                enabled = false;
                return;
            }

            serialManager.OnSerialConnected?.AddListener(UpdateReconnectButtonLabel);
            serialManager.OnSerialDisconnected?.AddListener(UpdateReconnectButtonLabel);

            // SerialToInputSystemAdapter normally sits on the same GameObject as the
            // SerialManager, but it's optional (an output-only rig may not have one).
            _adapter = serialManager.GetComponent<SerialToInputSystemAdapter>();
            if (_adapter != null)
                _adapter.OnUnmatchedKey += HandleUnmatchedKey;

            _renderedLinesVersion = -1; // force a refresh after being re-enabled
            _renderedConnected = null;
            _unmatchedDirty = true;
        }

        void OnDisable()
        {
            if (serialManager == null) return;

            serialManager.OnSerialConnected?.RemoveListener(UpdateReconnectButtonLabel);
            serialManager.OnSerialDisconnected?.RemoveListener(UpdateReconnectButtonLabel);

            if (_adapter != null)
                _adapter.OnUnmatchedKey -= HandleUnmatchedKey;
        }

        void HandleUnmatchedKey(string key)
        {
            _unmatchedKeyCounts.TryGetValue(key, out int count);
            _unmatchedKeyCounts[key] = count + 1;
            _unmatchedDirty = true;
        }

        void Update()
        {
            UpdateStatus();

            bool filterOn = gibberishFilterToggle.isOn;
            if (serialManager.LinesVersion != _renderedLinesVersion || filterOn != _renderedFilterState)
            {
                _renderedLinesVersion = serialManager.LinesVersion;
                _renderedFilterState = filterOn;
                RebuildLog(filterOn);

                if (autoScrollToggle.isOn)
                    ScrollToBottom();
            }

            if (_unmatchedDirty)
            {
                _unmatchedDirty = false;
                RebuildUnmatchedKeys();
            }
        }

        void RebuildLog(bool filterOn)
        {
            var lines = serialManager.RecentLines;
            logBuffer.Clear();

            int shownLines = 0;
            string lastTime = "---";

            for (int i = Mathf.Max(0, lines.Count - maxLines); i < lines.Count; i++)
            {
                var line = lines[i];
                if (filterOn && IsGibberish(line.Text)) continue;

                logBuffer.AppendLine(line.ToString());
                shownLines++;
                lastTime = line.Timestamp.ToString("HH:mm:ss");
            }

            logText.text = logBuffer.ToString();
            summaryText.text = $"Lines: {shownLines}   Last: {lastTime}";
        }

        void RebuildUnmatchedKeys()
        {
            if (unmatchedKeysText == null) return;

            if (_unmatchedKeyCounts.Count == 0)
            {
                unmatchedKeysText.text = "Unmatched keys: none";
                return;
            }

            var sb = new StringBuilder();
            sb.Append("Unmatched keys (").Append(_unmatchedKeyCounts.Count).Append("):\n");
            foreach (var kvp in _unmatchedKeyCounts)
                sb.Append("  ").Append(kvp.Key).Append("  x").Append(kvp.Value).Append('\n');
            unmatchedKeysText.text = sb.ToString();
        }

        void UpdateStatus()
        {
            bool connected = serialManager.serial_connected;
            if (_renderedConnected == connected) return;
            _renderedConnected = connected;

            statusText.text = connected
                ? "<color=green><b>CONNECTED</b></color>"
                : "<color=red><b>DISCONNECTED</b></color>";
        }

        void UpdateReconnectButtonLabel()
        {
            if (serialManager == null || reconnectButtonText == null) return;

            reconnectButtonText.text = serialManager.serial_connected ? "Disconnect" : "Connect";
        }

        void ReconnectSerial()
        {
            if (serialManager.serial_connected)
                serialManager.StopSerial();
            else
                serialManager.StartSerial();

            UpdateReconnectButtonLabel();
        }

        void SendInputLine()
        {
            if (string.IsNullOrWhiteSpace(inputField.text)) return;

            try
            {
                serialManager.SendLine(inputField.text);
                inputField.text = "";
            }
            catch (Exception ex)
            {
                Debug.LogError("❌ Send failed: " + ex.Message);
            }
        }

        void SaveLogToFile()
        {
            string folder = Application.persistentDataPath;
            string path = Path.Combine(folder, $"SerialLog_{DateTime.Now:yyyyMMdd_HHmmss}.txt");

            File.WriteAllText(path, logText.text);
            Debug.Log($"📄 Log saved to: {path}");
        }

        void ClearLogs()
        {
            serialManager.ClearRecentLines(); // bumps LinesVersion, so the log redraws empty next frame
            _unmatchedKeyCounts.Clear();
            _unmatchedDirty = true;
        }

        /// <summary>
        /// Shows the manager's effective baud rate without firing onValueChanged. A baud
        /// the list doesn't have is added so the dropdown never claims the wrong rate.
        /// When a device profile is assigned it owns the baud rate (SerialManager
        /// re-reads it on every connect), so the dropdown is locked to avoid a choice
        /// that would silently be overwritten.
        /// </summary>
        void SyncBaudDropdown()
        {
            int baud = serialManager.deviceProfile != null ? serialManager.deviceProfile.baudRate : serialManager.baudRate;
            string text = baud.ToString();

            int index = baudRateDropdown.options.FindIndex(o => o.text == text);
            if (index < 0)
            {
                baudRateDropdown.AddOptions(new List<string> { text });
                index = baudRateDropdown.options.Count - 1;
            }

            baudRateDropdown.SetValueWithoutNotify(index);
            baudRateDropdown.interactable = serialManager.deviceProfile == null;
        }

        void OnBaudRateChanged(int index)
        {
            if (serialManager.deviceProfile != null)
            {
                Debug.LogWarning($"⚠️ Baud rate comes from the device profile '{serialManager.deviceProfile.name}' - " +
                                 "change it there.");
                SyncBaudDropdown();
                return;
            }

            if (int.TryParse(baudRateDropdown.options[index].text, out int selectedBaud))
            {
                serialManager.baudRate = selectedBaud;

                if (serialManager.serial_connected)
                {
                    serialManager.StopSerial();
                    serialManager.StartSerial();
                }

                UpdateReconnectButtonLabel();
            }
        }

        bool IsGibberish(string line)
        {
            if (string.IsNullOrWhiteSpace(line)) return true;
            int printable = 0;
            foreach (char c in line)
                if (c >= 32 && c <= 126) printable++;

            return (float)printable / line.Length < 0.75f;
        }

        void ScrollToBottom()
        {
            Canvas.ForceUpdateCanvases();
            var scrollRect = logText.GetComponentInParent<ScrollRect>();
            if (scrollRect != null)
                scrollRect.verticalNormalizedPosition = 0f;
        }
    }
}
#endif