namespace UnityPpoRacingTrainer.Core.AiDriver.Versions
{
    /// <summary>
    /// Selector for which premium AI driver model the scene runs. The enum is
    /// the single source of truth — every other version-specific decision
    /// (prefab path, ONNX, observation layout, physics tunings, reward shaper)
    /// flows from <see cref="IAiDriverVersionProfile"/> resolved through
    /// <see cref="AiDriverVersionRegistry"/>.
    ///
    /// Convention: <see cref="Latest"/> is an alias for the current canonical
    /// version. When a new snapshot is taken, the prior canonical gets a
    /// numbered enum value here (V1, V2, V3, …) and <see cref="Latest"/>
    /// stays a fixed sentinel so call-sites don't need touching. See
    /// <c>docs/snapshot-version.md</c> for the snapshot procedure.
    /// </summary>
    public enum AiDriverVersion
    {
        /// <summary>
        /// First frozen snapshot. Captures the initial public canonical
        /// (60-float observation layout + the manifest-driven reward and
        /// physics values frozen at the time of the snapshot, persisted in
        /// <c>Assets/_Bootstrap/Configs/Versions/v1.json</c>).
        /// Demonstrates the snapshot pattern; real regression use requires
        /// copying the matching prefab + ONNX per docs/snapshot-version.md.
        ///
        /// To add the next snapshot, follow the same recipe and bump to V2.
        /// </summary>
        V1 = 1,
        Latest = 100,
    }
}
