namespace UnityPpoRacingTrainer.Core.Track
{
    /// <summary>
    /// Geometric constants shared by mesh strategies. All units are in world units (1u = 1 cube).
    /// Aligned with the Terrain system (slab-on-cube convention) so a flat tile at base level 0
    /// has its top surface at y=0, and the road slab sits at y=[0, SlabThickness].
    /// </summary>
    public static class TrackPieceConstants
    {
        // Default cell size (world units per grid tile). Live scenarios pass this value
        // to TerrainBuildOptions; tests may pass 1f to keep the legacy 1u scale.
        // v18: 2.0 → 3.5 (1.75× spacier). v18d: 3.5 → 3.0 (1.5× of original) —
        // mid-point: still spacier than baseline but less forgiving so the agent
        // has to actually brake into corners instead of carving any radius. Car
        // physics still pinned at CarPhysicsCellSize=2.0 below, so the wider
        // corridor benefit (vs 2.0 baseline) is preserved without the
        // "infinite cornering grip illusion" of 3.5.
        public const float CellSize = 3.0f;

        // Reference cell size used by AiDriverPhysicsDefaults.Latest for physics scaling.
        // Decoupled from CellSize so bumping the world tile size does NOT also
        // bump car speed / brake / wheelbase. Keep at 2.0 unless we deliberately
        // want to retrain the policy on a different car-feel.
        public const float CarPhysicsCellSize = 2.0f;

        // Slab geometry — slab sits just above the terrain so the bottom face does not
        // z-fight with a flat tile at y=0. Thickness collapsed to 0 so no visible
        // side faces appear between adjacent / parallel-close tiles (the side faces
        // were reading as "mini walls" between tracks). Top face still renders as
        // a flat quad at SlabBaseY; bottom face degenerates into the same plane.
        public const float SlabBaseY = 0.01f;
        public const float SlabThickness = 0f;
        public const float SlabTopY = SlabBaseY + SlabThickness;

        // Lane geometry — cell-local units. Road occupies 2*LaneHalfWidth = 0.81 of a cell;
        // remaining 0.19 splits as block margin on each side. After spawn-time
        // localScale=cellSize, road ends up cellSize*0.81 world-units wide
        // (e.g. 1.62 at cellSize=2) with cellSize*0.095 world-units of block margin per side.
        public const float LaneHalfWidth = 0.405f;       // half-width of one lane band
        public const float LaneDividerHalfWidth = 0.02f; // strip between two parallel lanes
        public const float LaneDividerYLift = 0.001f;    // sit above slab to avoid z-fighting

        // Curve geometry
        public const float CurveRadiusSmall = 0.5f;      // 1×1 quarter-arc centerline radius
        public const float CurveRadiusLarge = 1.0f;      // 2×2 quarter-arc centerline radius
        public const int CurveSegmentsSmall = 10;
        public const int CurveSegmentsLarge = 16;

        // Piano (kerb) geometry
        public const float PianoBandWidth = 0.06f;
        public const int PianoStripeCountSmall = 12;
        public const int PianoStripeCountLarge = 20;
        public const float PianoYLift = 0.005f;

        // Wall geometry — extruded barrier emitted along piece outer/internal edges.
        // Height ~0.4u (≈80cm at CellSize=2). Thickness juts the wall *outward*
        // from the road edge so visuals don't overlap the slab top. Shoulder is
        // a strip of "off-road but pre-wall" tolerance: car can drift past the
        // curb by Shoulder × cellSize before the wall fires. Lets PPO explore
        // racing-line variation without immediate episode-end on micro-drift.
        public const float WallHeight = 0.40f;
        public const float WallThickness = 0.06f;
        // Near = walls almost touching the road carriageway; Mid = visibly offset
        // but still in-tile. Selectable per variant via WallShoulderMode so the
        // V-key can cycle close-walled vs distance-walled configurations of the
        // same shape.
        public const float WallShoulderNear = 0.04f;   // ~0.08m @ c=2
        public const float WallShoulderMid = 0.18f;    // ~0.36m @ c=2
        // Legacy default — kept to preserve PPO training reward shaping. Existing
        // code that reads WallShoulder unchanged stays at the historical 0.075f
        // value; new variant-aware code uses ShoulderFor(...) instead.
        public const float WallShoulder = 0.075f;
        public const float WallYBase = SlabBaseY;
        public const float WallYTop = WallYBase + WallHeight;

        public static float ShoulderFor(WallShoulderMode mode) => mode switch
        {
            WallShoulderMode.Mid => WallShoulderMid,
            _ => WallShoulderNear,
        };

        // Static kerb constants were removed. The dynamic racing-line kerb service
        // (Assets/Scripts/Core/Ghost/Kerbs/) owns its own band-width / inset / y-lift
        // tuning since it places kerbs procedurally based on where the ghost drifts.

        // Ramp geometry
        public const float RampRise = 0.5f;

        // Junction geometry
        public const float JunctionInsetEpsilon = 0.0005f; // helps prevent z-fighting at slab unions

        // Spatial quantisation (open-port pairing, spine endpoint adjacency).
        // 5cm grid — well below any port spacing, well above floating-point noise.
        public const float PortQuantizeGridSize = 0.05f;

        // Ribbon apron / cross-section sampling.
        public const float ApronWidth = 0.40f;       // outward extent of skirt past the road edge
        public const int ApronSubdivisions = 4;      // strips per side from road edge (0) to apron outer (N)
        public const float RoadLift = 0.003f;        // tiny lift over the slab top to avoid z-fighting on flats
        public const float RibbonSampleArcStep = 0.08f; // ~12 samples per 1u tile along the spline
        public const int RibbonYSmoothKernel = 7;    // moving-average window in samples (~0.56u arc)

        // Spine sampling — default segment count for a quarter-arc / Hermite curve.
        public const int DefaultArcSegments = 8;
    }
}
