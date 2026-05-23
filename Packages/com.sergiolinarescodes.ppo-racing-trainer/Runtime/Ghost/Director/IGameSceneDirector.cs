namespace UnityPpoRacingTrainer.Core.Ghost.Director
{
    public enum GhostDirectorState { Idle, SpawnDrop, Settle, Drive, Respawn }

    /// <summary>
    /// Hosts the ghost-loop state machine. The orchestrator calls
    /// <see cref="StartGhostLoop"/> once on bootstrap; the director keeps the
    /// cycle going for the rest of the session.
    /// </summary>
    public interface IGameSceneDirector
    {
        GhostDirectorState CurrentState { get; }
        void StartGhostLoop();
    }
}
