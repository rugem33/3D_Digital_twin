using System;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

namespace Rugem.RoadTools.Editor
{
    [CustomEditor(typeof(TerrainSourceSwitcher))]
    public class TerrainSourceSwitcherEditor : UnityEditor.Editor
    {
        private static readonly TimeSpan _terrainPoll    = TimeSpan.FromSeconds(5);
        private static readonly TimeSpan _terrainTimeout = TimeSpan.FromMinutes(30);
        private static readonly TimeSpan _httpTimeout    = TimeSpan.FromSeconds(30);

        private bool   _isTesting      = false;
        private string _testResult     = "";
        private bool   _testIsError    = false;

        private bool   _isConverting   = false;
        private string _convertStatus  = "";
        private bool   _convertIsError = false;

        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();

            TerrainSourceSwitcher switcher = (TerrainSourceSwitcher)target;

            // ── TIF 전송 ──────────────────────────────────────────────
            EditorGUILayout.Space(8);
            EditorGUILayout.LabelField("── TIF → 지형 서버 전송 ──", EditorStyles.boldLabel);
            DrawTifUploadSection(switcher);

            // ── 에디터 진단 (Play 불필요) ──────────────────────────────
            EditorGUILayout.Space(8);
            EditorGUILayout.LabelField("── URL 진단 (에디터) ──", EditorStyles.boldLabel);

            GUI.enabled = !_isTesting;
            if (GUILayout.Button(
                    _isTesting ? "접속 테스트 중..." : "layer.json 접속 테스트",
                    GUILayout.Height(30)))
                _ = RunUrlTestAsync(switcher);
            GUI.enabled = true;

            if (!string.IsNullOrEmpty(_testResult))
            {
                EditorGUILayout.HelpBox(_testResult,
                    _testIsError ? MessageType.Error : MessageType.Info);
            }

            // ── 런타임 전환 버튼 (Play 전용) ──────────────────────────
            EditorGUILayout.Space(8);
            EditorGUILayout.LabelField("── 런타임 테스트 도구 ──", EditorStyles.boldLabel);

