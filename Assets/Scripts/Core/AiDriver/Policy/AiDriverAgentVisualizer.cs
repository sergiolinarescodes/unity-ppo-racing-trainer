using UnityPpoRacingTrainer.Core.AiDriver.Physics;
using Reflex.Extensions;
using UnityEngine;

namespace UnityPpoRacingTrainer.Core.AiDriver.Policy
{
    /// <summary>
    /// Renders a sibling <see cref="AiDriverAgentBehaviour"/>'s simulated car as a
    /// coloured cube so live training is actually watchable. The agent prefab itself
    /// has no mesh — the policy service drives <c>ICarSimulationService</c> in pure
    /// data, decoupled from any visual. Attached at runtime by
    /// <c>TrainerBootstrap</c> so player builds (which don't load the trainer scene)
    /// never pull this in.
    /// </summary>
    public sealed class AiDriverAgentVisualizer : MonoBehaviour
    {
        private IAiDriverAgentRef _agent;
        private ICarSimulationService _carSim;
        private IAiDriverPolicyService _policy;
        private GameObject _visual;

        // Live telemetry. Populated each LateUpdate, read-only in the
        // Inspector during Play — do NOT edit. Lets you watch per-clone
        // dynamics (speed, accel, slip, throttle/steer commands, health)
        // in the Inspector while the trainer runs.
        [Header("Live telemetry (read-only during Play)")]
        [SerializeField] private float speed_mPerSec;
        [SerializeField] private float speedFraction;
        [SerializeField] private float acceleration_mPerSec2;
        [SerializeField] private float longVel_mPerSec;
        [SerializeField] private float latVel_mPerSec;
        [SerializeField] private float slipRatio;
        [SerializeField] private float yawRate_radPerSec;
        [SerializeField] private float headingDeg;
        [SerializeField] private float steerAngleDeg;
        [SerializeField] private float steerCmd;
        [SerializeField] private float throttleCmd;
        [SerializeField] private float commandedEngineForce;
        [SerializeField] private float boostBudget;
        [SerializeField] private float health;
        [SerializeField] private bool onGround;

        // PPO observation view — the exact 25 floats fed to the policy
        // network on the last decision tick. All values normalized and
        // clamped per RacingObservationLayout. Useful for debugging
        // questions like "why didn't the agent see that wall coming?"
        // since indices 17-23 hold the wall feeler-ray occupancies
        // (1 = wall touching nose, 0 = clear).
        [Header("PPO observation (what policy sees, per-decision)")]
        [SerializeField] private float ppo_00_longVelNorm;
        [SerializeField] private float ppo_01_latVelNorm;
        [SerializeField] private float ppo_02_yawRateNorm;
        [SerializeField] private float ppo_03_signedLatNorm;
        [SerializeField] private float ppo_04_headingErrNorm;
        [SerializeField] private float ppo_05_prevSteer;
        [SerializeField] private float ppo_06_prevThrottle;
        [SerializeField] private float ppo_07_anchor0_curvature;
        [SerializeField] private float ppo_08_anchor0_halfWidth;
        [SerializeField] private float ppo_09_anchor1_curvature;
        [SerializeField] private float ppo_10_anchor1_halfWidth;
        [SerializeField] private float ppo_11_anchor2_curvature;
        [SerializeField] private float ppo_12_anchor2_halfWidth;
        [SerializeField] private float ppo_13_anchor3_curvature;
        [SerializeField] private float ppo_14_anchor3_halfWidth;
        [SerializeField] private float ppo_15_anchor4_curvature;
        [SerializeField] private float ppo_16_anchor4_halfWidth;
        [SerializeField] private float ppo_17_wallRay_neg90;
        [SerializeField] private float ppo_18_wallRay_neg45;
        [SerializeField] private float ppo_19_wallRay_neg22;
        [SerializeField] private float ppo_20_wallRay_0;
        [SerializeField] private float ppo_21_wallRay_pos22;
        [SerializeField] private float ppo_22_wallRay_pos45;
        [SerializeField] private float ppo_23_wallRay_pos90;
        [SerializeField] private float ppo_24_surfaceCode;

        private float _prevSpeed;
        private float _prevHeading;
        private bool _haveTelemetryHistory;

        private void Awake()
        {
            // Interface lookup — Unity 2021+ resolves IAiDriverAgentRef
            // against any matching concrete component on the prefab. The
            // interface indirection lets any snapshot's Behaviour class plug
            // in without per-version null-coalesce chains here.
            _agent = GetComponent<IAiDriverAgentRef>();
            var container = gameObject.scene.GetSceneContainer();
            _carSim = container?.Resolve<ICarSimulationService>();
            _policy = container?.Resolve<IAiDriverPolicyService>();

            _visual = GameObject.CreatePrimitive(PrimitiveType.Cube);
            _visual.name = "[AgentVisual]";
            _visual.transform.SetParent(transform, worldPositionStays: false);
            _visual.transform.localScale = new Vector3(0.5f, 0.4f, 0.8f);

            var renderer = _visual.GetComponent<Renderer>();
            if (renderer != null)
            {
                var shader = Shader.Find("Universal Render Pipeline/Lit")
                             ?? Shader.Find("Standard")
                             ?? Shader.Find("Hidden/InternalErrorShader");
                if (shader != null)
                {
                    var mat = new Material(shader) { name = "AiDriverAgentVisual_Mat", color = ColorForAgent() };
                    renderer.sharedMaterial = mat;
                }
            }
        }

