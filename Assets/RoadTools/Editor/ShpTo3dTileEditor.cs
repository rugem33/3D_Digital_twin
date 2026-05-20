using System.IO;
using UnityEditor;
using UnityEngine;

namespace Rugem.RoadTools.Editor
{
    [CustomEditor(typeof(ShpTo3dTile))]
    public class ShpTo3dTileEditor : UnityEditor.Editor
    {
        // Inspector 자동 갱신을 위한 타이머
        private double _lastRepaintTime;

        private void OnEnable()
        {
            EditorApplication.update += OnEditorUpdate;
        }

        private void OnDisable()
        {
            EditorApplication.update -= OnEditorUpdate;
        }

        private void OnEditorUpdate()
        {
            var tile = target as ShpTo3dTile;
            if (tile == null || !tile.IsConverting) return;

            // 변환 중에는 0.5초마다 Inspector 갱신
            if (EditorApplication.timeSinceStartup - _lastRepaintTime > 0.5)
            {
                _lastRepaintTime = EditorApplication.timeSinceStartup;
                Repaint();
            }
        }

        public override void OnInspectorGUI()
        {
            serializedObject.Update();

            var tile = (ShpTo3dTile)target;

            // ── 서버 설정 ──────────────────────────────────────────────
            DrawBoldLabel("서버 설정");
            EditorGUILayout.PropertyField(serializedObject.FindProperty("_serverUrl"),
                new GUIContent("서버 URL"));

            // ── SHP ZIP ────────────────────────────────────────────────
            EditorGUILayout.Space(4);
            DrawBoldLabel("SHP 파일 (ZIP)");
            DrawPathWithBrowse(serializedObject.FindProperty("_shpZipPath"), "ZIP 경로", "SHP ZIP", "zip");

            // ── DEM TIF ────────────────────────────────────────────────
            EditorGUILayout.Space(4);
            DrawBoldLabel("DEM 파일 (선택)");
            DrawPathWithBrowse(serializedObject.FindProperty("_demTifPath"), "TIF 경로", "DEM GeoTIFF", "tif");

            // ── 변환 옵션 ──────────────────────────────────────────────
            EditorGUILayout.Space(4);
            DrawPropertiesExcluding(serializedObject,
                "m_Script", "_serverUrl", "_shpZipPath", "_demTifPath");

            serializedObject.ApplyModifiedProperties();

            // ── 변환 실행 패널 ─────────────────────────────────────────
            EditorGUILayout.Space(8);
            DrawBoldLabel("── 변환 실행 ──");

            if (tile.IsConverting)
            {
                DrawProgressBar(tile);
                DrawCancelButton(tile);
            }
            else
            {
                DrawConvertButton(tile);
                DrawLastStageInfo(tile);
            }
        }

        // ── UI 블록 ────────────────────────────────────────────────────

        private static void DrawConvertButton(ShpTo3dTile tile)
        {
            using var color = new ColorScope(new Color(0.4f, 0.8f, 0.4f));
            if (GUILayout.Button("변환 시작", GUILayout.Height(30)))
            {
                tile.StartConvert();
            }
        }

        private static void DrawCancelButton(ShpTo3dTile tile)
        {
            EditorGUILayout.Space(2);
            using var color = new ColorScope(new Color(0.9f, 0.5f, 0.4f));
            if (GUILayout.Button("취소", GUILayout.Height(22)))
                tile.CancelConvert();
        }

        private static void DrawProgressBar(ShpTo3dTile tile)
        {
            var rect = GUILayoutUtility.GetRect(18, 22, GUILayout.ExpandWidth(true));
            float t = tile.Progress / 100f;
            EditorGUI.ProgressBar(rect, t,
                $"{StageLabel(tile.Stage)}  {tile.Progress}%");

            if (!string.IsNullOrEmpty(tile.ProgressText))
            {
                EditorGUILayout.LabelField(tile.ProgressText,
                    EditorStyles.miniLabel,
                    GUILayout.Height(14));
            }
        }

        private static void DrawLastStageInfo(ShpTo3dTile tile)
        {
            if (string.IsNullOrEmpty(tile.Stage)) return;

            var style = new GUIStyle(EditorStyles.miniLabel);
            if (tile.Stage == "completed")
                style.normal.textColor = new Color(0.3f, 0.7f, 0.3f);
            else if (tile.Stage == "failed" || tile.Stage == "cancelled")
                style.normal.textColor = new Color(0.9f, 0.3f, 0.3f);

            EditorGUILayout.LabelField($"마지막 상태: {StageLabel(tile.Stage)}", style);
            if (!string.IsNullOrEmpty(tile.ProgressText))
                EditorGUILayout.LabelField(tile.ProgressText, EditorStyles.miniLabel);
        }

        // ── 경로 필드 + 찾기 버튼 ─────────────────────────────────────

        private static void DrawPathWithBrowse(SerializedProperty prop, string label,
                                               string dialogTitle, string ext)
        {
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.PropertyField(prop, new GUIContent(label));
            bool clicked = GUILayout.Button("찾기", GUILayout.Width(46));
            EditorGUILayout.EndHorizontal();

            // EndHorizontal 이후에 다이얼로그를 열어야 레이아웃 스택이 깨지지 않음
            if (clicked)
            {
                string path = EditorUtility.OpenFilePanel($"{dialogTitle} 파일 선택", "", ext);
                if (!string.IsNullOrEmpty(path))
                    prop.stringValue = path;
            }

            if (!string.IsNullOrEmpty(prop.stringValue) && !File.Exists(prop.stringValue))
                EditorGUILayout.HelpBox($"파일 없음: {prop.stringValue}", MessageType.Warning);
        }

        // ── 유틸리티 ─────────────────────────────────────────────────

        private static void DrawBoldLabel(string text) =>
            EditorGUILayout.LabelField(text, EditorStyles.boldLabel);

        private static string StageLabel(string stage) => stage switch
        {
            "preparing"    => "준비 중",
            "uploading"    => "업로드 중",
            "queued"       => "대기 중",
            "received"     => "수신됨",
            "preprocess"   => "전처리 중",
            "tiling"       => "타일 생성 중",
            "postprocess"  => "후처리 중",
            "finalizing"   => "마무리 중",
            "completed"    => "완료",
            "failed"       => "실패",
            "cancelled"    => "취소됨",
            _              => stage,
        };

        // GUI 색상을 using 블록으로 안전하게 변경하는 헬퍼
        private sealed class ColorScope : System.IDisposable
        {
            private readonly Color _prev;
            public ColorScope(Color c) { _prev = GUI.backgroundColor; GUI.backgroundColor = c; }
            public void Dispose()      { GUI.backgroundColor = _prev; }
        }
    }
}
