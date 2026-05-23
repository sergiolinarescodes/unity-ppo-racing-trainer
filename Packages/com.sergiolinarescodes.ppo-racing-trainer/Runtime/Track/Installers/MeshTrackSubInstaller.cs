using Reflex.Core;

namespace UnityPpoRacingTrainer.Core.Track.Installers
{
    /// <summary>
    /// Per-piece mesh pipeline: height adapter + cached mesh builder + the
    /// collision service that aggregates wall geometry across placed pieces.
    /// Kerbs are placed dynamically by the racing-line kerb service (Ghost/Kerbs/),
    /// not by the mesh pipeline.
    /// </summary>
    internal sealed class MeshTrackSubInstaller : ITrackSubInstaller
    {
        public void Install(ContainerBuilder builder)
        {
            builder.AddSingleton(_ => (ITrackHeightAdapter)new FlatHeightAdapter(),
                typeof(ITrackHeightAdapter));

            builder.AddSingleton(c =>
            {
                var adapter = c.Resolve<ITrackHeightAdapter>();
                return (ITrackPieceMeshBuilder)new TrackPieceMeshBuilder(adapter);
            }, typeof(ITrackPieceMeshBuilder));

            builder.AddSingleton(_ => (ITrackCollisionService)new TrackCollisionService(),
                typeof(ITrackCollisionService));
        }
    }
}
