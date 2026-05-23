using System;
using System.Collections.Generic;

namespace UnityPpoRacingTrainer.Core.Track.Shape
{
    /// <summary>
    /// Parses a Track Shape from plain text — one character per step. Authors can
    /// edit shapes in any text editor (notepad, etc.) using a single line:
    /// <c>F R L F</c>, <c>FFRLF</c>, <c>F-R-L-F</c> all parse to the same path.
    /// Unknown characters throw with a clear pointer.
    /// </summary>
    public static class TrackShapeTextParser
    {
        /// <summary>Parses a step from a single character. Throws on unknown. Note
        /// that <c>r</c>/<c>l</c> (lowercase) are 45° turns; <c>R</c>/<c>L</c> stay 90°.</summary>
        public static TrackStep ParseStep(char c) => c switch
        {
            'F' or 'f' or '-' => TrackStep.Forward,
            'R' or '>' => TrackStep.TurnRight,
            'L' or '<' => TrackStep.TurnLeft,
            'r' => TrackStep.DiagRight,
            'l' => TrackStep.DiagLeft,
            _ => throw new ArgumentException(
                $"Unknown step character '{c}'. Allowed: F/- forward, R/> right90, L/< left90, r right45, l left45.")
        };

        /// <summary>Parses a free-form string. Whitespace, commas, and pipes are skipped.</summary>
        public static IReadOnlyList<TrackStep> Parse(string text)
        {
            if (string.IsNullOrEmpty(text)) return Array.Empty<TrackStep>();
            var steps = new List<TrackStep>(text.Length);
            for (int i = 0; i < text.Length; i++)
            {
                var c = text[i];
                if (char.IsWhiteSpace(c) || c == ',' || c == '|' || c == '\r' || c == '\n')
                    continue;
                steps.Add(ParseStep(c));
            }
            return steps;
        }

        /// <summary>Renders a step list back to a compact text form (no separators).</summary>
        public static string Format(IReadOnlyList<TrackStep> steps)
        {
            if (steps == null || steps.Count == 0) return string.Empty;
            var chars = new char[steps.Count];
            for (int i = 0; i < steps.Count; i++) chars[i] = ToChar(steps[i]);
            return new string(chars);
        }

        private static char ToChar(TrackStep s) => s switch
        {
            TrackStep.Forward => 'F',
            TrackStep.TurnRight => 'R',
            TrackStep.TurnLeft => 'L',
            TrackStep.DiagRight => 'r',
            TrackStep.DiagLeft => 'l',
            _ => '?'
        };
    }
}
