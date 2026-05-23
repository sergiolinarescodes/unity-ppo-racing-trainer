using UnityEngine;

namespace UnityPpoRacingTrainer.Core.Track.Geometry
{
    /// <summary>
    /// Pure functions over piece-local port geometry. Two consumers — the placed-piece
    /// snapshot (<see cref="OpenPortIndex"/>) and the inverse anchor solve
    /// (<see cref="MagnetSnapResolver"/>) — both need to lift a port from a definition
    /// to its canonical local point on a piece, plus mirror/rotate it. Centralising
    /// the formulas keeps the two paths in lockstep when piece conventions change.
    /// </summary>
    public static class PortGeometry
    {
        /// <summary>
        /// Local port position in the canonical (north-facing) piece frame. Cardinal
        /// sides anchor at the lane-mid-point on that edge; diagonal sides anchor at
        /// the matching tile corner. Diagonal pieces are 1×1 by design — diagonal
        /// ports on multi-tile pieces are not supported.
        /// </summary>
        public static Vector2 CanonicalLocal(TrackPieceDefinition def, TrackPort port)
        {
            int W = def.Dimensions.Width;
            int L = def.Dimensions.Length;
            float lane = port.LaneOffset + 0.5f;
            return port.Side switch
            {
                TrackDirection.South => new Vector2(lane, 0f),
                TrackDirection.North => new Vector2(lane, L),
                TrackDirection.West => new Vector2(0f, lane),
                TrackDirection.East => new Vector2(W, lane),
                TrackDirection.SouthWest => new Vector2(0f, 0f),
                TrackDirection.SouthEast => new Vector2(W, 0f),
                TrackDirection.NorthEast => new Vector2(W, L),
                TrackDirection.NorthWest => new Vector2(0f, L),
                _ => new Vector2(lane, 0f)
            };
        }

        /// <summary>
        /// MirrorX flips around the X axis: N↔N, S↔S, E↔W, NE↔NW, SE↔SW.
        /// </summary>
        public static TrackDirection MirrorXSide(TrackDirection d) => d switch
        {
            TrackDirection.East => TrackDirection.West,
            TrackDirection.West => TrackDirection.East,
            TrackDirection.NorthEast => TrackDirection.NorthWest,
            TrackDirection.NorthWest => TrackDirection.NorthEast,
            TrackDirection.SouthEast => TrackDirection.SouthWest,
            TrackDirection.SouthWest => TrackDirection.SouthEast,
            _ => d
        };

        /// <summary>
        /// Compose: world side = canonical side rotated by piece facing.
        /// <c>mirrorX</c> is accepted for backward-compat but **intentionally ignored**:
        /// piece definitions author <see cref="TrackPort.Side"/> as the post-mirror
        /// (visible) port direction (e.g. LeftCurve declares <c>Road(West)</c> meaning
        /// "west port", not "east port that gets mirrored to west"). The MirrorX flag
        /// is mesh-only — the mesh builder mirrors geometry on X, but ports remain
        /// where the seeder authored them.
        /// </summary>
        public static TrackDirection ApplyFacing(TrackDirection canonicalSide, TrackDirection facing, bool mirrorX)
        {
            _ = mirrorX; // mesh-only flag; do not double-apply to port direction
            return (TrackDirection)(((byte)canonicalSide + (byte)facing) & 7);
        }

        /// <summary>
        /// Returns <paramref name="lx"/> unchanged. The <paramref name="mirrorX"/>
        /// parameter is kept for API compat but ignored — see <see cref="ApplyFacing"/>
        /// for rationale (port positions are authored post-mirror; mesh handles its
        /// own X-flip).
        /// </summary>
        public static float MirrorXLocal(float lx, bool mirrorX)
        {
            _ = mirrorX;
            return lx;
        }

        /// <summary>
        /// Rotate a local point (lx,lz) around the anchor-tile centre (0.5, 0.5) by the
        /// piece facing yaw. Unity's Quaternion.Euler(0, yaw, 0) rotates CW viewed from
        /// +Y above (i.e. +X → -Z for yaw=+90°), so the in-plane formula is the
        /// transpose of the conventional math-CCW 2D rotation.
        /// </summary>
        public static Vector2 RotateAroundAnchor(float lx, float lz, TrackDirection facing)
        {
            float yaw = facing.YawDegrees() * Mathf.Deg2Rad;
            float cs = Mathf.Cos(yaw), sn = Mathf.Sin(yaw);
            float dx = lx - 0.5f;
            float dz = lz - 0.5f;
            float rx = dx * cs + dz * sn;
            float rz = -dx * sn + dz * cs;
            return new Vector2(rx, rz);
        }
    }
}
