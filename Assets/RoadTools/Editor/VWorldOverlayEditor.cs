using UnityEditor;
using UnityEngine;

namespace Rugem.RoadTools.Editor
{
    [CustomEditor(typeof(VWorldOverlayController))]
    public class VWorldOverlayEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();

            VWorldOverlayController controller = (VWorldOverlayController)target;

            EditorGUILayout.Space(8);
            EditorGUILayout.LabelField("── 런타임 테스트 도구 ──", EditorStyles.boldLabel);

            if (!Application.isPlaying)
            {
                EditorGUILayout.HelpBox(
                    "▶ Play 전 필수 설정:\n" +
                    "1. Tileset GameObject 선택\n" +
                    "2. Add Component → Cesium → Cesium URL Template Raster Overlay\n" +
                    "3. 추가된 컴포넌트 체크박스를 OFF(비활성) 상태로 두기\n\n" +
                    "Play Mode에서 버튼이 활성화됩니다.",
                    MessageType.Warning);
                return;
            }

            // 현재 상태 표시
            string status = controller.IsOverlayActive
                ? $"활성 ({controller.CurrentLayer})"
                : "비활성";
            EditorGUILayout.LabelField("현재 상태", status);

            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("레이어 전환", EditorStyles.miniBoldLabel);

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("위성 (Satellite)"))
                controller.SwitchLayer(VWorldLayerType.Satellite);
            if (GUILayout.Button("지도 (Base)"))
                controller.SwitchLayer(VWorldLayerType.Base);
            if (GUILayout.Button("하이브리드 (Hybrid)"))
                controller.SwitchLayer(VWorldLayerType.Hybrid);
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("오버레이 제어", EditorStyles.miniBoldLabel);

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("표시 ON"))
                controller.SetVisible(true);
            if (GUILayout.Button("표시 OFF"))
                controller.SetVisible(false);
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("오버레이 제거"))
                controller.RemoveOverlay();
            if (GUILayout.Button("다시 적용"))
                controller.ApplyOverlay(controller.CurrentLayer);
            EditorGUILayout.EndHorizontal();
        }
    }
}
