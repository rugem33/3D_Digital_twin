using UnityEditor;
using UnityEngine;
using System.IO;
using System.Collections.Generic;
using Rugem.RoadTools;

namespace Rugem.RoadTools.EditorTools
{
    [CustomEditor(typeof(RoadAssetPlacer))]
    public class RoadAssetPlacerEditor : Editor
    {
        // ── 점 데이터 CSV 설정 (에디터 세션 내 유지) ──────────────────────────
        private bool   _pointFoldout   = true;
        private int    _latColumn      = 1;   // 0-based 열 인덱스
        private int    _lonColumn      = 2;
        private bool   _hasHeader      = true;
        private string _groupName      = "PointAssets";

        // ── 선 데이터 CSV 설정 ─────────────────────────────────────────────────
        private bool _lineFoldout = true;

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

            // ── 선(Line) 데이터 섹션 ─────────────────────────────────────────
            _lineFoldout = EditorGUILayout.BeginFoldoutHeaderGroup(_lineFoldout, "선(Line) 데이터 — 가로수·가로등 노선");
            if (_lineFoldout)
            {
                EditorGUI.indentLevel++;
                EditorGUILayout.HelpBox(
                    "CSV 형식: id, 시작위도, 시작경도, 종료위도, 종료경도, (기타), 갯수",
                    MessageType.Info);

                GUI.color = Color.cyan;
                if (GUILayout.Button("CSV 선 데이터 로드 및 배치", GUILayout.Height(32)))
                    ProcessLineCSV(script);
                GUI.color = Color.white;
                EditorGUI.indentLevel--;
            }
            EditorGUILayout.EndFoldoutHeaderGroup();

            GUILayout.Space(6);

            // ── 점(Point) 데이터 섹션 ───────────────────────────────────────
            _pointFoldout = EditorGUILayout.BeginFoldoutHeaderGroup(_pointFoldout, "점(Point) 데이터 — 버스정류장·표지판 등");
            if (_pointFoldout)
            {
                EditorGUI.indentLevel++;
                EditorGUILayout.HelpBox(
                    "위경도 한 쌍만 있는 CSV에 사용합니다.\n열 인덱스는 0부터 시작합니다.",
                    MessageType.Info);

                _hasHeader  = EditorGUILayout.Toggle("헤더 행 건너뜀", _hasHeader);
                _latColumn  = EditorGUILayout.IntField("위도(lat) 열 인덱스", _latColumn);
                _lonColumn  = EditorGUILayout.IntField("경도(lon) 열 인덱스", _lonColumn);
                _groupName  = EditorGUILayout.TextField("그룹 오브젝트 이름", _groupName);

                GUILayout.Space(4);
                GUI.color = new Color(0.5f, 1f, 0.8f);
                if (GUILayout.Button("CSV 점 데이터 로드 및 배치", GUILayout.Height(32)))
                    ProcessPointCSV(script);
                GUI.color = Color.white;
                EditorGUI.indentLevel--;
            }
            EditorGUILayout.EndFoldoutHeaderGroup();

            GUILayout.Space(8);

            // ── 공통: 전체 삭제 ──────────────────────────────────────────────
            GUI.color = new Color(1f, 0.5f, 0.5f);
            if (GUILayout.Button("배치된 오브젝트 전체 삭제", GUILayout.Height(28)))
            {
                if (EditorUtility.DisplayDialog("확인", "배치된 모든 오브젝트를 삭제하시겠습니까?", "삭제", "취소"))
                {
                    Undo.RegisterFullObjectHierarchyUndo(script.gameObject, "Clear All Assets");
                    script.ClearAllAssets();
                }
            }
            GUI.color = Color.white;
        }

        // ── 선 데이터 CSV 처리 ─────────────────────────────────────────────────

