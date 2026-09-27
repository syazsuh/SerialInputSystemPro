using UnityEngine;
using System.Collections.Generic;

namespace SISPro
{
    public enum SerialTransport
    {
        CustomText, // your own "D2:1;A0:512" line protocol - needs a matching sketch flashed
        Firmata     // drives a board running StandardFirmata directly - no per-project sketch
    }

    [CreateAssetMenu(fileName = "SerialDeviceProfile", menuName = "Serial/Device Profile")]
    public class SerialDeviceProfile : ScriptableObject
    {
        public string deviceID = "Uno";

        [Tooltip("Legacy: contiguous D0..D(n-1) inputs, only used when Digital Pins below is empty " +
                 "(profiles for device classes made by older versions of the sketch generator).")]
        public int digitalCount = 8;

        [Tooltip("Legacy: contiguous A0..A(n-1) inputs, only used when Analog Channels below is empty.")]
        public int analogCount = 6;

        public string layoutName = "UnoInputDevice"; // Input System layout name for this device
        public int baudRate = 115200;

        [Header("Transport")]
        [Tooltip("CustomText: your own line protocol, needs a matching sketch flashed per project. " +
                 "Firmata: drives a board running StandardFirmata directly at runtime - flash it once, " +
                 "never touch firmware again. Use the Serial Capability Explorer to discover pins first.")]
        public SerialTransport transport = SerialTransport.CustomText;

        [Header("Digital Input Polarity")]
        [Tooltip("On (default): a pin reading LOW (0) counts as PRESSED. This is correct for the usual " +
                 "wiring - button between the pin and GND, using the board's internal pull-up resistor - " +
                 "which is what both the Firmata transport (pins set to PULLUP) and the generated sketch " +
                 "(INPUT_PULLUP) use. Turn off only if your inputs are wired to 5V/3.3V with an external " +
                 "pull-down resistor, so HIGH (1) means pressed.")]
        public bool digitalActiveLow = true;

        [Header("Discovered Inputs (Serial Capability Explorer)")]
        [Tooltip("Actual microcontroller pin numbers usable as digital INPUT, as reported by the board itself. " +
                 "Sparse and non-contiguous by design - a Mega's usable pins are not 0..N.")]
        public List<int> digitalPins = new List<int>();

        [Tooltip("Analog channel indices (0 = A0, 1 = A1, ...) used for analog INPUT, from the board's analog mapping response.")]
        public List<int> analogChannels = new List<int>();

        [Tooltip("Parallel to analogChannels: the physical pin number each channel maps to (analogChannelPins[i] " +
                 "is the pin for analogChannels[i]). Populated by the Capability Explorer, used to detect a pin " +
                 "being claimed by both an analog-input role and a digital/output role at the same time.")]
        public List<int> analogChannelPins = new List<int>();

        [Tooltip("Max raw ADC value across all analog-capable pins (1023 for 10-bit boards like Uno/Mega/Nano, " +
                 "4095 for 12-bit boards like ESP32/Due). Auto-filled by the Capability Explorer - don't guess this by hand.")]
        public int analogMaxValue = 1023;

        [Header("Outputs (Firmata transport only)")]
        [Tooltip("Pins to drive as plain digital OUTPUT (on/off) - relays, LEDs, solenoids. " +
                 "Configured via SerialManager.SetDigitalOutput(pin, state). Must not overlap digitalPins.")]
        public List<int> digitalOutputPins = new List<int>();

        [Tooltip("Pins to drive as PWM OUTPUT (0-255) - motor drivers, dimmable LEDs. " +
                 "Configured via SerialManager.SetPwmOutput(pin, value). Pin must be PWM-capable on the board.")]
        public List<int> pwmOutputPins = new List<int>();

        [Tooltip("Pins to drive as SERVO OUTPUT (0-180 degrees). " +
                 "Configured via SerialManager.SetServoOutput(pin, angleDegrees).")]
        public List<int> servoOutputPins = new List<int>();

