using System.Collections.Generic;

namespace UnityPpoRacingTrainer.Core.AiDriver.Telemetry
{
    /// <summary>
    /// In-process ring buffer of <c>MaxKept</c> most recent races. Used by
    /// player builds (so the post-race UI can show the same history the
    /// Python dashboard shows during training, without ever touching disk)
    /// and by unit tests (so they can assert ring-eviction without
    /// filesystem ceremony).
    /// </summary>
    public sealed class InMemoryRaceSink : IRaceTelemetrySink, IRaceHistoryStore
    {
        private readonly LinkedList<RaceRecordDto> _records = new();
        private readonly int _maxKept;

        public InMemoryRaceSink(int maxKept = DiskJsonRaceSink.DefaultMaxKept)
        {
            _maxKept = maxKept <= 0 ? DiskJsonRaceSink.DefaultMaxKept : maxKept;
        }

        public int MaxKept => _maxKept;

        public void WriteRace(RaceRecordDto record)
        {
            if (record == null) return;
            _records.AddFirst(record);
            while (_records.Count > _maxKept) _records.RemoveLast();
        }

        public IReadOnlyList<RaceSummaryDto> List()
        {
            var result = new List<RaceSummaryDto>(_records.Count);
            foreach (var r in _records) result.Add(RaceSummaryDto.From(r));
            return result;
        }

        public RaceRecordDto Load(string raceId)
        {
            if (string.IsNullOrEmpty(raceId)) return null;
            foreach (var r in _records)
                if (r.race_id == raceId) return r;
            return null;
        }
    }
}
