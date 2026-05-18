using System;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using CesiumForUnity;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Rugem.RoadTools.Editor
{
    [CustomEditor(typeof(ShpToTileConverter))]
    public class ShpToTileConverterEditor : UnityEditor.Editor
    {
        private static readonly TimeSpan  PollInterval     = TimeSpan.FromSeconds(3);
        private static readonly TimeSpan  RequestTimeout   = TimeSpan.FromSeconds(30);
        private static readonly TimeSpan  TotalTimeout     = TimeSpan.FromMinutes(10);

        private bool   _isBusy        = false;
        private string _statusMessage = "";
        private bool   _isError       = false;

        // ── Inspector UI ──────────────────────────────────────────────
        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();

            var converter = (ShpToTileConverter)target;

            EditorGUILayout.Space(10);
            EditorGUILayout.LabelField("── 변환 및 배치 ──", EditorStyles.boldLabel);

            DrawFilePicker(converter);

            if (!string.IsNullOrEmpty(_statusMessage))
                EditorGUILayout.HelpBox(_statusMessage,
                    _isError ? MessageType.Error : MessageType.Info);

            EditorGUILayout.Space(4);

            GUI.enabled = !_isBusy;
            if (GUILayout.Button(
                    _isBusy ? "처리 중..." : "변환 요청 및 자동 배치",
                    GUILayout.Height(36)))
                _ = RunPipelineAsync(converter);
            GUI.enabled = true;

            // ── 로컬 테스트 섹션 ──────────────────────────────────────
            EditorGUILayout.Space(10);
            EditorGUILayout.LabelField("── 로컬 테스트 ──", EditorStyles.boldLabel);
            DrawLocalTestSection(converter);

            EditorGUILayout.Space(4);
            EditorGUILayout.HelpBox(
                "서버팀 구현 필요 엔드포인트\n" +
                "POST /api/convert  →  { jobId, status, tilesetUrl }\n" +
                "GET  /api/jobs/{jobId}  →  { jobId, status, tilesetUrl }",
                MessageType.None);
        }

        // ── 파일 선택 (ZIP + TIF) ─────────────────────────────────────
        private void DrawFilePicker(ShpToTileConverter converter)
        {
            // ZIP
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField(
                string.IsNullOrEmpty(converter.ShpZipPath) ? "ZIP 미선택" : Path.GetFileName(converter.ShpZipPath),
                EditorStyles.miniLabel);
            if (GUILayout.Button("ZIP 선택", GUILayout.Width(90)))
            {
                string path = EditorUtility.OpenFilePanel("SHP ZIP 선택", "", "zip");
                if (!string.IsNullOrEmpty(path))
                {
                    serializedObject.FindProperty("_shpZipPath").stringValue = path;
                    serializedObject.ApplyModifiedProperties();
                    _statusMessage = "";
                }
            }
            EditorGUILayout.EndHorizontal();

            // TIF (선택 사항)
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField(
                string.IsNullOrEmpty(converter.DemTifPath) ? "TIF 미선택 (선택 사항)" : Path.GetFileName(converter.DemTifPath),
                EditorStyles.miniLabel);
            if (GUILayout.Button("TIF 선택", GUILayout.Width(90)))
            {
                string path = EditorUtility.OpenFilePanel("DEM TIF 선택", "", "tif");
                if (!string.IsNullOrEmpty(path))
                {
                    serializedObject.FindProperty("_demTifPath").stringValue = path;
                    serializedObject.ApplyModifiedProperties();
                }
            }
            if (!string.IsNullOrEmpty(converter.DemTifPath) &&
                GUILayout.Button("✕", GUILayout.Width(24)))
            {
                serializedObject.FindProperty("_demTifPath").stringValue = "";
                serializedObject.ApplyModifiedProperties();
            }
            EditorGUILayout.EndHorizontal();
        }

        // ── 전체 파이프라인 ────────────────────────────────────────────
        private async Task RunPipelineAsync(ShpToTileConverter converter)
        {
            if (!ValidateInputs(converter)) return;

            _isBusy = true;

            try
            {
                // 1. 변환 요청
                SetStatus("서버에 변환 요청 중...");
                ConvertResponse response = await PostConvertAsync(converter);

                // 2. 완료 대기 (폴링)
                if (response.status == "running")
                    response = await PollUntilCompleteAsync(converter.ServerUrl, response.jobId);

                // 3. 결과 처리
                if (response.status == "failed")
                {
                    SetStatus($"서버 변환 오류: {response.error ?? response.message}", isError: true);
                    return;
                }

                if (string.IsNullOrEmpty(response.tilesetUrl))
                {
                    SetStatus("서버 응답에 tilesetUrl이 없습니다.", isError: true);
                    return;
                }

                // 4. 씬에 Cesium3DTileset 배치
                PlaceTileset(converter, response.tilesetUrl);
                SetStatus($"배치 완료!\n{response.tilesetUrl}");
            }
            catch (HttpRequestException ex)
            {
                SetStatus($"서버 연결 실패: {ex.Message}\n서버가 실행 중인지 확인하세요.", isError: true);
            }
            catch (TimeoutException)
            {
                SetStatus($"시간 초과 — 변환이 {TotalTimeout.TotalMinutes}분 안에 완료되지 않았습니다.", isError: true);
            }
            catch (Exception ex)
            {
                SetStatus($"오류: {ex.Message}", isError: true);
            }
            finally
            {
                _isBusy = false;
                Repaint();
            }
        }

        // ── POST /api/convert ─────────────────────────────────────────
        private async Task<ConvertResponse> PostConvertAsync(ShpToTileConverter converter)
        {
            string endpoint = converter.ServerUrl.TrimEnd('/') + "/api/convert";

            using var client = new HttpClient { Timeout = RequestTimeout };
            using var form   = new MultipartFormDataContent();

            // ZIP 파일 전송
            byte[] zipBytes = await File.ReadAllBytesAsync(converter.ShpZipPath);
            form.Add(new ByteArrayContent(zipBytes), "shpZip", Path.GetFileName(converter.ShpZipPath));

            // TIF 파일 첨부 (선택)
            if (!string.IsNullOrEmpty(converter.DemTifPath) && File.Exists(converter.DemTifPath))
            {
                byte[] tifBytes = await File.ReadAllBytesAsync(converter.DemTifPath);
                form.Add(new ByteArrayContent(tifBytes), "demFile", Path.GetFileName(converter.DemTifPath));
            }

            // mago-3D-tiler 파라미터
            form.Add(new StringContent(converter.OutputType.ToString()),          "outputType");
            form.Add(new StringContent(converter.CurvatureCorrection.ToString()), "curvatureCorrection");
            form.Add(new StringContent(converter.CoordinateSystem.ToString()),    "coordinateSystem");
            form.Add(new StringContent(converter.HeightColumn),                   "heightColumn");
            form.Add(new StringContent(converter.ScaleHeight.ToString("F1", CultureInfo.InvariantCulture)), "scaleHeight");

            HttpResponseMessage httpResponse = await client.PostAsync(endpoint, form);
            string body = await httpResponse.Content.ReadAsStringAsync();

            if (!httpResponse.IsSuccessStatusCode)
                throw new HttpRequestException($"HTTP {(int)httpResponse.StatusCode}: {body}");

            return ParseResponse(body);
        }

        // ── GET /api/jobs/{jobId} 폴링 ───────────────────────────────
        private async Task<ConvertResponse> PollUntilCompleteAsync(string serverUrl, string jobId)
        {
            string endpoint  = serverUrl.TrimEnd('/') + $"/api/jobs/{jobId}/status";
            DateTime deadline = DateTime.UtcNow + TotalTimeout;

            using var client = new HttpClient { Timeout = RequestTimeout };

            while (DateTime.UtcNow < deadline)
            {
                await Task.Delay(PollInterval);

                SetStatus($"변환 중... (Job: {jobId})");
                Repaint();

                HttpResponseMessage httpResponse = await client.GetAsync(endpoint);
                string body = await httpResponse.Content.ReadAsStringAsync();

                if (!httpResponse.IsSuccessStatusCode)
                    throw new HttpRequestException($"폴링 오류 HTTP {(int)httpResponse.StatusCode}: {body}");

                ConvertResponse response = ParseResponse(body);
                if (response.status == "completed" || response.status == "failed")
                    return response;
            }

            throw new TimeoutException();
        }

        // ── 씬에 Cesium3DTileset 배치 ─────────────────────────────────
        private void PlaceTileset(ShpToTileConverter converter, string tilesetUrl)
        {
            GameObject go = converter.TilesetObject;
            if (go == null)
            {
                SetStatus("배치할 오브젝트를 Inspector에서 지정해주세요.", isError: true);
                return;
            }

            if (!go.TryGetComponent<Cesium3DTileset>(out var tileset))
                tileset = go.AddComponent<Cesium3DTileset>();

            Undo.RecordObject(tileset, $"Update Tileset {go.name}");

            tileset.tilesetSource           = CesiumDataSource.FromUrl;
            tileset.url                     = tilesetUrl;
            tileset.maximumScreenSpaceError = converter.MaxScreenSpaceError;

            EditorSceneManager.MarkSceneDirty(SceneManager.GetActiveScene());
            Selection.activeGameObject = go;
            EditorGUIUtility.PingObject(go);
        }

        // ── 로컬 테스트 UI ────────────────────────────────────────────
        private void DrawLocalTestSection(ShpToTileConverter converter)
        {
            // 파일 선택 행
            EditorGUILayout.BeginHorizontal();
            string label = string.IsNullOrEmpty(converter.LocalTilesetPath)
                ? "tileset.json 미선택"
                : Path.GetFileName(converter.LocalTilesetPath);
            EditorGUILayout.LabelField(label, EditorStyles.miniLabel);

            if (GUILayout.Button("tileset.json 선택", GUILayout.Width(130)))
            {
                string path = EditorUtility.OpenFilePanel("tileset.json 선택", "", "json");
                if (!string.IsNullOrEmpty(path))
                {
                    serializedObject.FindProperty("_localTilesetPath").stringValue = path;
                    serializedObject.ApplyModifiedProperties();
                }
            }
            EditorGUILayout.EndHorizontal();

            // 선택된 경로 표시
            if (!string.IsNullOrEmpty(converter.LocalTilesetPath))
            {
                bool exists = File.Exists(converter.LocalTilesetPath);
                EditorGUILayout.HelpBox(
                    $"{(exists ? "✓" : "✗ 파일 없음")}  {converter.LocalTilesetPath}",
                    exists ? MessageType.None : MessageType.Warning);
            }

            // 로컬 배치 버튼
            bool canPlace = !string.IsNullOrEmpty(converter.LocalTilesetPath)
                            && File.Exists(converter.LocalTilesetPath);
            GUI.enabled = canPlace;
            if (GUILayout.Button("로컬 파일로 배치 (서버 없이)", GUILayout.Height(30)))
            {
                string fileUri = new Uri(converter.LocalTilesetPath).AbsoluteUri;
                PlaceTileset(converter, fileUri);
                SetStatus($"로컬 배치 완료!\n{fileUri}");
            }
            GUI.enabled = true;
        }

        // ── 유효성 검사 ───────────────────────────────────────────────
        private bool ValidateInputs(ShpToTileConverter converter)
        {
            if (string.IsNullOrWhiteSpace(converter.ShpZipPath) ||
                !File.Exists(converter.ShpZipPath))
            {
                SetStatus("ZIP 파일을 선택해주세요.", isError: true);
                return false;
            }
            if (string.IsNullOrWhiteSpace(converter.ServerUrl))
            {
                SetStatus("Server URL을 입력해주세요.", isError: true);
                return false;
            }
            return true;
        }

        // ── 응답 파싱 ─────────────────────────────────────────────────
        private static ConvertResponse ParseResponse(string json)
        {
            try   { return JsonUtility.FromJson<ConvertResponse>(json); }
            catch { return new ConvertResponse { status = "error", message = $"JSON 파싱 실패: {json}" }; }
        }

        private void SetStatus(string message, bool isError = false)
        {
            _statusMessage = message;
            _isError       = isError;
            Repaint();
        }

        // ── 서버 응답 모델 ─────────────────────────────────────────────
        [Serializable]
        private class ConvertResponse
        {
            public string jobId;
            public string status;
            public string tilesetUrl;
            public string message;
            public string error;
        }
    }
}
