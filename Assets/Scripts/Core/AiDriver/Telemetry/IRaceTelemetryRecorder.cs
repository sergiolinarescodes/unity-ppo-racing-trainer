using System;
using System.Collections.Generic;

namespace UnityPpoRacingTrainer.Core.AiDriver.Telemetry
{
    /// <summary>
    /// Writer side. Hands each finished race to a sampler; if the sampler
    /// keeps it, writes through to <see cref="IRaceTelemetrySink"/>. The
    /// recorder owns one in-flight buffer per active episode and drops it
    /// silently on rejection — the keep-rate is so low (1 per
    /// <see cref="IRaceSampler.WindowSize"/>) that storing every race
    /// "just in case" would dwarf the value of the kept sample.
    /// </summary>
    public interface IRaceTelemetryRecorder
    {
        /// <summary>Open a new in-flight race. Idempotent for the same episode.</summary>
        void BeginRace(RaceContext context);

        /// <summary>Hint that the recorder should treat the upcoming
        /// <c>EpisodeEndedEvent</c> as the closing boundary. Optional —
        /// the service also closes on <c>EpisodeEndedEvent</c> directly.</summary>
        void EndRaceHint();

        /// <summary>Currently-open race or <c>null</c>. For tests + debug.</summary>
        RaceRecordDto CurrentInFlight { get; }
    }

    /// <summary>Writer-side strategy. Receives only the races the sampler kept.</summary>
    public interface IRaceTelemetrySink
    {
        void WriteRace(RaceRecordDto record);
    }

    /// <summary>Reader-side strategy. Dashboards + in-game UI use this to
    /// browse the history. Backed by <c>DiskRaceHistoryStore</c> during
    /// training and <c>InMemoryRaceHistoryStore</c> in player builds.</summary>
    public interface IRaceHistoryStore
    {
        /// <summary>Lightweight headers, newest first. Max size = <see cref="MaxKept"/>.</summary>
        IReadOnlyList<RaceSummaryDto> List();

        /// <summary>Load the full record for a previously-summarised race.
        /// Returns <c>null</c> if the race has been evicted since the
        /// summary was issued.</summary>
        RaceRecordDto Load(string raceId);

        int MaxKept { get; }
    }

    /// <summary>Reservoir gate. The sampler decides which of the races
    /// in a fixed-size window actually survives to disk / memory.</summary>
    public interface IRaceSampler
    {
        /// <summary>Algorithm R: each ended episode has probability
        /// <c>1 / episodesSeenInWindow</c> of replacing the in-flight
        /// candidate. The implementation is responsible for window
        /// rollover when <see cref="WindowSize"/> is reached.</summary>
        /// <returns>True iff this episode is the one being kept this
        /// window (which only happens when the window closes on it).</returns>
        bool OnEpisodeEnded(Random rng);

        int WindowSize { get; }
    }
}
