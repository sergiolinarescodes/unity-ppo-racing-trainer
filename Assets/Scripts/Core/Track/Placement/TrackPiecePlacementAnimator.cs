using System.Collections.Generic;
using UnityPpoRacingTrainer.Core.Ghost.Presentation;
using Unidad.Core.Abstractions;
using Unidad.Core.EventBus;
using Unidad.Core.Systems;
using UnityEngine;

namespace UnityPpoRacingTrainer.Core.Track
{
    /// <summary>
    /// Drives the drop-from-air animation for player-placed track pieces. The
    /// placement service registers each fresh piece with <see cref="StartDrop"/>;
    /// this service ticks all active drops, offsets each piece's transform.Y
    /// from the drop handle, and publishes <see cref="TrackPieceLandedEvent"/>
    /// when the settle completes. Procedurally-spawned pieces (e.g. starter strip)
    /// skip this path entirely so bootstrap doesn't stall.
    /// </summary>
    public interface ITrackPiecePlacementAnimator
    {
        void StartDrop(TrackPieceId id, GameObject go, float dropHeight = 6f);
        bool IsAnimating(TrackPieceId id);
    }

    /// <summary>
    /// Fired when a player-card-placed piece has finished its drop + settle
    /// animation. Consumers (racing-line kerb service) use this as a soft signal
    /// that the topology is visually settled — the authoritative trigger for
    /// recomputing downstream state remains <c>TrackTopologyChangedEvent</c>.
    /// </summary>
    public readonly record struct TrackPieceLandedEvent(TrackPieceId Id);

    internal sealed class TrackPiecePlacementAnimator
        : SystemServiceBase, ITrackPiecePlacementAnimator, ITickable
    {
        private readonly IDropFromAirAnimator _drop;

        private readonly Dictionary<TrackPieceId, ActiveDrop> _active = new();
        private readonly List<TrackPieceId> _completedBuffer = new();

        public TrackPiecePlacementAnimator(IEventBus eventBus, IDropFromAirAnimator drop) : base(eventBus)
        {
            _drop = drop;
        }

        public bool IsAnimating(TrackPieceId id) => _active.ContainsKey(id);

        public void StartDrop(TrackPieceId id, GameObject go, float dropHeight = 6f)
        {
            if (go == null) return;
            // If a prior drop is still mid-flight for the same id, fast-forward and
            // overwrite — the piece-id is being reused (Remove + re-place), the
            // new drop wins.
            if (_active.TryGetValue(id, out var prior))
                RestoreTransform(prior);

            var basePos = go.transform.position;
            var handle = _drop.PlayDropFromAir(basePos, 0f, dropHeight);
            // Start the piece at the full drop height so the first rendered frame
            // is already up-in-the-air, not at ground (avoids one-frame flash).
            go.transform.position = basePos + new Vector3(0f, dropHeight, 0f);
            _active[id] = new ActiveDrop(go, basePos, handle);
        }

        public void Tick(float deltaTime)
        {
            if (_active.Count == 0) return;
            _completedBuffer.Clear();

            foreach (var kv in _active)
            {
                var d = kv.Value;
                if (d.GameObject == null)
                {
                    _completedBuffer.Add(kv.Key);
                    continue;
                }
                d.Handle.Update(deltaTime);
                d.GameObject.transform.position = d.BasePos + new Vector3(0f, d.Handle.CurrentYOffset, 0f);
                if (d.Handle.IsSettleComplete)
                {
                    d.GameObject.transform.position = d.BasePos;
                    _completedBuffer.Add(kv.Key);
                }
            }

            for (int i = 0; i < _completedBuffer.Count; i++)
            {
                var id = _completedBuffer[i];
                _active.Remove(id);
                Publish(new TrackPieceLandedEvent(id));
            }
        }

        private static void RestoreTransform(ActiveDrop d)
        {
            if (d.GameObject != null)
                d.GameObject.transform.position = d.BasePos;
        }

        private readonly struct ActiveDrop
        {
            public readonly GameObject GameObject;
            public readonly Vector3 BasePos;
            public readonly IDropHandle Handle;
            public ActiveDrop(GameObject go, Vector3 basePos, IDropHandle handle)
            {
                GameObject = go;
                BasePos = basePos;
                Handle = handle;
            }
        }
    }
}
