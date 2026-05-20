using UnityEditor;
using UnityEngine;

namespace Rugem.RoadTools.Editor
{
    [CustomEditor(typeof(VWorldOverlayController))]
    public class VWorldOverlayEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            EditorGUI.BeginChangeCheck();
            DrawDefaultInspector();
            bool inspectorChanged = EditorGUI.EndChangeCheck();

            VWorldOverlayController controller = (VWorldOverlayController)target;

            if (inspectorChanged && !Application.isPlaying)
                controller.ApplyOverlayImmediate(controller.CurrentLayer, controller.IsOverlayActive);

            EditorGUILayout.Space(8);
            EditorGUILayout.LabelField("── 런타임 테스트 도구 ──", EditorStyles.boldLabel);

            if (!Application.isPlaying)
            {
                EditorGUILayout.HelpBox(
                    "▶ Play 전 필수 설정:\n" +
                    "1. Tileset GameObject 선택\n" +
                    "2. Add Component → Cesium → Cesium URL Template Raster Overlay\n" +
                    "3. 추가된 컴포넌트 체크박스를 OFF(비활성) 상태로 두기\n\n" +
                    "Edit Mode에서도 아래 버튼으로 URL을 즉시 적용할 수 있습니다.",
                    MessageType.Info);
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
                ApplyLayer(controller, VWorldLayerType.Satellite);
            if (GUILayout.Button("지도 (Base)"))
                ApplyLayer(controller, VWorldLayerType.Base);
            if (GUILayout.Button("하이브리드 (Hybrid)"))
                ApplyLayer(controller, VWorldLayerType.Hybrid);
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("오버레이 제어", EditorStyles.miniBoldLabel);

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("표시 ON"))
                SetVisible(controller, true);
            if (GUILayout.Button("표시 OFF"))
                SetVisible(controller, false);
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("오버레이 제거"))
                controller.RemoveOverlay();
            if (GUILayout.Button("다시 적용"))
                ApplyLayer(controller, controller.CurrentLayer);
            EditorGUILayout.EndHorizontal();
        }

        private static void ApplyLayer(VWorldOverlayController controller, VWorldLayerType layer)
        {
            if (Application.isPlaying)
                controller.SwitchLayer(layer);
            else
                controller.ApplyOverlayImmediate(layer, true);
        }

        private static void SetVisible(VWorldOverlayController controller, bool visible)
        {
            if (Application.isPlaying)
                controller.SetVisible(visible);
            else
                controller.ApplyOverlayImmediate(controller.CurrentLayer, visible);
        }
    }
}
