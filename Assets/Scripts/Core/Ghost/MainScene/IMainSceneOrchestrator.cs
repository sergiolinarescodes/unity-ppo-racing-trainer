namespace UnityPpoRacingTrainer.Core.Ghost.MainScene
{
    /// <summary>
    /// Bootstrap-time entry point for the player's main game scene. Configures
    /// the ortho camera, generates the procedural starter strip, and kicks off
    /// the ghost loop. Force-resolved + Run() called once from GameBootstrap.
    /// </summary>
    public interface IMainSceneOrchestrator
    {
        void Run();
    }
}
