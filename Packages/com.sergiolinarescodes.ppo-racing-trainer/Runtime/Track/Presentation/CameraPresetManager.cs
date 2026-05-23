using UnityEngine;
using UnityEngine.InputSystem;

namespace UnityPpoRacingTrainer.Core.Track.Presentation
{
    /// <summary>
    /// Captures the current camera pose once, applies an arbitrary preset, and
    /// restores the captured pose on dispose. Reusable across every scenario that
    /// wants to drop the user into a fixed iso framing while it runs.
    /// <para>
    /// Also drives editor camera control: <see cref="Tick"/> reads WASD for pan and
    /// the mouse scroll wheel for dolly-zoom. Rotation is left untouched so the iso
    /// preset's framing angle persists. Hold Shift while panning to fast-pan (×3).
    /// </para>
    /// </summary>
    internal sealed class CameraPresetManager
    {
        // Tile-cells per second at base speed. ~8 keeps the small editor terrains
        // (12×12 — 25×25) traversable in 2-3 seconds, fast enough not to feel
        // sluggish but slow enough to stop on a target tile.
        private const float PanSpeed = 8f;
        // World-units per scroll click. The Input System reports scroll deltas in
        // multiples of 120; 3.0 ≈ 360 units per click — large bursts that the
        // ZoomSmoothing exponential bleeds out into one continuous glide rather
        // than a teleport.
        private const float ZoomStep = 3.0f;
        // Higher = snappier; lower = floatier. 15/s reaches ~63% of the target
        // in ~67ms — feels close to "instant" on a scroll click but still smooths
        // a fast scroll burst into one continuous glide.
        private const float ZoomSmoothing = 15f;

        private readonly Camera _camera;
        private Vector3 _originalPosition;
        private Quaternion _originalRotation;
        private bool _captured;
        // Pending dolly delta accumulated from scroll input and bled into the
        // camera position over multiple frames via exponential smoothing. Lets a
        // single fast scroll burst translate into one continuous glide instead
        // of a stutter-step per frame.
        private float _zoomAccumulator;

        public CameraPresetManager(Camera camera)
        {
            _camera = camera;
        }

        public bool HasCamera => _camera != null;

        public void ApplyLookAt(Vector3 cameraPosition, Vector3 lookTarget)
        {
            if (_camera == null) return;
            CaptureIfNeeded();
            _camera.transform.position = cameraPosition;
            _camera.transform.LookAt(lookTarget);
        }

        /// <summary>
        /// Per-frame editor camera control. WASD pans on the world XZ plane along
        /// the camera's projected forward/right axes (so "W" moves toward where the
        /// camera is looking, regardless of iso angle). Scroll wheel dollies along
        /// the camera's forward vector — zoom in / zoom out without re-aiming. Hold
        /// Shift to pan ×3. Camera rotation is never changed here, so the iso
        /// preset's pitch/yaw is preserved.
        /// </summary>
        public void Tick(float deltaTime)
        {
            if (_camera == null) return;
            // Tick may fire before any preset has been applied (e.g. scenario
            // didn't call ApplyLookAt). In that case there's no captured original
            // pose; that's fine — we only mutate transform.position here, not the
            // capture state.
            var t = _camera.transform;

            var kbd = Keyboard.current;
            if (kbd != null)
            {
                Vector3 fwd = t.forward; fwd.y = 0f;
                Vector3 right = t.right; right.y = 0f;
                if (fwd.sqrMagnitude > 1e-6f) fwd.Normalize();
                if (right.sqrMagnitude > 1e-6f) right.Normalize();

                Vector3 pan = Vector3.zero;
                if (kbd.wKey.isPressed) pan += fwd;
                if (kbd.sKey.isPressed) pan -= fwd;
                if (kbd.dKey.isPressed) pan += right;
                if (kbd.aKey.isPressed) pan -= right;

                if (pan.sqrMagnitude > 0f)
                {
                    float speed = (kbd.leftShiftKey.isPressed || kbd.rightShiftKey.isPressed)
                        ? PanSpeed * 3f
                        : PanSpeed;
                    t.position += pan.normalized * (speed * deltaTime);
                }
            }

            var mouse = Mouse.current;
            if (mouse != null)
            {
                float scroll = mouse.scroll.ReadValue().y;
                if (Mathf.Abs(scroll) > 0.01f)
                    _zoomAccumulator += scroll * ZoomStep;
            }

            if (Mathf.Abs(_zoomAccumulator) > 0.0001f)
            {
                // Frame-rate-independent exponential smoothing toward the target.
                float k = 1f - Mathf.Exp(-ZoomSmoothing * deltaTime);
                float step = _zoomAccumulator * k;
                t.position += t.forward * step;
                _zoomAccumulator -= step;
            }
            else
            {
                _zoomAccumulator = 0f;
            }
        }

        public void Restore()
        {
            if (!_captured || _camera == null) return;
            _camera.transform.position = _originalPosition;
            _camera.transform.rotation = _originalRotation;
            _captured = false;
        }

        private void CaptureIfNeeded()
        {
            if (_captured) return;
            _originalPosition = _camera.transform.position;
            _originalRotation = _camera.transform.rotation;
            _captured = true;
        }
    }
}
