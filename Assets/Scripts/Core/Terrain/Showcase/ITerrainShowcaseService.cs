namespace UnityPpoRacingTrainer.Core.Terrain.Showcase
{
    /// <summary>
    /// Bootstrap-time visual showcase: builds a random 16x16 terrain and orbits the
    /// MainCamera around it. Stop() before any future system that wants to control
    /// the camera (Build Phase, gameplay).
    /// </summary>
    public interface ITerrainShowcaseService
    {
        bool IsRunning { get; }
        void Start();
        void Stop();
    }
}
