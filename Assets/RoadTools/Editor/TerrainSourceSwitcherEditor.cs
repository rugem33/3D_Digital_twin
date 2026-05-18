using System.IO;
using UnityEditor;
using UnityEngine;

namespace Rugem.RoadTools.Editor
{
    [CustomEditor(typeof(TerrainSourceSwitcher))]
    public class TerrainSourceSwitcherEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            serializedObject.Update();

            SerializedProperty prop = serializedObject.GetIterator();
            bool enterChildren = true;
            while (prop.NextVisible(enterChildren))
            {
                enterChildren = false;

                if (prop.propertyPath == "m_Script")
                {
                    using (new EditorGUI.DisabledScope(true))
                        EditorGUILayout.PropertyField(prop);
                    continue;
                }

                if (prop.propertyPath == "_demFilePath")
                {
                    DrawDemFilePath(prop);
                    continue;
                }

                EditorGUILayout.PropertyField(prop, true);
            }

            serializedObject.ApplyModifiedProperties();

            EditorGUILayout.Space(8);
            EditorGUILayout.LabelField("Terrain Source Tools", EditorStyles.boldLabel);

            TerrainSourceSwitcher sw = (TerrainSourceSwitcher)target;

            EditorGUILayout.LabelField($"Current Mode: {sw.CurrentMode}", EditorStyles.boldLabel);
            EditorGUILayout.Space(4);

            EditorGUILayout.BeginHorizontal();
            ModeButton(sw, "Ion", TerrainSourceMode.CesiumIon);
            ModeButton(sw, "Custom URL", TerrainSourceMode.CustomUrl);
            ModeButton(sw, "Ellipsoid", TerrainSourceMode.Ellipsoid);
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.Space(4);
            GUI.backgroundColor = sw.CurrentMode == TerrainSourceMode.DemServer ? Color.cyan : Color.white;
            string demButtonLabel = !string.IsNullOrWhiteSpace(sw.ResolvedTerrainUrl)
                ? "Apply DEM Server URL"
                : Application.isPlaying ? "Upload DEM Server" : "Apply DEM Server URL";
            if (GUILayout.Button(demButtonLabel))
                sw.Apply(TerrainSourceMode.DemServer);
            GUI.backgroundColor = Color.white;

            DrawResolvedUrl(sw);
            DrawDemStatus(sw);
            DrawHttpWarning();
        }

        private static void DrawDemFilePath(SerializedProperty prop)
        {
            EditorGUILayout.LabelField(prop.displayName, EditorStyles.label);
            Rect row = EditorGUILayout.GetControlRect(false, EditorGUIUtility.singleLineHeight);
            Rect field = new Rect(row.x, row.y, row.width - 34, row.height);
            Rect button = new Rect(row.xMax - 32, row.y, 32, row.height);

            prop.stringValue = EditorGUI.TextField(field, prop.stringValue);
            if (GUI.Button(button, "..."))
            {
                string currentDir = string.IsNullOrEmpty(prop.stringValue)
                    ? ""
                    : Path.GetDirectoryName(prop.stringValue);
                string selected = EditorUtility.OpenFilePanel(
                    "Select DEM File",
                    currentDir ?? "",
                    "tif,tiff,img,hgt"
                );
                if (!string.IsNullOrEmpty(selected))
                    prop.stringValue = selected;
            }
        }

        private static void DrawResolvedUrl(TerrainSourceSwitcher sw)
        {
            if (sw.CurrentMode != TerrainSourceMode.CustomUrl &&
                sw.CurrentMode != TerrainSourceMode.DemServer)
            {
                return;
            }

            EditorGUILayout.Space(4);
            string url = sw.ResolvedTerrainUrl;
            MessageType type = string.IsNullOrWhiteSpace(url) ? MessageType.Warning : MessageType.Info;
            string message = string.IsNullOrWhiteSpace(url)
                ? "No terrain URL is available. Set Terrain Url or Dem Job Id."
                : $"Resolved terrain URL:\n{url}";
            EditorGUILayout.HelpBox(message, type);
        }

        private static void DrawDemStatus(TerrainSourceSwitcher sw)
        {
            if (sw.CurrentMode != TerrainSourceMode.DemServer &&
                sw.DemStatus == DemUploadStatus.Idle)
            {
                return;
            }

            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("DEM Status", EditorStyles.boldLabel);

            MessageType msgType = sw.DemStatus switch
            {
                DemUploadStatus.Completed => MessageType.Info,
                DemUploadStatus.Failed => MessageType.Error,
                _ => MessageType.None,
            };

            if (!string.IsNullOrEmpty(sw.DemStatusMessage))
                EditorGUILayout.HelpBox(sw.DemStatusMessage, msgType);

            if (sw.DemStatus is DemUploadStatus.Uploading or DemUploadStatus.Processing)
                EditorWindow.focusedWindow?.Repaint();
        }

        private static void DrawHttpWarning()
        {
#if UNITY_2022_2_OR_NEWER
            if (PlayerSettings.insecureHttpOption == InsecureHttpOption.AlwaysAllowed)
                return;

            EditorGUILayout.Space(4);
            EditorGUILayout.HelpBox(
                "HTTP downloads are blocked. Enable them to load localhost terrain.",
                MessageType.Warning);

            if (GUILayout.Button("Allow HTTP Downloads"))
            {
                PlayerSettings.insecureHttpOption = InsecureHttpOption.AlwaysAllowed;
                Debug.Log("[RoadTools] Player Settings > Allow downloads over HTTP = Always allowed");
            }
#endif
        }

        private static void ModeButton(TerrainSourceSwitcher sw, string label, TerrainSourceMode mode)
        {
            GUI.backgroundColor = sw.CurrentMode == mode ? Color.green : Color.white;
            if (GUILayout.Button(label))
                sw.Apply(mode);
            GUI.backgroundColor = Color.white;
        }
    }
}
