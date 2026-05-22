using System;
using System.Collections.Generic;
using UnityPpoRacingTrainer.Core.Track.Authoring;
using UnityPpoRacingTrainer.Core.Track.Loop;
using UnityPpoRacingTrainer.Core.Track.Scenarios;
using UnityPpoRacingTrainer.Core.Track.Shape;
using Unidad.Core.Testing;

namespace UnityPpoRacingTrainer.Core.Track
{
    internal sealed class TrackTestFactory : ISystemTestFactory
    {
        public Type[] TestedServices => new[]
        {
            typeof(ITrackPieceCatalog),
            typeof(ITrackPieceMeshBuilder),
            typeof(ITrackPlacementService),
            typeof(ITrackShapeCatalog),
            typeof(IShapePreviewService),
            typeof(IShapePlacementService),
            typeof(IShapeCycleService),
            typeof(IClosedLoopService)
        };

        public object CreateForTesting(TestDependencies deps)
        {
            var catalog = new TrackPieceCatalog();
            TrackPieceCatalogSeeder.Seed(catalog);
            return catalog;
        }

        public IEnumerable<ITestScenario> GetScenarios()
        {
            // Live, mouse-driven placement is the only Scenario Browser entry left.
            // Catalog / validator / mesh / placement-on-terrain / ribbon / diagonal
            // coverage moved to NUnit fixtures under Assets/Scripts/Tests/Track/.
            yield return new MouseShapePlacementScenario();
            yield return new WallKerbPieceShowcaseScenario();
            yield return new UCurveCollisionSandboxScenario();
            yield return new CircuitBarrierExportScenario();
            yield return new TrackPartsEditorScenario();
        }
    }
}
