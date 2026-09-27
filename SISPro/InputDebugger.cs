using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Serialization;

namespace SISPro
{
    /// <summary>
    /// Shows an action's live value in the Inspector - no UI needed.
    /// </summary>
    public class InputDebugger : MonoBehaviour
    {
        public InputActionReference actionReference; // Assign in Inspector

        [Header("Live Value from Serial")]
        [FormerlySerializedAs("outputText")]
        [Tooltip("Read-only: updated every frame while playing.")]
        public string liveValue;

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
            if (action != null)
                liveValue = action.ReadValue<float>().ToString("F2");
        }
    }
}