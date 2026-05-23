using UnityEngine;

namespace UnityPpoRacingTrainer.Core.AiDriver.Training.Curriculum
{
    /// <summary>
    /// Marks a string field as a circuit-id picker for the trainer's
    /// Editor inference override. The matching PropertyDrawer scans
    /// <see cref="CurriculumStages.AuthoredLibraryDir"/> at edit time and
    /// renders the field as a dropdown over the available ids (with a
    /// "&lt;random&gt;" sentinel = empty string at the top). The attribute
    /// carries no data; the drawer does all the work.
    /// </summary>
    public sealed class ForceCircuitIdAttribute : PropertyAttribute
    {
    }
}
