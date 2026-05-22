using System.Collections.Generic;
using UnityPpoRacingTrainer.Core.AiDriver.Loop;
using UnityPpoRacingTrainer.Core.AiDriver.Physics.Modifiers;
using UnityPpoRacingTrainer.Core.Track;
using Unidad.Core.Abstractions;
using Unidad.Core.EventBus;
using Unidad.Core.Systems;
using UnityEngine;

namespace UnityPpoRacingTrainer.Core.AiDriver.Physics
{
    internal sealed class CarSimulationService : SystemServiceBase, ICarSimulationService, IFixedTickable
    {
        /// <summary>
        /// Y-coordinate fallback when no track loop is registered. Cars rest on
        /// this plane in scenarios without a generated circuit (heuristic / test).
        /// </summary>
        private const float DefaultGroundY = 0f;

        /// <summary>
        /// Brake input above which a deceleration bonus kicks in (mirrors the
        /// TirePhysics hard-brake wear curve at 0.90). Brake input below stays
        /// linear in <c>MaxBrake</c>.
        /// </summary>
        private const float HardBrakeThreshold = 0.9f;

        /// <summary>
        /// Peak multiplier on <c>MaxBrake</c> at full brake (input = 1.0).
        /// Lerps 1.0× at the threshold up to <c>HardBrakePeakDecelMul</c> at
        /// full pedal. Lets late-braking recover meters into a curve at the
        /// cost of the 2.5× hard-brake tire wear (and 5× blockade) already
        /// applied in <see cref="Tires.TirePhysicsService"/>.
        /// UP: stopping power scales hard with pedal — passing into corners
        /// becomes cheap meters, paid in rubber.
        /// DOWN: brake response is linear; threshold braking is mechanical
        /// only, no in-physics carrot.
        /// </summary>
        private const float HardBrakePeakDecelMul = 1.3f;

        /// <summary>
        /// Per-tick diagnostic log cadence, in ticks (50 ≈ 1 s at 50 Hz physics).
        /// Set to ≤ 0 to silence. Useful for hunting timing bugs at the cost of
        /// flooding the console.
        /// UP: log fires less often — quieter, easier to skim.
        /// DOWN (but > 0): more granular per-tick traces.
        /// </summary>
        private const int LogTickInterval = 0;

        private readonly ITrackQueryService _trackQuery;
        private readonly ITrackCollisionService _collision;
        private readonly ICarPhysicsModifierAggregator _modifiers;
        private readonly Dictionary<CarId, Entry> _entries = new();
        private readonly List<CarId> _activeCarsView = new();
        private int _nextId = 1;

        public CarSimulationService(IEventBus eventBus, ITrackQueryService trackQuery,
            ITrackCollisionService collision = null,
            ICarPhysicsModifierAggregator modifiers = null) : base(eventBus)
        {
            _trackQuery = trackQuery;
            _collision = collision;
            _modifiers = modifiers;
        }

        public IReadOnlyCollection<CarId> ActiveCars => _entries.Keys;

        public CarId Spawn(Vector3 position, float heading, CarParameters parameters)
        {
            var id = new CarId(_nextId++);
            _entries[id] = new Entry
            {
                Parameters = parameters,
                State = new CarState
                {
                    Position = position,
                    Heading = heading,
                    BoostBudget = 1f,
                    OnGround = true,
                    LastAnchorIndex = -1
                },
                Input = default,
                WasOffTrack = false,
                LastSurface = SurfaceKind.Asphalt,
                WasOnKerb = false,
                Health = 1f
            };
            Publish(new CarSpawnedEvent(id, position, heading));
            return id;
        }

        public void Despawn(CarId id)
        {
            if (_entries.Remove(id))
            {
                Publish(new CarDespawnedEvent(id));
            }
        }

        public void SetInput(CarId id, DriverInput input)
        {
            if (!_entries.TryGetValue(id, out var e)) return;
            e.Input = input;
            _entries[id] = e;
        }

        public bool TryGetState(CarId id, out CarState state)
        {
            if (_entries.TryGetValue(id, out var e))
            {
                state = e.State;
                return true;
            }
            state = default;
            return false;
        }

