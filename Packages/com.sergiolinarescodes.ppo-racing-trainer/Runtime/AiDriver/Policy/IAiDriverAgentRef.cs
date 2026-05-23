using UnityPpoRacingTrainer.Core.AiDriver.Physics;

namespace UnityPpoRacingTrainer.Core.AiDriver.Policy
{
    /// <summary>
    /// Surface every <c>AiDriverAgentBehaviour*</c> implementation exposes
    /// to the rest of the game (HUD, debug renderers, scenarios). Lets
    /// version-snapshot Behaviour classes (the canonical plus any frozen
    /// snapshots under <c>Versions/V&lt;N&gt;/</c>) coexist on different
    /// prefabs without consumers needing to know which one they have. Use
    /// <c>GetComponent&lt;IAiDriverAgentRef&gt;()</c> — Unity 2021+ resolves
    /// interface lookups against any matching concrete component.
    /// </summary>
    public interface IAiDriverAgentRef
    {
        CarId CarId { get; }
        bool IsRegistered { get; }
        float LastSteerCmd { get; }
        float LastThrottleCmd { get; }
    }
}
