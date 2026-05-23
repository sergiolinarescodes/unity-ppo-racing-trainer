using System.Collections.Generic;
using UnityPpoRacingTrainer.Core.Track;
using UnityPpoRacingTrainer.Core.Track.Shape;
using UnityEditor;
using UnityEngine;

namespace UnityPpoRacingTrainer.Editor.Track.Shape
{
    /// <summary>
    /// Custom inspector for <see cref="TrackShapeAsset"/>. Provides three authoring
    /// surfaces over the same step list:
    /// <list type="number">
    ///   <item>A row of clickable boxes — left-click cycles F → R → L; right-click removes.</item>
    ///   <item>A free-form text field that round-trips to <c>F R L</c> notation.</item>
    ///   <item>A small ASCII map of the resolved canonical path so the author can see the route.</item>
    /// </list>
    /// </summary>
    [CustomEditor(typeof(TrackShapeAsset))]
    internal sealed class TrackShapeAssetEditor : UnityEditor.Editor
    {
        private const float BoxSize = 36f;
        private const int MapMaxExtent = 12;

        private static readonly Color BoxBgForward = new(0.85f, 0.85f, 0.85f, 1f);
        private static readonly Color BoxBgRight = new(0.30f, 0.70f, 0.95f, 1f);
        private static readonly Color BoxBgLeft = new(0.95f, 0.55f, 0.30f, 1f);