        public void TeleportTo(CarId id, Vector3 position, float heading)
        {
            if (!_entries.TryGetValue(id, out var e)) return;
            e.State.Position = position;
            e.State.Heading = heading;
            e.State.VelocityXZ = Vector2.zero;
            e.State.VerticalVelocity = 0f;
            e.State.SteerAngle = 0f;
            e.State.OnGround = true;
            e.State.LastAnchorIndex = -1;
            e.State.LapDistance = 0f;
            e.WasOffTrack = false;
            e.LastSurface = SurfaceKind.Asphalt;
            e.WasOnKerb = false;
            e.Health = 1f;
            e.StunCountdown = 0f;
            e.StraightStreakSec = -e.Parameters.StraightLineAeroRecoverySec;
            _entries[id] = e;
        }

        public bool TryGetHealth(CarId id, out float health)
        {
            if (_entries.TryGetValue(id, out var e))
            {
                health = e.Health;
                return true;
            }
            health = 0f;
            return false;
        }

        public void ApplyImpulse(CarId id, Vector2 deltaVelocityXZ)
        {
            if (!_entries.TryGetValue(id, out var e)) return;
            Vector2 v = e.State.VelocityXZ + deltaVelocityXZ;
            float maxSpeed = e.Parameters.MaxSpeed;
            if (maxSpeed > 0f)
            {
                float m = v.magnitude;
                if (m > maxSpeed) v *= maxSpeed / m;
            }
            e.State.VelocityXZ = v;
            _entries[id] = e;
        }

        public void Separate(CarId id, Vector2 worldOffsetXZ)
        {
            if (!_entries.TryGetValue(id, out var e)) return;
            e.State.Position = new Vector3(
                e.State.Position.x + worldOffsetXZ.x,
                e.State.Position.y,
                e.State.Position.z + worldOffsetXZ.y);
            _entries[id] = e;
        }

        public void ApplyDamage(CarId id, float damageDelta)
        {
            if (!_entries.TryGetValue(id, out var e)) return;
            e.Health = Mathf.Clamp01(e.Health - damageDelta);
            _entries[id] = e;
        }

        public void SetStun(CarId id, float seconds)
        {
            if (seconds <= 0f) return;
            if (!_entries.TryGetValue(id, out var e)) return;
            if (e.StunCountdown < seconds) e.StunCountdown = seconds;
            _entries[id] = e;
        }

        public void PerturbHeading(CarId id, float deltaRadians)
        {
            if (!_entries.TryGetValue(id, out var e)) return;
            e.State.Heading += deltaRadians;
            _entries[id] = e;
        }

        public bool TryGetParameters(CarId id, out CarParameters parameters)
        {
            if (_entries.TryGetValue(id, out var e))
            {
                parameters = e.Parameters;
                return true;
            }
            parameters = default;
            return false;
        }

        public void FixedTick(float fixedDeltaTime)
        {
            // Snapshot keys so callbacks (e.g. lap-complete) that despawn don't mutate
            // the dictionary mid-iteration.
            _activeCarsView.Clear();
            foreach (var id in _entries.Keys) _activeCarsView.Add(id);

            for (int i = 0; i < _activeCarsView.Count; i++)
            {
                var id = _activeCarsView[i];
                if (!_entries.TryGetValue(id, out var entry)) continue;
                Step(ref entry, fixedDeltaTime, id);
                _entries[id] = entry;
            }
        }

