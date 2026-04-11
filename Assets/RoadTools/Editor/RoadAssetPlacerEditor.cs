using UnityEditor;
using UnityEngine;
using System.IO;
using Rugem.RoadTools;

namespace Rugem.RoadTools.EditorTools
{
    [CustomEditor(typeof(RoadAssetPlacer))]
    public class RoadAssetPlacerEditor : Editor
    {
        public override void OnInspectorGUI()
        {
            base.OnInspectorGUI();
            RoadAssetPlacer script = (RoadAssetPlacer)target;

            GUILayout.Space(10);

            if (script.assetPrefab == null)
            {
                EditorGUILayout.HelpBox("assetPrefab을 먼저 설정해주세요.", MessageType.Warning);
                return;
            }

            GUI.color = Color.cyan;
            if (GUILayout.Button("CSV 가로수 데이터 로드 및 배치", GUILayout.Height(35)))
            {
                ProcessCSV(script);
            }
            GUI.color = Color.white;

            GUILayout.Space(5);

            GUI.color = new Color(1f, 0.5f, 0.5f);
            if (GUILayout.Button("배치된 나무 전체 삭제", GUILayout.Height(28)))
            {
                if (EditorUtility.DisplayDialog("확인", "배치된 모든 나무를 삭제하시겠습니까?", "삭제", "취소"))
                {
                    Undo.RegisterFullObjectHierarchyUndo(script.gameObject, "Clear All Trees");
                    script.ClearAllTrees();
                }
            }
            GUI.color = Color.white;
        }

        private void ProcessCSV(RoadAssetPlacer script)
        {
            string path = EditorUtility.OpenFilePanel("가로수 CSV 파일 선택", "", "csv");
            if (string.IsNullOrEmpty(path)) return;

            string[] lines = File.ReadAllLines(path);
            int totalLines = lines.Length - 1; // 헤더 제외
            int successCount = 0;

            Undo.RegisterFullObjectHierarchyUndo(script.gameObject, "Place Trees from CSV");

            try
            {
                for (int i = 1; i < lines.Length; i++)
                {
                    if (i % 10 == 0)
                    {
                        float progress = (float)(i - 1) / totalLines;
                        bool cancelled = EditorUtility.DisplayCancelableProgressBar(
                            "가로수 배치 중...",
                            $"노선 {i - 1} / {totalLines} 처리 중",
                            progress);

                        if (cancelled)
                        {
                            Debug.Log("[RoadTools] 사용자가 배치를 취소했습니다.");
                            break;
                        }
                    }

                    if (string.IsNullOrWhiteSpace(lines[i])) continue;

                    string[] data = lines[i].Split(',');
                    if (data.Length < 7) continue;

                    try
                    {
                        double sLat = double.Parse(data[1].Trim());
                        double sLon = double.Parse(data[2].Trim());
                        double eLat = double.Parse(data[3].Trim());
                        double eLon = double.Parse(data[4].Trim());
                        int treeCount = int.Parse(data[6].Trim());

                        script.PlaceTreeLine(sLat, sLon, eLat, eLon, treeCount);
                        successCount++;
                    }
                    catch (System.Exception ex)
                    {
                        Debug.LogWarning($"[RoadTools] {i + 1}번 줄 파싱 실패: {ex.Message}");
                    }
                }
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }

            EditorUtility.DisplayDialog("배치 완료", $"{successCount}개의 노선 배치를 완료했습니다.", "확인");
        }
    }
}