        private void ProcessLineCSV(RoadAssetPlacer script)
        {
            string path = EditorUtility.OpenFilePanel("선 데이터 CSV 파일 선택", "", "csv");
            if (string.IsNullOrEmpty(path)) return;

            string[] lines = File.ReadAllLines(path);
            int totalLines = lines.Length - 1;
            int successCount = 0;

            Undo.RegisterFullObjectHierarchyUndo(script.gameObject, "Place Line Assets from CSV");

            try
            {
                for (int i = 1; i < lines.Length; i++)
                {
                    if (i % 10 == 0)
                    {
                        float progress = (float)(i - 1) / totalLines;
                        bool cancelled = EditorUtility.DisplayCancelableProgressBar(
                            "선 데이터 배치 중...",
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
                        double sLat      = double.Parse(data[1].Trim());
                        double sLon      = double.Parse(data[2].Trim());
                        double eLat      = double.Parse(data[3].Trim());
                        double eLon      = double.Parse(data[4].Trim());
                        int    treeCount = int.Parse(data[6].Trim());

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

        // ── 점 데이터 CSV 처리 ─────────────────────────────────────────────────

        private void ProcessPointCSV(RoadAssetPlacer script)
        {
            string path = EditorUtility.OpenFilePanel("점 데이터 CSV 파일 선택", "", "csv");
            if (string.IsNullOrEmpty(path)) return;

            // 열 인덱스 유효성 검사
            int minColumns = Mathf.Max(_latColumn, _lonColumn) + 1;

            string[] lines = File.ReadAllLines(path);
            int startLine   = _hasHeader ? 1 : 0;
            int totalLines  = lines.Length - startLine;
            int successCount = 0;
            int failCount    = 0;

            // 모든 점을 하나의 그룹 오브젝트 아래 배치
            GameObject groupObj = new GameObject(string.IsNullOrWhiteSpace(_groupName) ? "PointAssets" : _groupName);
            groupObj.transform.SetParent(script.transform);
            Undo.RegisterCreatedObjectUndo(groupObj, "Place Point Assets from CSV");

            try
            {
                for (int i = startLine; i < lines.Length; i++)
                {
                    int displayIndex = i - startLine + 1;

                    if (displayIndex % 20 == 0)
                    {
                        float progress  = (float)(displayIndex - 1) / totalLines;
                        bool  cancelled = EditorUtility.DisplayCancelableProgressBar(
                            "점 데이터 배치 중...",
                            $"{displayIndex - 1} / {totalLines} 처리 중",
                            progress);
                        if (cancelled)
                        {
                            Debug.Log("[RoadTools] 사용자가 배치를 취소했습니다.");
                            break;
                        }
                    }

                    if (string.IsNullOrWhiteSpace(lines[i])) continue;

                    string[] data = SplitCSVLine(lines[i]);
                    if (data.Length < minColumns)
                    {
                        Debug.LogWarning($"[RoadTools] {i + 1}번 줄 열 수 부족 (필요: {minColumns}, 실제: {data.Length})");
                        failCount++;
                        continue;
                    }

                    try
                    {
                        double lat = double.Parse(data[_latColumn].Trim());
                        double lon = double.Parse(data[_lonColumn].Trim());

                        bool placed = script.PlacePointAsset(lat, lon, groupObj.transform);
                        if (placed) successCount++;
                        else        failCount++;
                    }
                    catch (System.Exception ex)
                    {
                        Debug.LogWarning($"[RoadTools] {i + 1}번 줄 파싱 실패: {ex.Message}");
                        failCount++;
                    }
                }
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }

            // 아무것도 배치되지 않으면 빈 그룹 오브젝트 제거
            if (successCount == 0)
            {
                Undo.DestroyObjectImmediate(groupObj);
            }

            string message = $"배치 성공: {successCount}개";
            if (failCount > 0) message += $"\n지면 미감지 / 파싱 실패: {failCount}개";
            EditorUtility.DisplayDialog("배치 완료", message, "확인");

            Debug.Log($"[RoadTools] 점 데이터 배치 — 성공: {successCount}, 실패: {failCount}");
        }

        /// <summary>
        /// 쉼표 구분 CSV 한 줄을 파싱합니다.
        /// 따옴표로 감싼 필드(쉼표 포함 가능)를 올바르게 처리합니다.
        /// </summary>
        private static string[] SplitCSVLine(string line)
        {
            var fields = new List<string>();
            bool inQuotes = false;
            int  start    = 0;

            for (int i = 0; i < line.Length; i++)
            {
                if (line[i] == '"')
                {
                    inQuotes = !inQuotes;
                }
                else if (line[i] == ',' && !inQuotes)
                {
                    fields.Add(line.Substring(start, i - start).Trim('"'));
                    start = i + 1;
                }
            }
            fields.Add(line.Substring(start).Trim('"'));
            return fields.ToArray();
        }
    }
}