        private void Step(ref Entry e, float dt, CarId id)
        {
            var p = _modifiers != null ? _modifiers.Apply(id, e.Parameters) : e.Parameters;
            var input = e.Input;

            // Capture pre-step heading + lateral velocity so we can publish a
            // lateral-acceleration estimate for tire/fuel/draft services. Yaw-
            // rate × forward-speed approximates centripetal G; cheaper than
            // measuring frame-over-frame and immune to position quantization.
            float headingPrev = e.State.Heading;
            float slipForEvent = 0f;

            // Stun lockout: while StunCountdown > 0 the car ignores pilot
            // input. Steer + throttle zeroed so the post-bounce velocity
            // decays via drag and re-grip; the car can't power straight
            // back into the wall.
            if (e.StunCountdown > 0f)
            {
                e.StunCountdown = Mathf.Max(0f, e.StunCountdown - dt);
                input = new DriverInput(0f, 0f, false);
            }

            // 0. Pre-step state — last tick's off-track + surface seed this tick's
            //    surface model. We project once at the end and feed the result back
            //    to the next tick.
            bool offTrack = e.WasOffTrack;
            SurfaceKind surface = e.LastSurface;

            // 1. Steering toward target — F1-style traction-circle. Accelerating
            //    hard at speed burns longitudinal grip → less is available
            //    laterally → commanded steer angle is capped. Braking also
            //    consumes a (weighted) fraction of the budget. High-speed steer
            //    rate is slowed to suppress high-frequency wobble.
            float speedNow = e.State.VelocityXZ.magnitude;
            float speedFracForCircle = p.MaxSpeed > 0f ? speedNow / p.MaxSpeed : 0f;
            speedFracForCircle = Mathf.Clamp01(speedFracForCircle);
            float accelLoad = Mathf.Max(0f, input.Throttle);
            // Trail-brake penalty. Braking ALSO consumes the traction budget,
            // weighted by TrailBrakeAuthorityWeight (< 1) so it costs less than
            // equivalent throttle. Brake-on-straight stays free (no steer to
            // lose). Brake-into-corner at speed reduces steer authority —
            // favours the classical racing line: brake on straight, lift,
            // turn-in unloaded, throttle on exit.
            float brakeLoad = Mathf.Max(0f, -input.Throttle) * p.TrailBrakeAuthorityWeight;
            float circleDemand = p.TractionCircleGain * (accelLoad + brakeLoad) * speedFracForCircle;
            float allowedFrac = Mathf.Sqrt(Mathf.Max(0f, 1f - circleDemand * circleDemand));
            allowedFrac = Mathf.Max(p.MinSteerAuthority, allowedFrac);
            float effectiveMaxSteer = p.MaxSteer * allowedFrac;
            float targetSteer = Mathf.Clamp(input.Steer, -1f, 1f) * effectiveMaxSteer;
            float rateScale = 1f - p.HighSpeedSteerRateFactor
                                   * speedFracForCircle * speedFracForCircle;
            float effSteerRate = p.SteerRate * Mathf.Max(0.25f, rateScale);
            e.State.SteerAngle = Mathf.MoveTowards(e.State.SteerAngle, targetSteer, effSteerRate * dt);

            // Straight-line aero streak. Track sustained low-steer cruise;
            // while the streak is active the speed cap rises (applied below).
            // Curving resets to a negative recovery delay so the next ramp-up
            // takes time to rebuild — no instant pop out of corners.
            if (p.StraightLineAeroSteerThreshold > 0f)
            {
                if (Mathf.Abs(e.State.SteerAngle) < p.StraightLineAeroSteerThreshold)
                    e.StraightStreakSec += dt;
                else
                    e.StraightStreakSec = -p.StraightLineAeroRecoverySec;
            }

            // 2. Boost budget + active-boost flag.
            bool boosting = false;
            if (input.Boost && e.State.BoostBudget > 0f)
            {
                boosting = true;
                e.State.BoostBudget = Mathf.Max(0f, e.State.BoostBudget - dt / p.BoostDurationSec);
            }
            else
            {
                e.State.BoostBudget = Mathf.Min(1f, e.State.BoostBudget + p.BoostRechargeRate * dt);
            }

            // 3. Forward speed integration (no reverse gear supported).
            // Hard-brake decel bonus: brake input past HardBrakeThreshold
            // ramps the effective brake force up to HardBrakePeakDecelMul.
            // Tire wear already mirrors this curve (2.5× peak + 5× blockade)
            // so the policy faces a tradeoff: late-braking lets it recover
            // meters into a corner, paid in rubber.
            float brakeInput = Mathf.Max(0f, -input.Throttle);
            float hardBrakeMul = brakeInput > HardBrakeThreshold
                ? Mathf.Lerp(1f, HardBrakePeakDecelMul,
                    (brakeInput - HardBrakeThreshold) / (1f - HardBrakeThreshold))
                : 1f;
            float forwardAcc = boosting
                ? p.MaxAccel + p.BoostThrust
                : input.Throttle >= 0f ? input.Throttle * p.MaxAccel
                                       : input.Throttle * p.MaxBrake * hardBrakeMul;

            // Straight-line aero accel boost: while the straight-streak is
            // ramping (low steer + throttle on), amplify positive throttle
            // accel. Falls off naturally with drag at higher speeds; the
            // intent is to help cars punch out of corners and build speed
            // in the 3–10 m/s band rather than raise the rarely-touched cap.
            if (e.StraightStreakSec > 0f
                && p.StraightLineAeroBoost > 0f
                && p.StraightLineAeroRampSec > 0f
                && input.Throttle > 0f
                && !boosting)
            {
                float aeroRamp = Mathf.Clamp01(e.StraightStreakSec / p.StraightLineAeroRampSec);
                forwardAcc *= 1f + p.StraightLineAeroBoost * aeroRamp;
            }

            float depthRamp = Mathf.Clamp01(e.LastOffTrackDepth);
            float deepDragMul = Mathf.Max(1f, p.OffTrackDragMul);
            float surfaceDragMul = Mathf.Lerp(1f, deepDragMul, depthRamp);
            float deepCap = Mathf.Clamp01(p.OffTrackSpeedCapFrac);
            if (deepCap <= 0f) deepCap = 1f;
            float surfaceCapFrac = Mathf.Lerp(1f, deepCap, depthRamp);

            // Forward speed = velocity projected onto the *pre-yaw* forward
            // axis. Reading magnitude here folds lateral velocity into the
            // longitudinal scalar — under a yaw swing, that promotes lateral
            // motion into forward motion next frame, compounding magnitude
            // unboundedly when the policy steers noisily.
            float forwardXPrev = Mathf.Sin(e.State.Heading);
            float forwardZPrev = Mathf.Cos(e.State.Heading);
            float speed = Mathf.Max(0f,
                e.State.VelocityXZ.x * forwardXPrev + e.State.VelocityXZ.y * forwardZPrev);
            speed = Mathf.Max(0f, speed + (forwardAcc - p.DragCoefficient * surfaceDragMul * speed) * dt);
            if (!offTrack && p.MinCruiseSpeed > 0f && speed < p.MinCruiseSpeed)
            {
                speed = p.MinCruiseSpeed;
            }
            float speedCap = boosting ? p.MaxSpeed * 1.25f : p.MaxSpeed;
            speedCap *= surfaceCapFrac;
            // Note: StraightLineAeroBoost used to raise speedCap here; now it
            // boosts acceleration in the forwardAcc block above (top speed
            // wasn't actually being hit, so the cap boost was dead weight).
            // Health-modulated cap. healthFactor ∈ [MinHealthSpeedFactor, 1].
            // A damaged car physically cannot reach top speed — the racing-line
            // gradient comes from physics, not reward shaping.
            float healthFactor = Mathf.Lerp(p.MinHealthSpeedFactor, 1f, Mathf.Clamp01(e.Health));
            if (healthFactor < 1f) speedCap *= healthFactor;
            speed = Mathf.Min(speed, speedCap);

            // 4. Heading from kinematic bicycle, with steering-authority multiplier.
            float speedFrac = p.MaxSpeed > 0f ? speed / p.MaxSpeed : 0f;
            float steerNorm = p.MaxSteer > 0f
                ? Mathf.Min(1f, Mathf.Abs(e.State.SteerAngle) / p.MaxSteer)
                : 0f;
            float lowSpeedAuthority = 1f + p.LowSpeedTurnBonus * (1f - speedFrac);
            float highSpeedSuppress = 1f + p.SpeedInducedUndersteerGain * speedFrac * speedFrac * steerNorm;
            float effectiveSteer = e.State.SteerAngle * lowSpeedAuthority
                                                       / Mathf.Max(1e-3f, highSpeedSuppress);
            float yawRate = (speed / Mathf.Max(0.01f, p.WheelBase)) * Mathf.Tan(effectiveSteer);
            e.State.Heading += yawRate * dt;

            // 5. Velocity vector — slip-aware grip-lerp. Grip choice now three-way:
            //    off-track grass → OffTrackGripFactor; on-kerb → KerbGripFactor;
            //    otherwise the normal asphalt LateralGripFactor.
            float forwardX = Mathf.Sin(e.State.Heading);
            float forwardZ = Mathf.Cos(e.State.Heading);
            float rightX = forwardZ;       // 90° clockwise from forward in XZ
            float rightZ = -forwardX;

            float baseGrip;
            if (offTrack)
                baseGrip = p.OffTrackGripFactor;
            else if (surface == SurfaceKind.Kerb && p.KerbGripFactor > 0f)
                baseGrip = p.KerbGripFactor;
            else
            {
                baseGrip = p.LateralGripFactor;
                // Off-kerb cornering destabilization. On asphalt, when the car
                // is turning above the deadband, lateral grip scales down
                // linearly with steering. The kerb path above bypasses this,
                // so cars learn to seek the kerb on hard corners.
                if (p.OffKerbCorneringPenalty > 0f && steerNorm > p.OffKerbCorneringSteerThreshold)
                {
                    float t = (steerNorm - p.OffKerbCorneringSteerThreshold)
                              / Mathf.Max(1e-3f, 1f - p.OffKerbCorneringSteerThreshold);
                    baseGrip *= 1f - p.OffKerbCorneringPenalty * Mathf.Clamp01(t);
                }

                // Speed-scaled grip — encodes the centripetal v²/r cost.
                // Kerb and off-track paths are handled above so their own
                // factors are unaffected by this term.
                if (p.SpeedLateralGripScale > 0f)
                    baseGrip /= 1f + p.SpeedLateralGripScale * speedFrac * speedFrac;
            }

            Vector2 newVel;
            if (baseGrip <= 0f)
            {
                newVel = new Vector2(forwardX, forwardZ) * speed;
            }
            else
            {
                float longVel = e.State.VelocityXZ.x * forwardX + e.State.VelocityXZ.y * forwardZ;
                float latVel = e.State.VelocityXZ.x * rightX + e.State.VelocityXZ.y * rightZ;

                float slip = speed > 0.1f ? Mathf.Min(1f, Mathf.Abs(latVel) / speed) : 0f;
                slipForEvent = slip;
                float effGrip = baseGrip * Mathf.Lerp(1f, p.SlipReleaseFactor, slip);
                float decay = Mathf.Exp(-effGrip * dt);
                latVel *= decay;
                longVel = speed;

                newVel = new Vector2(
                    forwardX * longVel + rightX * latVel,
                    forwardZ * longVel + rightZ * latVel);
            }
            e.State.VelocityXZ = newVel;

            // 6. XZ position integrate.
            e.State.Position.x += newVel.x * dt;
            e.State.Position.z += newVel.y * dt;

            // 6b. Wall collision. Walls are authored geometry per track piece,
            //     registered with the collision service at placement time. A
            //     point-vs-segment query each tick resolves penetration along
            //     the wall normal and damps velocity.
            if (_collision != null)
            {
                if (_collision.TryFindNearestWall(e.State.Position, p.CarCollisionRadius, out var hit))
                {
                    // Push out along the wall normal.
                    e.State.Position.x += hit.Normal.x * hit.Penetration;
                    e.State.Position.z += hit.Normal.y * hit.Penetration;

                    // Reflect-and-damp velocity.
                    Vector2 v = e.State.VelocityXZ;
                    float impactSpeed = v.magnitude;
                    float vn = Vector2.Dot(v, hit.Normal);
                    if (vn < 0f)
                    {
                        Vector2 reflected = v - (1f + p.WallNormalRestitution) * vn * hit.Normal;
                        e.State.VelocityXZ = reflected * Mathf.Clamp01(p.WallBounceDamping);
                    }
                    else
                    {
                        e.State.VelocityXZ = v * Mathf.Clamp01(p.WallBounceDamping);
                    }

                    // Chassis damage. damage = max(MinPerHit, k × impactSpeed²)
                    // — quadratic so full-speed hits are catastrophic, with a
                    // per-hit floor so very-low-speed scrubs (where k·s² ≈ 0)
                    // still cost health. Health is clamped to [0, 1].
                    if (p.WallDamageCoefficient > 0f || p.WallDamageMinPerHit > 0f)
                    {
                        float damage = Mathf.Max(
                            p.WallDamageMinPerHit,
                            p.WallDamageCoefficient * impactSpeed * impactSpeed);
                        e.Health = Mathf.Clamp01(e.Health - damage);
                    }

                    // Speed-proportional stun: hard impacts get a longer freeze,
                    // brushes still cost at least WallStunSeconds. Capped by
                    // MaxStunSeconds so back-to-back hits cannot permanently
                    // lock the car.
                    float stunFromImpact = (p.WallStunSecondsPerImpactSpeed > 0f)
                        ? p.WallStunSecondsPerImpactSpeed * impactSpeed
                        : 0f;
                    float maxStun = p.MaxStunSeconds > 0f ? p.MaxStunSeconds : float.PositiveInfinity;
                    float stunSeconds = Mathf.Min(maxStun,
                        Mathf.Max(p.WallStunSeconds, stunFromImpact));
                    if (stunSeconds > 0f && e.StunCountdown < stunSeconds)
                        e.StunCountdown = stunSeconds;

                    Publish(new CarHitWallEvent(id,
                        new Vector3(hit.ClosestPoint.x, e.State.Position.y, hit.ClosestPoint.y),
                        new Vector3(hit.Normal.x, 0f, hit.Normal.y),
                        impactSpeed));
                }
            }

            // 7. Vertical: gravity + ground reference from track query.
            float groundY = _trackQuery.HasLoop
                ? _trackQuery.GetElevationAt(e.State.Position)
                : DefaultGroundY;

            e.State.VerticalVelocity -= p.Gravity * dt;
            e.State.Position.y += e.State.VerticalVelocity * dt;

            if (e.State.Position.y <= groundY)
            {
                e.State.Position.y = groundY;
                if (e.State.VerticalVelocity < 0f) e.State.VerticalVelocity = 0f;
                e.State.OnGround = true;
            }
            else
            {
                e.State.OnGround = false;
            }

            // 8. Lap state via projection. Cache off-track + surface for next tick's
            //    grip + drag selection.
            if (_trackQuery.HasLoop)
            {
                var proj = _trackQuery.Project(e.State.Position, e.State.LastAnchorIndex);
                e.State.LastAnchorIndex = proj.NearestAnchorIndex;
                e.State.LapDistance = proj.ArcLengthAlong;
                offTrack = proj.IsOffTrack;
                surface = proj.Surface;
                e.LastOffTrackDepth = proj.HalfWidth > 0f
                    ? Mathf.Max(0f, Mathf.Abs(proj.SignedLateralOffset) / proj.HalfWidth - 1f)
                    : 0f;
            }

            // 9. Edge events — off-track + kerb transitions.
            if (offTrack && !e.WasOffTrack)
            {
                Publish(new CarOffTrackEvent(id));
                e.WasOffTrack = true;
            }
            else if (!offTrack && e.WasOffTrack)
            {
                Publish(new CarBackOnTrackEvent(id));
                e.WasOffTrack = false;
            }

            bool onKerbNow = surface == SurfaceKind.Kerb && !offTrack;
            if (onKerbNow && !e.WasOnKerb)
            {
                Publish(new CarOnKerbEvent(id));
                e.WasOnKerb = true;
            }
            else if (!onKerbNow && e.WasOnKerb)
            {
                Publish(new CarOffKerbEvent(id));
                e.WasOnKerb = false;
            }

            e.LastSurface = surface;

            float headingDelta = e.State.Heading - headingPrev;
            if (headingDelta >  Mathf.PI) headingDelta -= 2f * Mathf.PI;
            if (headingDelta < -Mathf.PI) headingDelta += 2f * Mathf.PI;
            float yawRateTick = dt > 0f ? headingDelta / dt : 0f;
            float lateralAccel = yawRateTick * speed;

            Publish(new CarStateUpdatedEvent(id, e.State.Position, e.State.Heading, speed, e.State.OnGround));
            Publish(new CarPhysicsTickedEvent(
                id,
                e.State.Position,
                e.State.Heading,
                speed,
                lateralAccel,
                slipForEvent,
                Mathf.Max(0f, input.Throttle),
                Mathf.Max(0f, -input.Throttle),
                surface,
                e.State.LapDistance,
                dt));

#pragma warning disable CS0162 // unreachable when LogTickInterval == 0; flip the const to enable.
            if (LogTickInterval > 0)
            {
                e.TickCounter++;
                if (e.TickCounter >= LogTickInterval)
                {
                    e.TickCounter = 0;
                    Debug.Log($"[CarSim] car={id.Value} speed={speed:F2} thr={input.Throttle:F2} steer={input.Steer:F2} off={offTrack} surf={surface} pos=({e.State.Position.x:F1},{e.State.Position.z:F1})");
                }
            }
#pragma warning restore CS0162
        }

        private struct Entry
        {
            public CarParameters Parameters;
            public CarState State;
            public DriverInput Input;
            public bool WasOffTrack;
            public float LastOffTrackDepth;
            public SurfaceKind LastSurface;
            public bool WasOnKerb;
            public int TickCounter;
            // Countdown (seconds) after a wall hit during which inputs are
            // ignored. Decremented each Step; > 0 → steer + throttle forced
            // to 0 so the car coasts and decays via drag.
            public float StunCountdown;
            // Chassis health ∈ [0, 1]. 1 = pristine, 0 = wrecked. Wall hits
            // decrement it ∝ impactSpeed². The speed cap scales with health
            // so a damaged car is also a slower car. EpisodeRunner reads it
            // via TryGetHealth and ends the episode at 0.
            public float Health;
            // Straight-line aero streak. Negative = recovery delay; positive
            // = ramp time toward the full speed-cap boost. Reset to
            // −StraightLineAeroRecoverySec on any steer ≥ threshold.
            public float StraightStreakSec;
        }
    }
}
