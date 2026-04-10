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
            GUI.color = Color.cyan; // 버튼 색상 구분
            if (GUILayout.Button("CSV 가로수 데이터 로드 및 배치", GUILayout.Height(35)))
            {
                ProcessCSV(script);
            }
            GUI.color = Color.white;
        }

        private void ProcessCSV(RoadAssetPlacer script)
        {
            string path = EditorUtility.OpenFilePanel("가로수 CSV 파일 선택", "", "csv");
            if (string.IsNullOrEmpty(path)) return;

            string[] lines = File.ReadAllLines(path);
            int successCount = 0;

            // 첫 줄(헤더)을 건너뛰려면 i = 1, 헤더가 없다면 i = 0부터 시작하세요.
            for (int i = 1; i < lines.Length; i++)
            {
                if (string.IsNullOrWhiteSpace(lines[i])) continue;

                // 콤마로 분리
                string[] data = lines[i].Split(',');

                // 데이터 개수가 부족하면 스킵 (최소 7개 이상 필요)
                if (data.Length < 7) continue;

                try
                {
                    // 유성님 데이터셋 맞춤 인덱스
                    double sLat = double.Parse(data[1].Trim());
                    double sLon = double.Parse(data[2].Trim());
                    double eLat = double.Parse(data[3].Trim());
                    double eLon = double.Parse(data[4].Trim());

                    // 수량 파싱 (정수형)
                    int treeCount = int.Parse(data[6].Trim());

                    // 배치 실행
                    script.PlaceTreeLine(sLat, sLon, eLat, eLon, treeCount);
                    successCount++;
                }
                catch (System.Exception ex)
                {
                    // 어떤 줄에서 어떤 값이 문제인지 로그를 남깁니다.
                    Debug.LogWarning($"{i + 1}번 줄 파싱 실패: {ex.Message}");
                    continue;
                }
            }
            EditorUtility.DisplayDialog("배치 완료", $"{successCount}개의 노선 배치를 완료했습니다.", "확인");
        }
    }
}