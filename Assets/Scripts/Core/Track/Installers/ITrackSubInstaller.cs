using Reflex.Core;

namespace UnityPpoRacingTrainer.Core.Track.Installers
{
    /// <summary>
    /// One capability slice of the Track system's Reflex registration. Sub-installers
    /// are NOT full <c>ISystemInstaller</c>s (no test factory of their own) — they're
    /// composed by <see cref="TrackSystemInstaller"/>, which remains the single
    /// system entry point per <c>CLAUDE.md</c>.
    ///
    /// Adding a new track capability (e.g. AI driver routing, bet payouts) = one new
    /// sub-installer added to the composition list, no edits to the dispatcher.
    /// </summary>
    internal interface ITrackSubInstaller
    {
        void Install(ContainerBuilder builder);
    }
}
