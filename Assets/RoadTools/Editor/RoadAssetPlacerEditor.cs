using UnityEditor;
using UnityEngine;
using System.IO;
using System.Collections.Generic;
using Rugem.RoadTools;

namespace Rugem.RoadTools.EditorTools
{
    [CustomEditor(typeof(RoadAssetPlacer))]
    public class RoadAssetPlacerEditor : UnityEditor.Editor
    {
        // ── 점 데이터 CSV 설정 ─────────────────────────────────────────────────
        private bool   _pointFoldout   = true;
        private int    _latColumn      = 1;
        private int    _lonColumn      = 2;
        private bool   _hasHeader      = true;
        private string _groupName      = "PointAssets";
        private string _pointTypeName  = "시설물";

        // ── 선 데이터 CSV 설정 ─────────────────────────────────────────────────
        private bool   _lineFoldout    = true;
        private string _lineTypeName   = "가로수";

        // ── US-08 가시성 / 메쉬 분리 ──────────────────────────────────────────
        private bool _visibilityFoldout = true;
        private bool _meshDetachFoldout = false;

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

                _lineTypeName = EditorGUILayout.TextField("타입 이름", _lineTypeName);

                GUI.color = Color.cyan;
                if (GUILayout.Button("CSV 선 데이터 로드 및 배치", GUILayout.Height(32)))
                    ProcessLineCSV(script, _lineTypeName);
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

                _hasHeader     = EditorGUILayout.Toggle("헤더 행 건너뜀", _hasHeader);
                _latColumn     = EditorGUILayout.IntField("위도(lat) 열 인덱스", _latColumn);
                _lonColumn     = EditorGUILayout.IntField("경도(lon) 열 인덱스", _lonColumn);
                _groupName     = EditorGUILayout.TextField("그룹 오브젝트 이름", _groupName);
                _pointTypeName = EditorGUILayout.TextField("타입 이름", _pointTypeName);

                GUILayout.Space(4);
                GUI.color = new Color(0.5f, 1f, 0.8f);
                if (GUILayout.Button("CSV 점 데이터 로드 및 배치", GUILayout.Height(32)))
                    ProcessPointCSV(script, _pointTypeName);
                GUI.color = Color.white;
                EditorGUI.indentLevel--;
            }
            EditorGUILayout.EndFoldoutHeaderGroup();

            GUILayout.Space(8);

            // ── 타입별 가시성 제어 ───────────────────────────────────────────
            _visibilityFoldout = EditorGUILayout.BeginFoldoutHeaderGroup(_visibilityFoldout, "타입별 가시성 제어");
            if (_visibilityFoldout)
            {
                EditorGUI.indentLevel++;
                var typeNames = script.GetAllTypeNames();
                if (typeNames.Count == 0)
                {
                    EditorGUILayout.HelpBox("배치된 타입이 없습니다.", MessageType.None);
                }
                else
                {
                    foreach (var typeName in typeNames)
                    {
                        bool current = script.GetTypeVisible(typeName);
                        bool next    = EditorGUILayout.Toggle(typeName, current);
                        if (next != current)
                        {
                            Undo.RegisterFullObjectHierarchyUndo(script.gameObject, $"Toggle Visibility: {typeName}");
                            script.SetTypeVisible(typeName, next);
                        }
                    }
                }
                EditorGUI.indentLevel--;
            }
            EditorGUILayout.EndFoldoutHeaderGroup();

            GUILayout.Space(6);

            // ── 메쉬 분리 ────────────────────────────────────────────────────
            _meshDetachFoldout = EditorGUILayout.BeginFoldoutHeaderGroup(_meshDetachFoldout, "메쉬 분리 (Mesh Detach)");
            if (_meshDetachFoldout)
            {
                EditorGUI.indentLevel++;
                EditorGUILayout.HelpBox(
                    "배치된 프리팹에서 MeshRenderer를 별도의 순수 메쉬 오브젝트로 추출합니다.\n" +
                    "추출된 메쉬는 [MeshContainer] 하위에 생성됩니다.",
                    MessageType.Info);

                GUI.color = new Color(1f, 0.85f, 0.4f);
                if (GUILayout.Button("메쉬 분리 실행", GUILayout.Height(30)))
                    DetachMeshesWithUndo(script);
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

        private void ProcessLineCSV(RoadAssetPlacer script, string typeName)
        {
            string path = EditorUtility.OpenFilePanel("선 데이터 CSV 파일 선택", "", "csv");
            if (string.IsNullOrEmpty(path)) return;

            string[] lines = File.ReadAllLines(path);
            int totalLines  = lines.Length - 1;
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

                        script.PlaceTreeLine(sLat, sLon, eLat, eLon, treeCount, typeName);
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

        private void ProcessPointCSV(RoadAssetPlacer script, string typeName)
        {
            string path = EditorUtility.OpenFilePanel("점 데이터 CSV 파일 선택", "", "csv");
            if (string.IsNullOrEmpty(path)) return;

            int minColumns = Mathf.Max(_latColumn, _lonColumn) + 1;

            string[] lines = File.ReadAllLines(path);
            int startLine   = _hasHeader ? 1 : 0;
            int totalLines  = lines.Length - startLine;
            int successCount = 0;
            int failCount    = 0;

            // 타입 그룹 하위에 그룹 오브젝트 배치
            GameObject typeGroupParent = script.GetOrCreateTypeGroup(typeName);
            GameObject groupObj = new GameObject(string.IsNullOrWhiteSpace(_groupName) ? "PointAssets" : _groupName);
            groupObj.transform.SetParent(typeGroupParent.transform);
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

            if (successCount == 0)
                Undo.DestroyObjectImmediate(groupObj);

            string message = $"배치 성공: {successCount}개";
            if (failCount > 0) message += $"\n지면 미감지 / 파싱 실패: {failCount}개";
            EditorUtility.DisplayDialog("배치 완료", message, "확인");

            Debug.Log($"[RoadTools] 점 데이터 배치 — 성공: {successCount}, 실패: {failCount}");
        }

        // ── 메쉬 분리 ──────────────────────────────────────────────────────────

        private void DetachMeshesWithUndo(RoadAssetPlacer script)
        {
            var containerGo = new GameObject("[MeshContainer]");
            containerGo.transform.SetParent(script.transform);
            Undo.RegisterCreatedObjectUndo(containerGo, "Detach Meshes");

            int count = script.DetachMeshes(containerGo.transform,
                go => Undo.RegisterCreatedObjectUndo(go, "Detach Meshes"));

            EditorUtility.DisplayDialog("메쉬 분리 완료", $"{count}개의 메쉬를 분리했습니다.", "확인");
        }

        // ── CSV 파싱 헬퍼 ──────────────────────────────────────────────────────

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
