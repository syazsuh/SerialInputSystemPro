using UnityEngine;
using UnityEngine.Events;
using UnityEngine.InputSystem;

namespace SISPro
{
    public class AnalogInputEvent : MonoBehaviour
    {
        public InputActionReference actionReference; // Assign in Inspector
        private InputAction action;

        public UnityEvent<float> onValueChanged;

        void OnEnable()
        {
            if (actionReference == null || actionReference.action == null)
            {
                Debug.LogWarning($"{nameof(AnalogInputEvent)}: No InputActionReference assigned.");
                return;
            }

            action = actionReference.action;
            action.performed += OnActionChanged;
            action.canceled += OnActionChanged; // so we also get "0" when released
            action.Enable();
        }

        void OnDisable()
        {
            if (action == null) return;

            // Unsubscribe only - Disable() would stop the shared action for every other listener.
            action.performed -= OnActionChanged;
            action.canceled -= OnActionChanged;
            action = null;
        }

        private void OnActionChanged(InputAction.CallbackContext context)
        {
            onValueChanged?.Invoke(context.ReadValue<float>());
        }
    }
}