        public override void OnInspectorGUI()
        {
            var asset = (TrackShapeAsset)target;
            serializedObject.Update();

            EditorGUILayout.PropertyField(serializedObject.FindProperty("id"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("displayName"));
            serializedObject.ApplyModifiedProperties();

            EditorGUILayout.Space(8);
            EditorGUILayout.LabelField("Steps", EditorStyles.boldLabel);
            DrawBoxRow(asset);

            EditorGUILayout.Space(4);
            DrawAddRemoveControls(asset);

            EditorGUILayout.Space(8);
            EditorGUILayout.LabelField("Text Source", EditorStyles.boldLabel);
            DrawTextSourceControls(asset);

            EditorGUILayout.Space(8);
            EditorGUILayout.LabelField("Path Map (canonical / north-facing)", EditorStyles.boldLabel);
            DrawAsciiMap(asset);
        }

        private void DrawBoxRow(TrackShapeAsset asset)
        {
            var steps = asset.Steps;
            using (new EditorGUILayout.HorizontalScope())
            {
                for (int i = 0; i < steps.Length; i++)
                {
                    var rect = GUILayoutUtility.GetRect(BoxSize, BoxSize, GUILayout.Width(BoxSize), GUILayout.Height(BoxSize));
                    DrawBox(rect, steps[i]);

                    var e = Event.current;
                    if (e.type == EventType.MouseDown && rect.Contains(e.mousePosition))
                    {
                        if (e.button == 0) // left-click cycles
                        {
                            Undo.RecordObject(asset, "Cycle Track Step");
                            steps[i] = Cycle(steps[i]);
                            asset.SetSteps(steps);
                            EditorUtility.SetDirty(asset);
                            e.Use();
                            Repaint();
                        }
                        else if (e.button == 1) // right-click removes
                        {
                            Undo.RecordObject(asset, "Remove Track Step");
                            var list = new List<TrackStep>(steps);
                            list.RemoveAt(i);
                            asset.SetSteps(list.ToArray());
                            EditorUtility.SetDirty(asset);
                            e.Use();
                            Repaint();
                        }
                    }
                }
            }
        }

        private void DrawAddRemoveControls(TrackShapeAsset asset)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("+ Forward (F)", GUILayout.Width(110)))
                    AppendStep(asset, TrackStep.Forward);
                if (GUILayout.Button("+ Right (R)", GUILayout.Width(100)))
                    AppendStep(asset, TrackStep.TurnRight);
                if (GUILayout.Button("+ Left (L)", GUILayout.Width(100)))
                    AppendStep(asset, TrackStep.TurnLeft);
                GUILayout.FlexibleSpace();
                if (GUILayout.Button("Clear", GUILayout.Width(60)))
                {
                    Undo.RecordObject(asset, "Clear Track Shape");
                    asset.SetSteps(System.Array.Empty<TrackStep>());
                    EditorUtility.SetDirty(asset);
                }
            }
        }

        private void DrawTextSourceControls(TrackShapeAsset asset)
        {
            EditorGUI.BeginChangeCheck();
            string newText = EditorGUILayout.TextArea(asset.TextSource ?? "", GUILayout.MinHeight(40));
            if (EditorGUI.EndChangeCheck())
            {
                Undo.RecordObject(asset, "Edit Text Source");
                asset.TextSource = newText;
                EditorUtility.SetDirty(asset);
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Apply Text → Steps"))
                {
                    Undo.RecordObject(asset, "Apply Track Shape Text");
                    try
                    {
                        asset.ApplyTextSource();
                        EditorUtility.SetDirty(asset);
                    }
                    catch (System.ArgumentException ex)
                    {
                        EditorUtility.DisplayDialog("Invalid Text Source", ex.Message, "OK");
                    }
                }
                if (GUILayout.Button("Sync Steps → Text"))
                {
                    Undo.RecordObject(asset, "Sync Track Shape Text");
                    asset.TextSource = TrackShapeTextParser.Format(asset.Steps);
                    EditorUtility.SetDirty(asset);
                }
            }
        }

        private void DrawAsciiMap(TrackShapeAsset asset)
        {
            var pieces = TrackShapeWalker.Walk(asset.Steps);
            if (pieces.Count == 0)
            {
                EditorGUILayout.HelpBox("Empty path. Add steps above to see the route.", MessageType.None);
                return;
            }

            int minX = int.MaxValue, maxX = int.MinValue, minZ = int.MaxValue, maxZ = int.MinValue;
            foreach (var p in pieces)
            {
                minX = Mathf.Min(minX, p.Offset.Dx);
                maxX = Mathf.Max(maxX, p.Offset.Dx);
                minZ = Mathf.Min(minZ, p.Offset.Dz);
                maxZ = Mathf.Max(maxZ, p.Offset.Dz);
            }
            int w = Mathf.Min(MapMaxExtent, maxX - minX + 1);
            int h = Mathf.Min(MapMaxExtent, maxZ - minZ + 1);

            var grid = new char[h, w];
            for (int z = 0; z < h; z++) for (int x = 0; x < w; x++) grid[z, x] = '.';
            for (int i = 0; i < pieces.Count; i++)
            {
                var p = pieces[i];
                int gx = p.Offset.Dx - minX;
                int gz = p.Offset.Dz - minZ;
                if (gx < 0 || gx >= w || gz < 0 || gz >= h) continue;
                grid[gz, gx] = i == 0 ? 'S' : Glyph(p.PieceType);
            }

            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                // Print north at top.
                for (int z = h - 1; z >= 0; z--)
                {
                    var row = new System.Text.StringBuilder(w * 2);
                    for (int x = 0; x < w; x++)
                    {
                        row.Append(grid[z, x]);
                        row.Append(' ');
                    }
                    EditorGUILayout.LabelField(row.ToString(), EditorStyles.miniLabel);
                }
                EditorGUILayout.LabelField($"S = start, F = straight, R = right curve, L = left curve. " +
                                          $"Bounds: {w}×{h} (canonical north up)", EditorStyles.miniLabel);
            }
        }

        private static char Glyph(TrackPieceShape pieceType)
        {
            if (pieceType.Id == TrackPieceShapes.Curve_1x1.Id) return 'R';
            if (pieceType.Id == TrackPieceShapes.LeftCurve_1x1.Id) return 'L';
            return 'F';
        }

        private static void AppendStep(TrackShapeAsset asset, TrackStep step)
        {
            Undo.RecordObject(asset, "Append Track Step");
            var list = new List<TrackStep>(asset.Steps) { step };
            asset.SetSteps(list.ToArray());
            EditorUtility.SetDirty(asset);
        }

        private static void DrawBox(Rect rect, TrackStep step)
        {
            EditorGUI.DrawRect(rect, BoxColor(step));
            var prev = GUI.color;
            GUI.color = Color.black;
            var style = new GUIStyle(EditorStyles.boldLabel)
            {
                alignment = TextAnchor.MiddleCenter,
                fontSize = 16
            };
            GUI.Label(rect, BoxLabel(step), style);
            GUI.color = prev;
        }

        private static Color BoxColor(TrackStep s) => s switch
        {
            TrackStep.Forward => BoxBgForward,
            TrackStep.TurnRight => BoxBgRight,
            TrackStep.TurnLeft => BoxBgLeft,
            _ => Color.gray
        };

        private static string BoxLabel(TrackStep s) => s switch
        {
            TrackStep.Forward => "F",
            TrackStep.TurnRight => "R",
            TrackStep.TurnLeft => "L",
            _ => "?"
        };

        private static TrackStep Cycle(TrackStep s) => s switch
        {
            TrackStep.Forward => TrackStep.TurnRight,
            TrackStep.TurnRight => TrackStep.TurnLeft,
            TrackStep.TurnLeft => TrackStep.Forward,
            _ => TrackStep.Forward
        };
    }
}
