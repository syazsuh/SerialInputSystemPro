using UnityEngine;
using UnityEngine.InputSystem;
using TMPro;

namespace SISPro
{
    public class UIInputDisplay_InputActionReference : MonoBehaviour
    {
        public InputActionReference actionReference; // Assign in Inspector
        public TextMeshProUGUI outputText;           // Assign in Inspector

        private InputAction action;

        void OnEnable()
        {
            if (actionReference != null && actionReference.action != null)
            {
                action = actionReference.action;
                action.Enable();
            }
        }

        void OnDisable()
        {
            // Don't Disable() the shared action - other components may be using it.
            action = null;
        }

        void Update()
        {
            if (action != null && outputText != null)
            {
                float value = action.ReadValue<float>();
                outputText.text = value.ToString("F2");
            }
        }
    }
}