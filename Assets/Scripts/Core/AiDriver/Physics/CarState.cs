using UnityEngine;

namespace UnityPpoRacingTrainer.Core.AiDriver.Physics
{
    /// <summary>
    /// Live, mutable state of a single car. Heading convention matches
    /// <see cref="UnityPpoRacingTrainer.Core.Track.TrackDirection"/>:
    /// 0 rad = facing +Z (north), π/2 = facing +X (east). Velocity is decomposed as
    /// (sin(h), cos(h)) * speed so a positive yaw rate from positive steering rotates
    /// the car clockwise viewed from above — a right turn.
    /// </summary>
    public struct CarState
    {
        public Vector3 Position;
        public Vector2 VelocityXZ;     // x = world X, y = world Z
        public float VerticalVelocity;
        public float Heading;          // radians
        public float SteerAngle;       // radians, clamped to [-MaxSteer, MaxSteer]
        public float BoostBudget;      // 0..1 (fraction of full budget)
        public bool OnGround;
        public int LastAnchorIndex;    // hint for ITrackQueryService.Project
        public float LapDistance;      // arc length along the active loop
        public int LapsCompleted;

        public float Speed => VelocityXZ.magnitude;
    }
}
