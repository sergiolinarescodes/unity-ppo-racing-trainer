using System.Collections.Generic;

namespace UnityPpoRacingTrainer.Core.AiDriver.Physics.Modifiers
{
    /// <summary>
    /// Pluggable per-tick mutator on a car's <see cref="CarParameters"/>.
    /// Each side-system (tire wear, fuel mass, drafting) implements this and
    /// registers with <see cref="ICarPhysicsModifierAggregator"/>; the aggregator
    /// is injected into <see cref="CarSimulationService"/> and applies all
    /// registered modifiers once at the top of each step. With zero modifiers
    /// registered, the integrator behaves identically to the baseline car.
    /// </summary>
    public interface ICarPhysicsModifier
    {
        /// <summary>
        /// Sort key for the per-tick apply pipeline; modifiers run from lowest
        /// to highest. Built-in tiers: tires = 100, fuel mass = 200, drafting
        /// drag = 300. Use offsets between tiers for new modifiers that must
        /// slot between two existing ones (e.g. 250 for a system that depends
        /// on tires + fuel but precedes drafting).
        /// UP: this modifier sees the cumulative effect of more earlier
        /// modifiers — useful when its math depends on those.
        /// DOWN: this modifier runs first; later modifiers see its output.
        /// </summary>
        int Order { get; }

        /// <summary>
        /// Return new <see cref="CarParameters"/> with this modifier's
        /// contribution baked in. Pure function; no per-tick allocations.
        /// </summary>
        CarParameters Apply(CarId carId, CarParameters parameters);
    }

    public interface ICarPhysicsModifierAggregator
    {
        void Register(ICarPhysicsModifier modifier);
        void Unregister(ICarPhysicsModifier modifier);
        CarParameters Apply(CarId carId, CarParameters parameters);
    }

    internal sealed class CarPhysicsModifierAggregator : ICarPhysicsModifierAggregator
    {
        private readonly List<ICarPhysicsModifier> _modifiers = new();
        private bool _sorted = true;

        public void Register(ICarPhysicsModifier modifier)
        {
            if (modifier == null) return;
            _modifiers.Add(modifier);
            _sorted = false;
        }

        public void Unregister(ICarPhysicsModifier modifier)
        {
            _modifiers.Remove(modifier);
        }

        public CarParameters Apply(CarId carId, CarParameters parameters)
        {
            if (_modifiers.Count == 0) return parameters;
            if (!_sorted)
            {
                _modifiers.Sort(static (a, b) => a.Order.CompareTo(b.Order));
                _sorted = true;
            }
            var p = parameters;
            for (int i = 0; i < _modifiers.Count; i++)
            {
                p = _modifiers[i].Apply(carId, p);
            }
            return p;
        }
    }
}
