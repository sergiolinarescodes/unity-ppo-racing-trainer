using System.Collections.Generic;
using UnityPpoRacingTrainer.Core.Track.Installers;
using Reflex.Core;
using Unidad.Core.Bootstrap;
using Unidad.Core.Testing;

namespace UnityPpoRacingTrainer.Core.Track
{
    /// <summary>
    /// Wires the Track system into Reflex. Composes capability-grouped
    /// <see cref="ITrackSubInstaller"/>s rather than registering every service inline —
    /// adding a new capability is one new sub-installer in the list.
    ///
    /// Depends on Grid + Terrain installers being registered first (validators read
    /// <see cref="UnityPpoRacingTrainer.Core.Terrain.ITerrainService"/>).
    /// </summary>
    public sealed class TrackSystemInstaller : ISystemInstaller
    {
        private static readonly IReadOnlyList<ITrackSubInstaller> SubInstallers = new ITrackSubInstaller[]
        {
            new CatalogTrackSubInstaller(),
            new MeshTrackSubInstaller(),
            new ValidationTrackSubInstaller(),
            new ShapeTrackSubInstaller(),
            new RibbonTrackSubInstaller(),
            new LoopDetectionTrackSubInstaller()
        };

        public void Install(ContainerBuilder builder)
        {
            foreach (var sub in SubInstallers) sub.Install(builder);
        }

        public ISystemTestFactory CreateTestFactory() => new TrackTestFactory();
    }
}
