using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityPpoRacingTrainer.Core.AiDriver.Training.Curriculum;

namespace UnityPpoRacingTrainer.Core.AiDriver.Editor
{
    /// <summary>
    /// PropertyDrawer for <see cref="ForceCircuitIdAttribute"/>. Scans
    /// <c>&lt;projectRoot&gt;/circuits/authored_closure/*.json</c> at edit
    /// time and renders the field as a popup over the discovered ids. The
    /// first entry is a "&lt;random&gt;" sentinel that writes an empty
    /// string (= random pick at runtime). Falls back to a plain text field
    /// when the directory is missing so a fresh clone still lets the user
    /// type an id.
    /// </summary>
    [CustomPropertyDrawer(typeof(ForceCircuitIdAttribute))]
    public sealed class ForceCircuitIdDrawer : PropertyDrawer
    {
        private const string RandomLabel = "<random>";
        // OnGUI fires many times per inspector tick; scan disk at most once
        // per Editor frame to keep large authored-closure dirs responsive.
        private static int _cachedFrame = -1;
        private static string[] _cachedIds;

        public override void OnGUI(Rect position, SerializedProperty property, GUIContent label)
        {
            if (property.propertyType != SerializedPropertyType.String)
            {
                EditorGUI.PropertyField(position, property, label);
                return;
            }

            string libDir = ResolveLibraryDir();
            if (!Directory.Exists(libDir))
            {
                EditorGUI.PropertyField(position, property, label);
                return;
            }

            string[] ids = GetIds(libDir);

            string current = property.stringValue ?? string.Empty;
            int selected = 0;
            for (int i = 0; i < ids.Length; i++)
            {
                if (ids[i] == current) { selected = i; break; }
            }

            int newSelected = EditorGUI.Popup(position, label.text, selected, BuildDisplay(ids));
            if (newSelected != selected)
            {
                property.stringValue = newSelected == 0 ? string.Empty : ids[newSelected];
            }
        }

        private static string ResolveLibraryDir()
        {
            // Application.dataPath ends with .../Assets; project root is one up.
            return Path.GetFullPath(Path.Combine(Application.dataPath, "..", CurriculumStages.AuthoredLibraryDir));
        }

        private static string[] GetIds(string libDir)
        {
            int frame = Time.frameCount;
            if (_cachedIds != null && _cachedFrame == frame) return _cachedIds;

            var ids = new List<string> { string.Empty };
            foreach (var path in Directory.GetFiles(libDir, "*.json"))
            {
                ids.Add(Path.GetFileNameWithoutExtension(path));
            }
            ids.Sort(1, ids.Count - 1, System.StringComparer.OrdinalIgnoreCase);

            _cachedIds = ids.ToArray();
            _cachedFrame = frame;
            return _cachedIds;
        }

        private static string[] BuildDisplay(string[] ids)
        {
            var display = new string[ids.Length];
            display[0] = RandomLabel;
            for (int i = 1; i < ids.Length; i++) display[i] = ids[i];
            return display;
        }
    }
}
