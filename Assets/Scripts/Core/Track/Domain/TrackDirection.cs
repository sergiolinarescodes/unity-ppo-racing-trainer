namespace UnityPpoRacingTrainer.Core.Track
{
    /// <summary>
    /// Facing for a placed track piece. Eight values stepping by 45°. Cardinal axes
    /// (N/E/S/W) sit on even indices so cardinal-only consumers can still cycle
    /// through them with <see cref="TrackDirectionExtensions.RotateRight"/> /
    /// <see cref="TrackDirectionExtensions.RotateLeft"/>, which advance by 90°
    /// (2 enum steps). Use the 45-suffixed variants for the fine 45° step needed
    /// by diagonal pieces and magnet rotation.
    ///
    /// North = +Z, East = +X (matches the Terrain coord system:
    /// <c>TerrainPosition.X</c> = east, <c>TerrainPosition.Z</c> = north).
    /// Yaw = <c>(int)d * 45</c> degrees around +Y.
    /// </summary>
    public enum TrackDirection : byte
    {
        North = 0,
        NorthEast = 1,
        East = 2,
        SouthEast = 3,
        South = 4,
        SouthWest = 5,
        West = 6,
        NorthWest = 7
    }

    public static class TrackDirectionExtensions
    {
        /// <summary>Rotate 90° clockwise (cardinal-preserving). 4-step cycle on cardinals.</summary>
        public static TrackDirection RotateRight(this TrackDirection d) =>
            (TrackDirection)(((byte)d + 2) & 7);

        /// <summary>Rotate 90° counter-clockwise.</summary>
        public static TrackDirection RotateLeft(this TrackDirection d) =>
            (TrackDirection)(((byte)d + 6) & 7);

        /// <summary>Rotate 45° clockwise. Cardinal → diagonal → next cardinal.</summary>
        public static TrackDirection RotateRight45(this TrackDirection d) =>
            (TrackDirection)(((byte)d + 1) & 7);

        /// <summary>Rotate 45° counter-clockwise.</summary>
        public static TrackDirection RotateLeft45(this TrackDirection d) =>
            (TrackDirection)(((byte)d + 7) & 7);

        public static TrackDirection Opposite(this TrackDirection d) =>
            (TrackDirection)(((byte)d + 4) & 7);

        public static int YawDegrees(this TrackDirection d) => (int)d * 45;

        public static bool IsCardinal(this TrackDirection d) => ((byte)d & 1) == 0;
        public static bool IsDiagonal(this TrackDirection d) => ((byte)d & 1) == 1;

        public static (int dx, int dz) Step(this TrackDirection d) => d switch
        {
            TrackDirection.North => (0, 1),
            TrackDirection.NorthEast => (1, 1),
            TrackDirection.East => (1, 0),
            TrackDirection.SouthEast => (1, -1),
            TrackDirection.South => (0, -1),
            TrackDirection.SouthWest => (-1, -1),
            TrackDirection.West => (-1, 0),
            TrackDirection.NorthWest => (-1, 1),
            _ => (0, 0)
        };
    }
}
