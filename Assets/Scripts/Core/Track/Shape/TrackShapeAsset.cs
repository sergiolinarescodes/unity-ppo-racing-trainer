using System;
using UnityEngine;

namespace UnityPpoRacingTrainer.Core.Track.Shape
{
    /// <summary>
    /// ScriptableObject holding one authored Track Shape. Edit in the inspector via
    /// the box-grid drawer (or paste a text line into <see cref="textSource"/> and
    /// click <c>Apply Text</c>). The runtime calls <see cref="ToTrackShape"/> to
    /// hand it to <see cref="TrackShapeCatalog"/>. Drop assets under any
    /// <c>Resources/TrackShapes/</c> folder for auto-discovery, or register
    /// explicitly from code.
    /// </summary>
    [CreateAssetMenu(menuName = "RACING/Track Shape", fileName = "TrackShape", order = 100)]
    public sealed class TrackShapeAsset : ScriptableObject
    {
        [Tooltip("Stable id used by the catalog. Defaults to the asset's filename when empty.")]
        [SerializeField] private string id;

        [Tooltip("Display name shown in UI / debug logs. Defaults to the asset's filename when empty.")]
        [SerializeField] private string displayName;

        [Tooltip("Step sequence — the path of the road. Edit via the box drawer or the text field.")]
        [SerializeField] private TrackStep[] steps = Array.Empty<TrackStep>();

        [Tooltip("Free-form text mirror of the steps (one character per step: F R L). " +
                 "Edit here for quick paste-from-notepad authoring; click 'Apply Text' to commit.")]
        [SerializeField, TextArea] private string textSource = "";

        public TrackShapeId Id =>
            new TrackShapeId(string.IsNullOrEmpty(id) ? name : id);

        public string DisplayName =>
            string.IsNullOrEmpty(displayName) ? name : displayName;

        public TrackStep[] Steps => steps;

        public string TextSource
        {
            get => textSource;
            set => textSource = value;
        }

        public TrackShape ToTrackShape() => new TrackShape(Id, DisplayName, steps);

        /// <summary>Replace the step array with a fresh allocation. Used by the editor.</summary>
        public void SetSteps(TrackStep[] newSteps)
        {
            steps = newSteps ?? Array.Empty<TrackStep>();
            textSource = TrackShapeTextParser.Format(steps);
        }

        /// <summary>Parse <see cref="textSource"/> into <see cref="steps"/>. Used by the editor.</summary>
        public void ApplyTextSource()
        {
            var parsed = TrackShapeTextParser.Parse(textSource ?? "");
            var arr = new TrackStep[parsed.Count];
            for (int i = 0; i < parsed.Count; i++) arr[i] = parsed[i];
            steps = arr;
        }
    }
}
