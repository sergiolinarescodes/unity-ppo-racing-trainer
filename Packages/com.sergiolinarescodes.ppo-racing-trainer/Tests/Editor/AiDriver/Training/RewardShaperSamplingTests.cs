using System;
using NUnit.Framework;
using Unidad.Core.EventBus;
using UnityPpoRacingTrainer.Core.AiDriver.Config;
using UnityPpoRacingTrainer.Core.AiDriver.Training;
using UnityEngine;

namespace UnityPpoRacingTrainer.Core.Tests.AiDriver.Training
{
    /// <summary>
    /// Unit tests for the deterministic-sampling surface of RewardShaper.
    /// Targets the two internal helpers the trainer calls at episode start:
    /// SamplePersonalityForCurrentEpisode (drives policy variety) and
    /// SampleStartingLiters (drives fuel pressure). Both read from
    /// UnityEngine.Random, so the tests pin the RNG state explicitly.
    /// </summary>
    [TestFixture]
    public class RewardShaperSamplingTests
    {
        private static RewardShaper BuildShaper()
        {
            var settings = new StaticTrainingSettingsService();
            return new RewardShaper(
                eventBus: new StubEventBus(),
                settings: settings,
                tires: null,
                fuel: null,
                draft: null,
                race: null,
                loop: null,
                registry: null,
                sim: null);
        }

        [Test]
        public void SamplePersonality_IsDeterministic_GivenSameRngSeed()
        {
            var shaper = BuildShaper();

            UnityEngine.Random.InitState(424242);
            var first = shaper.SamplePersonalityForCurrentEpisode();

            UnityEngine.Random.InitState(424242);
            var second = shaper.SamplePersonalityForCurrentEpisode();

            Assert.That(second.TirePreservation,   Is.EqualTo(first.TirePreservation));
            Assert.That(second.FuelEconomy,        Is.EqualTo(first.FuelEconomy));
            Assert.That(second.PassingAggression,  Is.EqualTo(first.PassingAggression));
            Assert.That(second.DefendingResolve,   Is.EqualTo(first.DefendingResolve));
            Assert.That(second.RiskTolerance,      Is.EqualTo(first.RiskTolerance));
            Assert.That(second.PeakPaceBias,       Is.EqualTo(first.PeakPaceBias));
        }

        [Test]
        public void SamplePersonality_StaysWithinUnitInterval()
        {
            var shaper = BuildShaper();

            UnityEngine.Random.InitState(1);
            for (int i = 0; i < 500; i++)
            {
                var p = shaper.SamplePersonalityForCurrentEpisode();
                Assert.That(p.TirePreservation,  Is.InRange(0f, 1f), $"tire i={i}");
                Assert.That(p.FuelEconomy,       Is.InRange(0f, 1f), $"fuel i={i}");
                Assert.That(p.PassingAggression, Is.InRange(0f, 1f), $"aggression i={i}");
                Assert.That(p.DefendingResolve,  Is.InRange(0f, 1f), $"defend i={i}");
                Assert.That(p.RiskTolerance,     Is.InRange(0f, 1f), $"risk i={i}");
                Assert.That(p.PeakPaceBias,      Is.InRange(0f, 1f), $"pace i={i}");
                Assert.That(p.Reserved0, Is.EqualTo(0f));
                Assert.That(p.Reserved1, Is.EqualTo(0f));
            }
        }

        [Test]
        public void SampleStartingLiters_StaysInExpectedBand()
        {
            var shaper = BuildShaper();

            UnityEngine.Random.InitState(99);
            // Sampler is `Max(2, [0.6..1.4] * 25)` → band is [15, 35].
            const float lowerExpected = 0.6f * 25f;
            const float upperExpected = 1.4f * 25f;
            const float epsilon = 1e-4f;

            for (int i = 0; i < 500; i++)
            {
                float liters = shaper.SampleStartingLiters();
                Assert.That(liters, Is.GreaterThanOrEqualTo(2f),
                    $"i={i}: must respect the 2L hard floor");
                Assert.That(liters, Is.InRange(lowerExpected - epsilon, upperExpected + epsilon),
                    $"i={i}: must stay within the {lowerExpected}..{upperExpected} band");
            }
        }

        // ---- Minimal stubs ----------------------------------------------------

        private sealed class StubEventBus : IEventBus
        {
            public IDisposable Subscribe<T>(Action<T> handler) where T : struct =>
                new NullDisposable();
            public void Publish<T>(T eventData) where T : struct { }
            public void Unsubscribe<T>(Action<T> handler) where T : struct { }
            public void ClearAllSubscriptions() { }

            private sealed class NullDisposable : IDisposable { public void Dispose() { } }
        }
    }
}
