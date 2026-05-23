using System;
using System.Collections.Generic;
using Unidad.Core.Testing;
using UnityEngine;

namespace UnityPpoRacingTrainer.Core.Ghost.Presentation.Scenarios
{
    /// <summary>
    /// Stand-alone: drives a synthetic drop animation curve and asserts that the
    /// handle reports IsLanded after DropSeconds + IsSettleComplete after settle.
    /// No GameObjects spawn — the presenter is integration-tested in the
    /// director scenario where the live IGameObjectFactory is bound.
    /// </summary>
    internal sealed class GhostSpawnAnimationScenario : DataDrivenScenario
    {
        private IGhostSpawnHandle _handle;
        private float _yAtTouchdown;
        private bool _landedSeen;
        private bool _settleSeen;

        public GhostSpawnAnimationScenario() : base(new TestScenarioDefinition(
            "ghost-spawn-animation",
            "Ghost — Drop Animation Curve",
            "Steps the drop animation in 50 ms ticks and checks landing + settle transitions.",
            Array.Empty<ScenarioParameter>()))
        { }

        protected override void ExecuteInternal(ScenarioParameterOverrides overrides)
        {
            var animator = new GhostSpawnAnimator();
            _handle = animator.PlayDropFromAir(Vector3.zero, 0f);

            const float dt = 0.05f;
            for (int i = 0; i < 60; i++)
            {
                _handle.Update(dt);
                if (!_landedSeen && _handle.IsLanded)
                {
                    _landedSeen = true;
                    _yAtTouchdown = _handle.CurrentYOffset;
                    Debug.Log($"[GhostSpawnScenario] LANDED at tick {i} yOff={_yAtTouchdown:F3}");
                }
                if (!_settleSeen && _handle.IsSettleComplete)
                {
                    _settleSeen = true;
                    Debug.Log($"[GhostSpawnScenario] SETTLED at tick {i} yOff={_handle.CurrentYOffset:F3} alpha={_handle.CurrentAlpha:F3}");
                }
            }
        }

        protected override ScenarioVerificationResult VerifyInternal(ScenarioParameterOverrides overrides)
            => new(new List<ScenarioVerificationResult.CheckResult>
            {
                new("handle constructed", _handle != null, "PlayDropFromAir returned null"),
                new("landed transition observed", _landedSeen, "IsLanded never flipped true"),
                new("settle transition observed", _settleSeen, "IsSettleComplete never flipped true"),
                new("final yOffset is zero", Mathf.Abs(_handle.CurrentYOffset) < 1e-3f,
                    $"expected yOffset ≈ 0 after settle, got {_handle.CurrentYOffset:F3}"),
            });

        protected override void OnCleanup()
        {
            _handle = null;
        }
    }
}
