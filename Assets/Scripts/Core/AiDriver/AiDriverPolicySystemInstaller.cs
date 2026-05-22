using UnityPpoRacingTrainer.Core.AiDriver.Loop;
using UnityPpoRacingTrainer.Core.AiDriver.Physics;
using UnityPpoRacingTrainer.Core.AiDriver.Physics.Draft;
using UnityPpoRacingTrainer.Core.AiDriver.Physics.Fuel;
using UnityPpoRacingTrainer.Core.AiDriver.Physics.Tires;
using UnityPpoRacingTrainer.Core.AiDriver.Policy;
using UnityPpoRacingTrainer.Core.AiDriver.Race;
using UnityPpoRacingTrainer.Core.AiDriver.Training;
using UnityPpoRacingTrainer.Core.AiDriver.Training.Stages;
using UnityPpoRacingTrainer.Core.AiDriver.Versions;
using UnityPpoRacingTrainer.Core.AiDriver.Versions.Manifest;
using UnityPpoRacingTrainer.Core.Track.Loop;
using Reflex.Core;
using Unidad.Core.Bootstrap;
using Unidad.Core.EventBus;
using Unidad.Core.Testing;

namespace UnityPpoRacingTrainer.Core.AiDriver
{
    /// <summary>
    /// Wires the per-car ML-Agents bridge (<see cref="IAiDriverPolicyService"/>) plus
    /// the lazy <see cref="DriverProfileRegistry"/>. No <c>IFixedTickable</c> here —
    /// the policy is driven from <see cref="AiDriverAgentBehaviour.OnActionReceived"/>,
    /// which fires on the ML-Agents <c>DecisionRequester</c> cadence.
    /// </summary>
    public sealed class AiDriverPolicySystemInstaller : ISystemInstaller
    {
        public void Install(ContainerBuilder builder)
        {
            builder.AddSingleton(_ => new DriverProfileRegistry(), typeof(DriverProfileRegistry));

            builder.AddSingleton(c => new AiDriverPolicyService(
                    c.Resolve<IEventBus>(),
                    c.Resolve<ICarSimulationService>(),
                    c.Resolve<ITrackQueryService>(),
                    c.Resolve<IClosedLoopService>(),
                    c.Resolve<DriverProfileRegistry>(),
                    c.Resolve<IAiDriverVersionProfile>(),
                    c.TryResolveOptional<Track.ITrackCollisionService>(),
                    c.TryResolveOptional<ITirePhysicsService>(),
                    c.TryResolveOptional<IFuelService>(),
                    c.TryResolveOptional<IDraftService>(),
                    c.TryResolveOptional<IStageIdProvider>(),
                    c.TryResolveOptional<IActiveStageProfile>(),
                    c.TryResolveOptional<IRaceCoordinator>(),
                    c.TryResolveOptional<IObservationWriter>()),
                typeof(IAiDriverPolicyService));
        }

        public ISystemTestFactory CreateTestFactory() => new AiDriverPolicyTestFactory();
    }

    /// <summary>
    /// Shared optional-resolve helper for AiDriver installers. Returns null
    /// when the binding is genuinely missing (e.g. side-system services in a
    /// non-trainer scene) and logs anything else so silent DI faults cannot
    /// hide.
    /// </summary>
    internal static class AiDriverContainerExtensions
    {
        public static T TryResolveOptional<T>(this Container c) where T : class
        {
            try { return c.Resolve<T>(); }
            catch (System.Exception ex)
            {
                // Reflex throws an UnknownContractException-shaped exception
                // when the binding is absent — expected. Anything else (cycle,
                // factory crash, ambiguous registration) is a real bug.
                string n = ex.GetType().Name;
                if (n.IndexOf("Unknown", System.StringComparison.Ordinal) < 0
                    && n.IndexOf("NotRegistered", System.StringComparison.Ordinal) < 0
                    && n.IndexOf("Missing", System.StringComparison.Ordinal) < 0)
                {
                    UnityEngine.Debug.LogWarning(
                        $"[AiDriver] TryResolveOptional<{typeof(T).Name}> swallowed unexpected " +
                        $"{n}: {ex.Message}");
                }
                return null;
            }
        }
    }
}