        // Distinct colour per agent. CarId hasn't been assigned at Awake yet
        // (RegisterAgent runs in the agent's Initialize, which fires AFTER our
        // Awake), so we hash the GameObject's instanceID — stable for the
        // session, distinct per spawn, no extra wiring.
        private Color ColorForAgent()
        {
#pragma warning disable CS0618 // GetInstanceID() flagged obsolete in Unity 6 in favor of GetEntityId(); EntityId is a struct not castable to uint, and the instance-id hashing here just needs a stable session-scoped int.
            uint h = (uint)gameObject.GetInstanceID();
#pragma warning restore CS0618
            float hue = (h * 2654435761u % 360u) / 360f;
            return Color.HSVToRGB(hue, 0.85f, 0.95f);
        }

        private void LateUpdate()
        {
            if (_agent == null || _carSim == null || !_agent.IsRegistered) return;
            if (!_carSim.TryGetState(_agent.CarId, out var state)) return;

            // Lift slightly so the cube sits on top of the road slab, not z-fighting it.
            transform.position = state.Position + new Vector3(0f, 0.1f, 0f);
            transform.rotation = Quaternion.Euler(0f, state.Heading * Mathf.Rad2Deg, 0f);

            // Live telemetry. dt = Time.deltaTime is fine for a viz-only readout
            // even though the sim runs in FixedTick — it just samples whatever the
            // physics produced last fixed step.
            float dt = Mathf.Max(1e-4f, Time.deltaTime);
            float speed = state.VelocityXZ.magnitude;
            float forwardX = Mathf.Sin(state.Heading);
            float forwardZ = Mathf.Cos(state.Heading);
            float rightX = forwardZ;
            float rightZ = -forwardX;
            float longVel = state.VelocityXZ.x * forwardX + state.VelocityXZ.y * forwardZ;
            float latVel = state.VelocityXZ.x * rightX + state.VelocityXZ.y * rightZ;

            float defaultMaxSpeed = AiDriverPhysicsDefaults.Latest.MaxSpeed;
            float defaultMaxAccel = AiDriverPhysicsDefaults.Latest.MaxAccel;
            float defaultMaxSteer = AiDriverPhysicsDefaults.Latest.MaxSteer;

            speed_mPerSec = speed;
            speedFraction = defaultMaxSpeed > 0f ? Mathf.Clamp01(speed / defaultMaxSpeed) : 0f;
            longVel_mPerSec = longVel;
            latVel_mPerSec = latVel;
            slipRatio = speed > 0.1f ? Mathf.Clamp01(Mathf.Abs(latVel) / speed) : 0f;
            headingDeg = state.Heading * Mathf.Rad2Deg;
            steerAngleDeg = state.SteerAngle * Mathf.Rad2Deg;
            steerCmd = _agent.LastSteerCmd;
            throttleCmd = _agent.LastThrottleCmd;
            commandedEngineForce = throttleCmd * defaultMaxAccel;
            boostBudget = state.BoostBudget;
            onGround = state.OnGround;
            if (_carSim.TryGetHealth(_agent.CarId, out var h)) health = h;

            if (_haveTelemetryHistory)
            {
                acceleration_mPerSec2 = (speed - _prevSpeed) / dt;
                float headingDelta = Mathf.DeltaAngle(_prevHeading * Mathf.Rad2Deg,
                                                      state.Heading * Mathf.Rad2Deg) * Mathf.Deg2Rad;
                yawRate_radPerSec = headingDelta / dt;
            }
            _prevSpeed = speed;
            _prevHeading = state.Heading;
            _haveTelemetryHistory = true;
            // suppress "assigned but unused" hints — Unity reads via SerializeField
            _ = defaultMaxSteer;

            // Decode the cached PPO observation. CollectObservations runs on
            // the ML-Agents decision tick (every DecisionPeriod=5 sim ticks),
            // so this view updates once per ~100ms — distinct from the per-
            // frame physics view above.
            if (_policy != null)
            {
                var obs = _policy.GetLastObservation(_agent.CarId);
                if (obs != null && obs.Count >= 25)
                {
                    ppo_00_longVelNorm       = obs[0];
                    ppo_01_latVelNorm        = obs[1];
                    ppo_02_yawRateNorm       = obs[2];
                    ppo_03_signedLatNorm     = obs[3];
                    ppo_04_headingErrNorm    = obs[4];
                    ppo_05_prevSteer         = obs[5];
                    ppo_06_prevThrottle      = obs[6];
                    ppo_07_anchor0_curvature = obs[7];
                    ppo_08_anchor0_halfWidth = obs[8];
                    ppo_09_anchor1_curvature = obs[9];
                    ppo_10_anchor1_halfWidth = obs[10];
                    ppo_11_anchor2_curvature = obs[11];
                    ppo_12_anchor2_halfWidth = obs[12];
                    ppo_13_anchor3_curvature = obs[13];
                    ppo_14_anchor3_halfWidth = obs[14];
                    ppo_15_anchor4_curvature = obs[15];
                    ppo_16_anchor4_halfWidth = obs[16];
                    ppo_17_wallRay_neg90     = obs[17];
                    ppo_18_wallRay_neg45     = obs[18];
                    ppo_19_wallRay_neg22     = obs[19];
                    ppo_20_wallRay_0         = obs[20];
                    ppo_21_wallRay_pos22     = obs[21];
                    ppo_22_wallRay_pos45     = obs[22];
                    ppo_23_wallRay_pos90     = obs[23];
                    ppo_24_surfaceCode       = obs[24];
                }
            }
        }

        private void OnDestroy()
        {
            if (_visual != null) Destroy(_visual);
        }
    }
}