        /// <summary>
        /// True once this profile has explicit pin lists (from the Capability Explorer or
        /// the sketch generator) rather than only the legacy digitalCount/analogCount.
        /// Consumers (SerialDeviceLayoutRegistry, SISProInputActionsBuilder,
        /// SerialToInputSystemAdapter, and SerialManager's Firmata setup) prefer this data
        /// when present. The Firmata transport requires this to be populated for inputs -
        /// there's no other source for which pins to enable reporting on.
        /// </summary>
        public bool HasDiscoveredPins => digitalPins.Count > 0 || analogChannels.Count > 0;

        /// <summary>True if any output pin (digital, PWM, or servo) has been configured.</summary>
        public bool HasOutputPins => digitalOutputPins.Count > 0 || pwmOutputPins.Count > 0 || servoOutputPins.Count > 0;

        /// <summary>
        /// Checks that no physical pin has been assigned more than one role across
        /// digitalPins (input), digitalOutputPins, pwmOutputPins, servoOutputPins, and
        /// analogChannelPins (the pins behind analogChannels). A pin can only ever be
        /// configured as one Firmata pin mode at a time - SET_PIN_MODE for a pin silently
        /// overwrites whatever mode was set for it before, so a double assignment doesn't
        /// error on the wire, it just quietly reconfigures a pin to something other than
        /// what one of its two "owners" expects. That's a real hazard on a physical rig
        /// (a pin wired to a switch could end up driven as an output), so this is checked
        /// both here (editor-time, via OnValidate) and again in SerialManager right before
        /// any pin mode is actually sent to hardware.
        /// </summary>
        public bool ValidatePinAssignments(out List<string> conflicts, out HashSet<int> conflictingPins)
        {
            var pinRoles = new Dictionary<int, List<string>>();
            conflictingPins = new HashSet<int>();
            conflicts = new List<string>();

            void Record(int pin, string role)
            {
                if (!pinRoles.TryGetValue(pin, out var roles))
                {
                    roles = new List<string>();
                    pinRoles[pin] = roles;
                }
                roles.Add(role);
            }

            foreach (var pin in digitalPins) Record(pin, "digital input");
            foreach (var pin in digitalOutputPins) Record(pin, "digital output");
            foreach (var pin in pwmOutputPins) Record(pin, "PWM output");
            foreach (var pin in servoOutputPins) Record(pin, "servo output");

            // analogChannelPins is parallel to analogChannels: index i's channel maps to
            // physical pin analogChannelPins[i]. Only checked where both lists agree in
            // length - an older profile that never had this field populated just skips
            // analog pins in this check rather than throwing.
            for (int i = 0; i < analogChannels.Count && i < analogChannelPins.Count; i++)
                Record(analogChannelPins[i], $"analog input (A{analogChannels[i]})");

            foreach (var kvp in pinRoles)
            {
                if (kvp.Value.Count > 1)
                {
                    conflictingPins.Add(kvp.Key);
                    conflicts.Add($"Pin {kvp.Key} is assigned to multiple roles: {string.Join(", ", kvp.Value)}.");
                }
            }

            return conflicts.Count == 0;
        }

#if UNITY_EDITOR
        /// <summary>
        /// Re-registers this profile's dynamic Input System layout, and re-checks for
        /// pin role conflicts, the moment anything changes in the Inspector - e.g. after
        /// the Capability Explorer applies new data, or a manual tweak to any pin list.
        /// No recompile, no Play Mode needed.
        /// </summary>
        void OnValidate()
        {
            SerialDeviceLayoutRegistry.EnsureRegistered(this);

            if (!ValidatePinAssignments(out var conflicts, out _))
            {
                foreach (var c in conflicts)
                    Debug.LogWarning($"[SISPro] {name}: {c}");
            }
        }
#endif
    }
}