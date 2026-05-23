using Reflex.Core;

namespace UnityPpoRacingTrainer.Core.Track.Installers
{
    /// <summary>
    /// Registers the piece catalog (eagerly seeded) — foundation every other Track
    /// sub-installer depends on.
    /// </summary>
    internal sealed class CatalogTrackSubInstaller : ITrackSubInstaller
    {
        public void Install(ContainerBuilder builder)
        {
            builder.AddSingleton(_ =>
            {
                var catalog = new TrackPieceCatalog();
                TrackPieceCatalogSeeder.Seed(catalog);
                return (ITrackPieceCatalog)catalog;
            }, typeof(ITrackPieceCatalog));
        }
    }
}
