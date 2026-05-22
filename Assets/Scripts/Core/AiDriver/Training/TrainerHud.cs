using System.Collections.Generic;
using UnityPpoRacingTrainer.Core.AiDriver.Physics;
using UnityPpoRacingTrainer.Core.AiDriver.Policy;
using UnityPpoRacingTrainer.Core.Track.Loop;
using Reflex.Extensions;
using Unidad.Core.EventBus;
using UnityEngine;

namespace UnityPpoRacingTrainer.Core.AiDriver.Training
{
    /// <summary>
    /// Live-training overlay (legacy <see cref="OnGUI"/> so it works without a
    /// UIDocument setup in the trainer scene). Shows the in-flight episode's car
    /// kinematics + the last finished episode's outcome + the current stage. Cheap
    /// to attach: TrainerBootstrap calls <see cref="AddComponent"/> on its own
    /// GameObject; no scene wiring required.
    /// </summary>
    internal sealed class TrainerHud : MonoBehaviour
    {
        private ICarSimulationService _carSim;
        private TrainingDirector _director;
        private IAiDriverAgentRef _agent;

        private readonly List<System.IDisposable> _subs = new();

        private int _episodesCompleted;
        private EpisodeEndReason? _lastReason;
        private float _lastCumulativeReward;
        private int _lastSteps;
        private float _lastElapsedSec;
        private int _lastLapsCompleted;
        private bool _isOffTrack;
        private float _loopLength;

        public void Bind(
            IEventBus eventBus,
            ICarSimulationService carSim,
            IClosedLoopService loop,
            TrainingDirector director,
            IAiDriverAgentRef agent)
        {
            _carSim = carSim;
            _director = director;
            _agent = agent;

            _subs.Add(eventBus.Subscribe<EpisodeEndedEvent>(OnEpisodeEnded));
            _subs.Add(eventBus.Subscribe<CarOffTrackEvent>(_ => _isOffTrack = true));
            _subs.Add(eventBus.Subscribe<CarBackOnTrackEvent>(_ => _isOffTrack = false));
            _subs.Add(eventBus.Subscribe<LoopClosedEvent>(e => _loopLength = e.TotalLength));
        }

        private void OnEpisodeEnded(EpisodeEndedEvent e)
        {
            _episodesCompleted++;
            _lastReason = e.Reason;
            _lastCumulativeReward = e.CumulativeReward;
            _lastSteps = e.Steps;
            _lastElapsedSec = e.ElapsedSec;
            _lastLapsCompleted = e.LapsCompleted;
            _isOffTrack = false;
        }

        private void OnDestroy()
        {
            foreach (var s in _subs) s?.Dispose();
            _subs.Clear();
        }

        private GUIStyle _labelStyle;
        private GUIStyle _headerStyle;

        private void OnGUI()
        {
            EnsureStyles();

            const float w = 420f;
            const float h = 560f;
            const float pad = 18f;
            GUI.Box(new Rect(8, 8, w, h), "");
            GUILayout.BeginArea(new Rect(8 + pad, 8 + pad, w - 2 * pad, h - 2 * pad));

            GUILayout.Label("RACING Trainer", _headerStyle);

            // Per-car kinematics (HUD shows the currently bound agent only).
            if (_agent != null && _agent.IsRegistered && _carSim != null && _carSim.TryGetState(_agent.CarId, out var state))
            {
                GUILayout.Label($"speed:    {state.Speed:F2} m/s", _labelStyle);
                GUILayout.Label($"lap dist: {state.LapDistance:F2} m" +
                                (_loopLength > 0f ? $" / {_loopLength:F1}" : ""), _labelStyle);
                GUILayout.Label($"on-track: {(_isOffTrack ? "<b>OFF</b>" : "yes")}", _labelStyle);
                // Policy command readout — what the network is OUTPUTTING.
                // If throttle stays near +1.00 in every tick, policy hasn't
                // discovered braking. If steer never goes negative or positive,
                // policy isn't using one direction. Critical debug signal.
                GUILayout.Label($"cmd steer={_agent.LastSteerCmd:+0.00;-0.00}  throttle={_agent.LastThrottleCmd:+0.00;-0.00}", _labelStyle);
                GUILayout.Label($"actuated steer:    {state.SteerAngle:+0.00;-0.00}", _labelStyle);
            }
            else
            {
                GUILayout.Label("(agent not registered)", _labelStyle);
            }

            // Per-episode roll-up.
            GUILayout.Space(6);
            GUILayout.Label($"episodes: {_episodesCompleted}", _labelStyle);
            if (_lastReason.HasValue)
            {
                GUILayout.Label($"last:     {_lastReason.Value}  laps={_lastLapsCompleted}", _labelStyle);
                GUILayout.Label($"          {_lastSteps} steps  {_lastElapsedSec:F1}s  reward={_lastCumulativeReward:F2}", _labelStyle);
            }
            else
            {
                GUILayout.Label("last:     (no episodes finished yet)", _labelStyle);
            }

            // Director state.
            if (_director != null)
            {
                GUILayout.Space(6);
                GUILayout.Label($"stage:    {_director.LastStageId}", _labelStyle);
                if (!string.IsNullOrEmpty(_director.LastFailureReason))
                    GUILayout.Label($"genfail:  {_director.LastFailureReason}", _labelStyle);
            }

            GUILayout.EndArea();
        }

        private void EnsureStyles()
        {
            if (_labelStyle == null)
            {
                _labelStyle = new GUIStyle(GUI.skin.label)
                {
                    fontSize = 22,
                    richText = true,
                    normal = { textColor = Color.white },
                };
            }
            if (_headerStyle == null)
            {
                _headerStyle = new GUIStyle(GUI.skin.label)
                {
                    fontSize = 26,
                    fontStyle = FontStyle.Bold,
                    normal = { textColor = new Color(0.4f, 0.85f, 1f) },
                };
            }
        }
    }
}
