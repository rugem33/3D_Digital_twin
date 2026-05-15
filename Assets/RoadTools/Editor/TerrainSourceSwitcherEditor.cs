using UnityEditor;
using UnityEngine;

namespace Rugem.RoadTools.Editor
{
    [CustomEditor(typeof(TerrainSourceSwitcher))]
    public class TerrainSourceSwitcherEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();

            TerrainSourceSwitcher switcher = (TerrainSourceSwitcher)target;

            EditorGUILayout.Space(8);
            EditorGUILayout.LabelField("── 런타임 테스트 도구 ──", EditorStyles.boldLabel);

            if (!Application.isPlaying)
            {
                EditorGUILayout.HelpBox(
                    "Play Mode에서만 동작합니다.\n\n" +
                    "방식 C 테스트 전에 터미널에서 실행:\n" +
                    "  pip install flask\n" +
                    "  python Tools/terrain_test_server.py",
                    MessageType.Info);
                return;
            }

            string current = $"현재: {switcher.CurrentMode}";
            EditorGUILayout.LabelField(current, EditorStyles.boldLabel);

            EditorGUILayout.Space(4);
            EditorGUILayout.BeginHorizontal();

            GUI.backgroundColor = switcher.CurrentMode == TerrainSourceMode.CesiumIon
                ? Color.green : Color.white;
            if (GUILayout.Button("Ion (방식 A)"))
                switcher.Apply(TerrainSourceMode.CesiumIon);

            GUI.backgroundColor = switcher.CurrentMode == TerrainSourceMode.CustomUrl
                ? Color.green : Color.white;
            if (GUILayout.Button("커스텀 URL (방식 C)"))
                switcher.Apply(TerrainSourceMode.CustomUrl);

            GUI.backgroundColor = switcher.CurrentMode == TerrainSourceMode.Ellipsoid
                ? Color.green : Color.white;
            if (GUILayout.Button("타원체 (오프라인)"))
                switcher.Apply(TerrainSourceMode.Ellipsoid);

            GUI.backgroundColor = Color.white;
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.Space(4);
            EditorGUILayout.HelpBox(
                "Ion ↔ 커스텀 URL 간 전환으로 동일 동작 여부 확인\n" +
                "타원체 = Ion 없이도 Cesium이 동작하는지 확인용",
                MessageType.None);
        }
    }
}