            if (!Application.isPlaying)
            {
                EditorGUILayout.HelpBox(
                    "아래 버튼은 Play Mode에서만 동작합니다.\n\n" +
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

        // ── TIF 선택 + 전송 UI ───────────────────────────────────────
        private void DrawTifUploadSection(TerrainSourceSwitcher switcher)
        {
            // TIF 파일 선택 행
            EditorGUILayout.BeginHorizontal();
            string tifLabel = string.IsNullOrEmpty(switcher.DemTifPath)
                ? "TIF 미선택"
                : Path.GetFileName(switcher.DemTifPath);
            EditorGUILayout.LabelField(tifLabel, EditorStyles.miniLabel);

            if (GUILayout.Button("TIF 선택", GUILayout.Width(90)))
            {
                string path = EditorUtility.OpenFilePanel("DEM TIF 선택", "", "tif");
                if (!string.IsNullOrEmpty(path))
                {
                    serializedObject.FindProperty("_demTifPath").stringValue = path;
                    serializedObject.ApplyModifiedProperties();
                    _convertStatus = "";
                }
            }
            if (!string.IsNullOrEmpty(switcher.DemTifPath) &&
                GUILayout.Button("✕", GUILayout.Width(24)))
            {
                serializedObject.FindProperty("_demTifPath").stringValue = "";
                serializedObject.ApplyModifiedProperties();
                _convertStatus = "";
            }
            EditorGUILayout.EndHorizontal();

            // 선택된 경로 표시
            if (!string.IsNullOrEmpty(switcher.DemTifPath))
            {
                bool exists = File.Exists(switcher.DemTifPath);
                EditorGUILayout.HelpBox(
                    $"{(exists ? "✓" : "✗ 파일 없음")}  {switcher.DemTifPath}",
                    exists ? MessageType.None : MessageType.Warning);
            }

            // 변환 상태 표시
            if (!string.IsNullOrEmpty(_convertStatus))
                EditorGUILayout.HelpBox(_convertStatus,
                    _convertIsError ? MessageType.Error : MessageType.Info);

            // 전송 버튼
            bool canSend = !_isConverting
                           && !string.IsNullOrEmpty(switcher.DemTifPath)
                           && File.Exists(switcher.DemTifPath);
            GUI.enabled = canSend;
            if (GUILayout.Button(
                    _isConverting ? "전송 중..." : "지형 서버에 TIF 전송",
                    GUILayout.Height(30)))
                _ = SendTifAsync(switcher);
            GUI.enabled = true;
        }

        private async Task SendTifAsync(TerrainSourceSwitcher switcher)
        {
            _isConverting = true;
            SetConvertStatus("서버에 TIF 전송 중...");

            try
            {
                string convertUrl = switcher.TerrainConvertServerUrl.TrimEnd('/') + "/api/terrain/convert";

                using var client = new HttpClient { Timeout = _httpTimeout };
                using var form   = new MultipartFormDataContent();

                byte[] tifBytes = await File.ReadAllBytesAsync(switcher.DemTifPath);
                form.Add(new ByteArrayContent(tifBytes), "demFile", Path.GetFileName(switcher.DemTifPath));

                HttpResponseMessage resp = await client.PostAsync(convertUrl, form);
                string body = await resp.Content.ReadAsStringAsync();

                if (!resp.IsSuccessStatusCode)
                {
                    SetConvertStatus($"서버 오류 ({(int)resp.StatusCode}): {body}", true);
                    return;
                }

                var startResp = JsonUtility.FromJson<TerrainJobResponse>(body);
                if (string.IsNullOrEmpty(startResp?.jobId))
                {
                    SetConvertStatus("응답에 jobId가 없습니다.", true);
                    return;
                }

                // 폴링
                string statusUrl = switcher.TerrainConvertServerUrl.TrimEnd('/')
                                   + $"/api/terrain/jobs/{startResp.jobId}/status";
                DateTime deadline = DateTime.UtcNow + _terrainTimeout;
                TerrainJobResponse status = startResp;

                while (DateTime.UtcNow < deadline)
                {
                    await Task.Delay(_terrainPoll);
                    SetConvertStatus($"변환 중... (Job: {startResp.jobId})");
                    Repaint();

                    using var poll = new HttpClient { Timeout = _httpTimeout };
                    HttpResponseMessage pr = await poll.GetAsync(statusUrl);
                    if (!pr.IsSuccessStatusCode) continue;

                    status = JsonUtility.FromJson<TerrainJobResponse>(
                        await pr.Content.ReadAsStringAsync());

                    if (status.status == "completed" || status.status == "failed")
                        break;
                }

                if (status.status == "failed")
                {
                    SetConvertStatus($"변환 실패: {status.error}", true);
                    return;
                }
                if (status.status != "completed")
                {
                    SetConvertStatus("타임아웃: 변환이 완료되지 않았습니다.", true);
                    return;
                }

                string layerUrl = status.layerUrl ?? status.layer_url;
                if (string.IsNullOrEmpty(layerUrl))
                {
                    SetConvertStatus("응답에 layerUrl이 없습니다.", true);
                    return;
                }

                // Terrain URL 자동 설정
                serializedObject.FindProperty("_terrainUrl").stringValue = layerUrl;
                serializedObject.ApplyModifiedProperties();
                SetConvertStatus($"완료! Terrain URL 설정됨:\n{layerUrl}");
            }
            catch (HttpRequestException ex)
            {
                SetConvertStatus($"서버 연결 실패: {ex.Message}", true);
            }
            catch (Exception ex)
            {
                SetConvertStatus($"오류: {ex.Message}", true);
            }
            finally
            {
                _isConverting = false;
                Repaint();
            }
        }

        private void SetConvertStatus(string msg, bool isError = false)
        {
            _convertStatus  = msg;
            _convertIsError = isError;
            Repaint();
        }

        [Serializable]
        private class TerrainJobResponse
        {
            public string jobId;
            public string status;
            public string layerUrl;
            public string layer_url;
            public string error;
        }

        // ── HTTP GET layer.json 진단 ──────────────────────────────────
        private async Task RunUrlTestAsync(TerrainSourceSwitcher switcher)
        {
            // Inspector에서 SerializedObject 통해 URL 읽기
            string url = serializedObject.FindProperty("_terrainUrl")?.stringValue ?? "";
            if (string.IsNullOrWhiteSpace(url))
            {
                SetTestResult("Terrain URL이 비어 있습니다.", isError: true);
                return;
            }

            _isTesting = true;
            SetTestResult($"접속 시도 중: {url}");

            try
            {
                using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
                HttpResponseMessage resp = await client.GetAsync(url);
                string body = await resp.Content.ReadAsStringAsync();

                if (!resp.IsSuccessStatusCode)
                {
                    SetTestResult(
                        $"✗ HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}\n" +
                        $"URL: {url}\n" +
                        $"서버가 실행 중인지 확인하세요.",
                        isError: true);
                    return;
                }

                // 포맷 검증
                bool hasQuantizedMesh = body.Contains("quantized-mesh");
                bool hasTilejson      = body.Contains("\"tilejson\"") || body.Contains("\"format\"");

                string preview = body.Length > 500 ? body.Substring(0, 500) + "\n…(생략)" : body;

                if (!hasQuantizedMesh)
                {
                    SetTestResult(
                        $"✗ quantized-mesh 포맷 아님\n" +
                        $"HTTP {(int)resp.StatusCode} OK  ({body.Length} bytes)\n\n" +
                        preview,
                        isError: true);
                }
                else
                {
                    SetTestResult(
                        $"✓ quantized-mesh 확인됨\n" +
                        $"HTTP {(int)resp.StatusCode} OK  ({body.Length} bytes)\n\n" +
                        preview);
                }
            }
            catch (TaskCanceledException)
            {
                SetTestResult($"✗ 타임아웃 (10초)\nURL: {url}", isError: true);
            }
            catch (Exception ex)
            {
                SetTestResult($"✗ 오류: {ex.Message}\nURL: {url}", isError: true);
            }
            finally
            {
                _isTesting = false;
                Repaint();
            }
        }

        private void SetTestResult(string message, bool isError = false)
        {
            _testResult  = message;
            _testIsError = isError;
            Repaint();
        }
    }
}
