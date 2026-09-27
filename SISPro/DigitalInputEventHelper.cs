using UnityEngine;
using UnityEngine.Events;
using UnityEngine.InputSystem;

namespace SISPro
{
    public class DigitalInputEventHelper : MonoBehaviour
    {
        [Tooltip("Input Action (Button) to listen to")]
        public InputActionReference actionReference;

        public UnityEvent digitalON;
        public UnityEvent digitalOFF;

        private InputAction action;

        private void OnEnable()
        {
            if (actionReference == null || actionReference.action == null)
            {
                Debug.LogWarning($"{nameof(DigitalInputEventHelper)}: No InputActionReference assigned.");
                return;
            }

            action = actionReference.action;

            // Listen to press and release
            action.started += OnActionStarted;   // when actuation crosses press threshold
            action.canceled += OnActionCanceled;  // when released
            action.Enable();
        }

        private void OnDisable()
        {
            if (action == null) return;

            // Unsubscribe only. The action is shared by every component that references
            // it - calling Disable() here would silence all the other listeners too.
            action.started -= OnActionStarted;
            action.canceled -= OnActionCanceled;
            action = null;
        }

        private void OnActionStarted(InputAction.CallbackContext ctx)
        {
            digitalON?.Invoke();
        }

        private void OnActionCanceled(InputAction.CallbackContext ctx)
        {
            digitalOFF?.Invoke();
        }
    }
}