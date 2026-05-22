using UnityEngine;

namespace UnityPpoRacingTrainer.Core.AiDriver.Policy
{
    /// <summary>
    /// Generic chase camera. Follows <see cref="Target"/> with a configurable
    /// offset (behind + above in the target's local space) and smoothing.
    /// Marked <see cref="ExecuteAlways"/> so it pans during Edit-mode scenarios
    /// — the agent ticks under <c>[ExecuteAlways]</c> proxies and the camera
    /// must keep up without entering Play mode.
    /// On disable the original camera pose is restored so the editor view
    /// doesn't get stranded after the scenario tears down.
    /// </summary>
    [ExecuteAlways]
    [DisallowMultipleComponent]
    public sealed class CarFollowCamera : MonoBehaviour
    {
        [Tooltip("Transform to follow. If null, the component does nothing.")]
        public Transform Target;

        [Tooltip("Offset in the target's local space: x=right, y=up, z=forward (negative = behind).")]
        public Vector3 LocalOffset = new(0f, 1.6f, -3.0f);

        [Tooltip("Position smoothing time (s). 0 = snap.")]
        [Range(0f, 1f)] public float PositionSmoothTime = 0.12f;

        [Tooltip("Rotation smoothing speed (1/s). 0 = snap.")]
        [Range(0f, 30f)] public float RotationLerpSpeed = 8f;

        [Tooltip("Where the camera looks: target position + this world offset.")]
        public Vector3 LookAtOffset = new(0f, 0.4f, 0f);

        private Vector3 _posVel;
        private Vector3 _initialPos;
        private Quaternion _initialRot;
        private bool _initialPoseCached;

        private void OnEnable()
        {
            if (!_initialPoseCached)
            {
                _initialPos = transform.position;
                _initialRot = transform.rotation;
                _initialPoseCached = true;
            }
        }

        private void OnDisable()
        {
            if (_initialPoseCached)
            {
                transform.position = _initialPos;
                transform.rotation = _initialRot;
            }
        }

        private void LateUpdate()
        {
            if (Target == null) return;

            // Compute desired pose from the target's heading.
            var basis = Quaternion.Euler(0f, Target.eulerAngles.y, 0f);
            Vector3 desiredPos = Target.position + basis * LocalOffset;
            Vector3 lookTarget = Target.position + LookAtOffset;

            // Edit-mode dt — Time.deltaTime is 0 on the first edit-mode frame.
            float dt = Application.isPlaying ? Time.deltaTime : 1f / 60f;

            transform.position = PositionSmoothTime > 0f
                ? Vector3.SmoothDamp(transform.position, desiredPos, ref _posVel, PositionSmoothTime, Mathf.Infinity, dt)
                : desiredPos;

            Quaternion desiredRot = Quaternion.LookRotation(lookTarget - transform.position, Vector3.up);
            transform.rotation = RotationLerpSpeed > 0f
                ? Quaternion.Slerp(transform.rotation, desiredRot, 1f - Mathf.Exp(-RotationLerpSpeed * dt))
                : desiredRot;
        }
    }
}
