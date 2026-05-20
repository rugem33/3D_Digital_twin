using System;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using CesiumForUnity;
using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace Rugem.RoadTools
{
    public enum OutputType { b3dm, i3dm, pnts }

    [Serializable]
    internal class ConvertJobResponse
    {
        public string job_id;
        public string jobId;
        public string status;
        public string statusUrl;

        public string ResolvedJobId  => string.IsNullOrEmpty(job_id) ? jobId : job_id;
        public string ResolvedStatus => string.IsNullOrEmpty(statusUrl) ? "" : statusUrl;
    }

    [Serializable]
    internal class JobStatusResponse
    {
        public string status;           // "running" | "completed" | "failed"
        public string stage;            // "preprocess" | "tiling" | "postprocess" | "completed" | "failed"
        public int    progress;         // 0–100
        public string progressText;
        public string tilesetUrl;       // camelCase
        public string tileset_url;      // snake_case (서버 중복 제공)
        public string error;

        public string ResolvedTilesetUrl =>
            !string.IsNullOrEmpty(tilesetUrl)  ? tilesetUrl  :
            !string.IsNullOrEmpty(tileset_url) ? tileset_url : "";
    }

    /// <summary>
    /// SHP → 3D Tiles 변환 클라이언트
    ///
    /// ── 동작 순서 ──────────────────────────────────────────────────────
    ///  1. [SHP ZIP 찾기] / [DEM TIF 찾기] 버튼으로 파일 선택
    ///  2. StartConvert() 호출 — 에디터 모드와 Play 모드 모두 동작
    ///  3. ZIP 파일을 multipart/form-data 로 POST {ServerUrl}/api/convert
    ///  4. 서버가 반환한 statusUrl 을 주기적으로 폴링
    ///  5. completed 시 tilesetUrl 을 Target Tileset 의 url 에 자동 적용
    ///     → 에디터 모드에서 즉시 타일 표시
    ///
    /// ── 씬 설정 ────────────────────────────────────────────────────────
    ///  1. 빈 GameObject 에 이 컴포넌트 추가
    ///  2. Target Tileset 에 Cesium3DTileset GameObject 연결
    ///  3. [찾기] 버튼으로 파일 선택 후 [변환 시작] 클릭
    /// ───────────────────────────────────────────────────────────────────
    /// </summary>
    [ExecuteAlways]
    [AddComponentMenu("RoadTools/Shp To 3D Tile")]
    public class ShpTo3dTile : MonoBehaviour
    {
        [Header("서버 설정")]
        [Tooltip("Mago 3D Tiler 서버 주소 (끝에 슬래시 없이)")]
        [SerializeField] private string _serverUrl = "http://localhost:8000";

        [Header("SHP 파일 (ZIP)")]
        [Tooltip(".shp / .dbf / .shx / .prj 를 묶은 ZIP 파일 경로")]
        [SerializeField] private string _shpZipPath;

        [Header("DEM 파일 (선택)")]
        [Tooltip(".tif GeoTIFF — 지형 높이 보정. 비워두면 전송하지 않음")]
        [SerializeField] private string _demTifPath;

        [Header("변환 옵션")]
        [SerializeField] private OutputType _outputType = OutputType.b3dm;
        [SerializeField] private bool _curvatureCorrection = true;
        [Tooltip("좌표계 EPSG 코드 (예: 5186 = GRS80 중부원점)")]
        [SerializeField] private int _coordinateSystem = 5186;
        [Tooltip("높이값으로 사용할 DBF 속성 컬럼명")]
        [SerializeField] private string _heightColumn = "height";
        [Tooltip("높이 스케일 배율")]
        [SerializeField] private float _scaleHeight = 10.0f;
        [Tooltip("추가 속성값 — JSON 객체 문자열 (예: {\"project\":\"demo\"})")]
        [SerializeField] private string _attributesJson = "{}";

        [Header("Cesium 연결")]
        [Tooltip("변환 완료 시 URL 을 적용할 Cesium3DTileset")]
        [SerializeField] private Cesium3DTileset _targetTileset;

        [Header("폴링 설정")]
        [SerializeField, Range(1f, 30f)]   private float _pollIntervalSeconds = 3f;
        [SerializeField, Range(30f, 1800f)] private float _timeoutSeconds = 300f;

        // ── 런타임 상태 (Inspector 표시용, SerializeField 아님) ──────────
        [HideInInspector] public bool   IsConverting;
        [HideInInspector] public int    Progress;
        [HideInInspector] public string Stage      = "";
        [HideInInspector] public string ProgressText = "";

        private string                  _currentJobId;
        private CancellationTokenSource _cts;

        public event Action<string> OnConvertCompleted;
        public event Action<string> OnConvertFailed;

        // ── 생명주기 ─────────────────────────────────────────────────────

        private void OnDisable()
        {
            // 에디터 모드에서는 Inspector 조작·씬 저장 등으로 OnDisable이 자주 호출되므로 취소하지 않음
#if !UNITY_EDITOR
            _cts?.Cancel();
#endif
        }

        private void OnDestroy() => _cts?.Cancel();

        // ── 파일 탐색기 ──────────────────────────────────────────────────

        /// <summary>SHP ZIP 파일을 탐색기로 선택합니다. UI 버튼에 연결하세요.</summary>
        public void BrowseShpZip()
        {
            string path = OpenFileDialog("SHP ZIP 파일 선택", "ZIP 파일\0*.zip\0모든 파일\0*.*\0\0");
            if (!string.IsNullOrEmpty(path))
            {
                _shpZipPath = path;
                Debug.Log($"[ShpTo3dTile] SHP ZIP 선택: {path}");
            }
        }

        /// <summary>DEM TIF 파일을 탐색기로 선택합니다. UI 버튼에 연결하세요.</summary>
        public void BrowseDemTif()
        {
            string path = OpenFileDialog("DEM GeoTIFF 파일 선택", "GeoTIFF\0*.tif;*.tiff\0모든 파일\0*.*\0\0");
            if (!string.IsNullOrEmpty(path))
            {
                _demTifPath = path;
                Debug.Log($"[ShpTo3dTile] DEM TIF 선택: {path}");
            }
        }

        // ── 공개 API ─────────────────────────────────────────────────────

        /// <summary>Inspector 에 설정된 파일/옵션으로 변환을 시작합니다.</summary>
        public void StartConvert()
        {
            if (IsConverting) { Debug.LogWarning("[ShpTo3dTile] 이미 변환이 진행 중입니다."); return; }
            _cts?.Dispose();
            _cts = new CancellationTokenSource();
            _ = ConvertAsync(_cts.Token);
        }

        /// <summary>파일 경로를 직접 지정하고 변환을 시작합니다.</summary>
        public void StartConvert(string shpZipPath, string demTifPath = null)
        {
            _shpZipPath = shpZipPath;
            _demTifPath = demTifPath ?? "";
            StartConvert();
        }

        /// <summary>진행 중인 변환을 취소합니다.</summary>
        public void CancelConvert()
        {
            _cts?.Cancel();
            SetState(false, "cancelled", 0, "변환 취소됨");
            Debug.Log("[ShpTo3dTile] 변환 취소됨");
        }

        // ── 비동기 변환 ───────────────────────────────────────────────────

        private async Task ConvertAsync(CancellationToken ct)
        {
            SetState(true, "preparing", 0, "파일 준비 중...");

            try
            {
                // 1. ZIP 파일 확인 및 읽기
                string zipPath = ResolvePath(_shpZipPath);
                if (string.IsNullOrEmpty(zipPath) || !File.Exists(zipPath))
                {
                    Fail($"SHP ZIP 파일을 찾을 수 없습니다: '{zipPath}'\n[찾기] 버튼으로 파일을 선택하세요.");
                    return;
                }

                // 2. multipart form 구성 — StreamContent로 스트리밍 업로드 (메모리에 통째로 올리지 않음)
                // Timeout.InfiniteTimeSpan: 대용량 파일 업로드 시 60초 초과 방지
                // 폴링 GET은 별도 CancellationToken으로 15초 타임아웃 제어
                using var httpClient = new HttpClient { Timeout = System.Threading.Timeout.InfiniteTimeSpan };
                using var form       = new MultipartFormDataContent();

                FileStream zipStream;
                try   { zipStream = new FileStream(zipPath, FileMode.Open, FileAccess.Read, FileShare.Read); }
                catch (Exception e) { Fail($"ZIP 파일 열기 실패: {e.Message}"); return; }
                long zipSizeKb = new FileInfo(zipPath).Length / 1024;

                var zipContent = new StreamContent(zipStream);
                zipContent.Headers.ContentType =
                    new System.Net.Http.Headers.MediaTypeHeaderValue("application/zip");
                form.Add(zipContent, "files[]", Path.GetFileName(zipPath));

                form.Add(new StringContent(_outputType.ToString()),                  "outputType");
                form.Add(new StringContent(_curvatureCorrection ? "true" : "false"), "curvatureCorrection");
                form.Add(new StringContent(_coordinateSystem.ToString()),            "coordinateSystem");
                form.Add(new StringContent(_heightColumn),                           "heightColumn");
                form.Add(new StringContent(_scaleHeight.ToString("F4")),             "scaleHeight");
                form.Add(new StringContent(_attributesJson),                         "attributes");

                // DEM 파일 (선택)
                string demPath = ResolvePath(_demTifPath);
                if (!string.IsNullOrEmpty(demPath) && File.Exists(demPath))
                {
                    FileStream demStream;
                    try   { demStream = new FileStream(demPath, FileMode.Open, FileAccess.Read, FileShare.Read); }
                    catch (Exception e) { Fail($"DEM 파일 열기 실패: {e.Message}"); return; }

                    var demContent = new StreamContent(demStream);
                    demContent.Headers.ContentType =
                        new System.Net.Http.Headers.MediaTypeHeaderValue("image/tiff");
                    form.Add(demContent, "demFile", Path.GetFileName(demPath));
                }

                // 3. POST /api/convert
                string convertUrl = $"{_serverUrl.TrimEnd('/')}/api/convert";
                SetState(true, "uploading", 0, $"업로드 중... ({zipSizeKb} KB)");
                Debug.Log($"[ShpTo3dTile] POST → {convertUrl}");

                HttpResponseMessage resp;
                try   { resp = await httpClient.PostAsync(convertUrl, form, ct); }
                catch (TaskCanceledException) { Fail("변환이 취소 또는 타임아웃되었습니다."); return; }
                catch (Exception e)           { Fail($"서버 연결 실패: {e.Message}"); return; }

                string respJson = await resp.Content.ReadAsStringAsync();

                if (!resp.IsSuccessStatusCode)
                {
                    Fail($"변환 요청 실패 (HTTP {(int)resp.StatusCode}): {respJson}");
                    return;
                }

                ConvertJobResponse jobResp;
                try   { jobResp = JsonUtility.FromJson<ConvertJobResponse>(respJson); }
                catch { Fail($"변환 응답 파싱 실패: {respJson}"); return; }

                if (string.IsNullOrEmpty(jobResp.ResolvedJobId))
                {
                    Fail($"job_id 를 찾을 수 없습니다: {respJson}");
                    return;
                }

                _currentJobId = jobResp.ResolvedJobId;
                Debug.Log($"[ShpTo3dTile] 작업 시작 — job_id: {_currentJobId}");

                string statusUrl = string.IsNullOrEmpty(jobResp.statusUrl)
                    ? $"{_serverUrl.TrimEnd('/')}/api/jobs/{_currentJobId}/status"
                    : jobResp.statusUrl;

                // 4. 상태 폴링
                await PollStatusAsync(httpClient, statusUrl, ct);
            }
            catch (Exception e) when (!ct.IsCancellationRequested)
            {
                Fail($"예상치 못한 오류: {e.Message}");
            }
        }

        private async Task PollStatusAsync(HttpClient client, string statusUrl, CancellationToken ct)
        {
            SetState(true, "queued", 0, "대기 중...");
            Debug.Log($"[ShpTo3dTile] 폴링 시작 → {statusUrl}");

            float elapsed    = 0f;
            int   httpErrors = 0;       // 연속 HTTP 오류 횟수
            const int MaxHttpErrors = 20; // 이 이상 연속 실패 시 중단

            while (elapsed < _timeoutSeconds)
            {
                try   { await Task.Delay((int)(_pollIntervalSeconds * 1000), ct); }
                catch (TaskCanceledException) { Fail("변환이 취소되었습니다."); return; }

                elapsed += _pollIntervalSeconds;

                HttpResponseMessage resp;
                try
                {
                    // 폴링 요청마다 15초 타임아웃 (전체 CancellationToken과 연결)
                    using var pollCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    pollCts.CancelAfter(TimeSpan.FromSeconds(15));
                    resp = await client.GetAsync(statusUrl, pollCts.Token);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    Fail("변환이 취소되었습니다.");
                    return;
                }
                catch (Exception e)
                {
                    httpErrors++;
                    Debug.LogWarning($"[ShpTo3dTile] [{elapsed:F0}s] 상태 조회 오류 ({httpErrors}/{MaxHttpErrors}): {e.Message}");
                    if (httpErrors >= MaxHttpErrors) { Fail($"서버 연결 오류 {MaxHttpErrors}회 초과 — {e.Message}"); return; }
                    continue;
                }

                if (!resp.IsSuccessStatusCode)
                {
                    httpErrors++;
                    string errBody = await resp.Content.ReadAsStringAsync();
                    Debug.LogWarning($"[ShpTo3dTile] [{elapsed:F0}s] HTTP {(int)resp.StatusCode} ({httpErrors}/{MaxHttpErrors}): {errBody}");
                    if (httpErrors >= MaxHttpErrors) { Fail($"서버 오류 {MaxHttpErrors}회 초과 (HTTP {(int)resp.StatusCode})"); return; }
                    continue;
                }

                // 성공 응답 → 오류 카운터 리셋
                httpErrors = 0;

                string json = await resp.Content.ReadAsStringAsync();

                JobStatusResponse s;
                try   { s = JsonUtility.FromJson<JobStatusResponse>(json); }
                catch { Debug.LogWarning($"[ShpTo3dTile] 상태 파싱 실패 (재시도): {json}"); continue; }

                SetState(true, s.stage ?? s.status, s.progress, s.progressText);
                Debug.Log($"[ShpTo3dTile] [{elapsed:F0}s] stage={Stage} {s.progress}% {s.progressText}");

                if (s.status == "completed")
                {
                    string tileUrl = s.ResolvedTilesetUrl;
                    if (string.IsNullOrEmpty(tileUrl))
                    {
                        tileUrl = $"{_serverUrl.TrimEnd('/')}/outputs/{_currentJobId}/tileset.json";
                        Debug.LogWarning($"[ShpTo3dTile] tilesetUrl 없음 — 기본 경로 사용: {tileUrl}");
                    }
                    else if (!tileUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                    {
                        // PUBLIC_BASE_URL 미설정 시 서버가 "/outputs/..." 상대 경로 반환 → 절대 URL로 변환
                        tileUrl = _serverUrl.TrimEnd('/') + (tileUrl.StartsWith("/") ? "" : "/") + tileUrl;
                    }
                    Complete(tileUrl);
                    return;
                }

                if (s.status == "failed")
                {
                    Fail($"서버 변환 실패: {s.error ?? s.progressText ?? "(원인 없음)"}");
                    return;
                }
            }

            Fail($"타임아웃 — {_timeoutSeconds}초 초과. job_id={_currentJobId}");
        }

        // ── 완료 / 실패 처리 ─────────────────────────────────────────────

        private void Complete(string tilesetUrl)
        {
            Debug.Log($"[ShpTo3dTile] 변환 완료 — {tilesetUrl}");

            if (_targetTileset != null)
            {
                _targetTileset.tilesetSource = CesiumDataSource.FromUrl;
                _targetTileset.url           = tilesetUrl;

#if UNITY_EDITOR
                // 에디터 모드에서 타일셋 즉시 리로드
                EditorUtility.SetDirty(_targetTileset);
                if (!Application.isPlaying)
                {
                    // Cesium3DTileset 내부 RecreateTileset() 호출 시도
                    var method = _targetTileset.GetType().GetMethod(
                        "RecreateTileset",
                        System.Reflection.BindingFlags.Instance |
                        System.Reflection.BindingFlags.Public   |
                        System.Reflection.BindingFlags.NonPublic);
                    method?.Invoke(_targetTileset, null);

                    UnityEditor.SceneManagement.EditorSceneManager
                        .MarkSceneDirty(gameObject.scene);
                }
#endif
                Debug.Log("[ShpTo3dTile] Cesium3DTileset URL 적용 완료");
            }

            SetState(false, "completed", 100, "변환 완료");
            OnConvertCompleted?.Invoke(tilesetUrl);
            RepaintEditor();
        }

        private void Fail(string message)
        {
            Debug.LogError($"[ShpTo3dTile] {message}");
            SetState(false, "failed", 0, message);
            OnConvertFailed?.Invoke(message);
            RepaintEditor();
        }

        // ── 유틸리티 ─────────────────────────────────────────────────────

        private void SetState(bool converting, string stage, int progress, string text)
        {
            IsConverting  = converting;
            Stage         = stage ?? "";
            Progress      = progress;
            ProgressText  = text ?? "";
        }

        private static void RepaintEditor()
        {
#if UNITY_EDITOR
            UnityEditorInternal.InternalEditorUtility.RepaintAllViews();
#endif
        }

        private static string ResolvePath(string path)
        {
            if (string.IsNullOrEmpty(path)) return "";
            if (Path.IsPathRooted(path))    return path;
            return Path.Combine(Application.streamingAssetsPath, path);
        }

        // ── 파일 탐색기 (플랫폼별) ───────────────────────────────────────

        private static string OpenFileDialog(string title, string filter)
        {
#if UNITY_EDITOR
            string ext = ExtractFirstExt(filter);
            return EditorUtility.OpenFilePanel(title, "", ext);
#elif UNITY_STANDALONE_WIN
            return WinOpenFileDialog(title, filter);
#else
            Debug.LogWarning("[ShpTo3dTile] 이 플랫폼은 파일 탐색기를 지원하지 않습니다. 경로를 직접 입력하세요.");
            return null;
#endif
        }

#if UNITY_EDITOR
        private static string ExtractFirstExt(string filter)
        {
            string[] parts = filter.Split('\0');
            if (parts.Length < 2) return "";
            return parts[1].Split(';')[0].TrimStart('*', '.');
        }
#endif

#if UNITY_STANDALONE_WIN
        private const int OFN_FILEMUSTEXIST = 0x00001000;
        private const int OFN_PATHMUSTEXIST = 0x00000800;
        private const int OFN_NOCHANGEDIR   = 0x00000008;
        private const int OFN_EXPLORER      = 0x00080000;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct OPENFILENAME
        {
            public  int    lStructSize;
            public  IntPtr hwndOwner;
            public  IntPtr hInstance;
            public  string lpstrFilter;
            public  string lpstrCustomFilter;
            public  int    nMaxCustFilter;
            public  int    nFilterIndex;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 512)]
            public  string lpstrFile;
            public  int    nMaxFile;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
            public  string lpstrFileTitle;
            public  int    nMaxFileTitle;
            public  string lpstrInitialDir;
            public  string lpstrTitle;
            public  int    Flags;
            public  short  nFileOffset;
            public  short  nFileExtension;
            public  string lpstrDefExt;
            public  IntPtr lCustData;
            public  IntPtr lpfnHook;
            public  string lpTemplateName;
            public  IntPtr pvReserved;
            public  int    dwReserved;
            public  int    FlagsEx;
        }

        [DllImport("comdlg32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool GetOpenFileName(ref OPENFILENAME ofn);

        private static string WinOpenFileDialog(string title, string filter)
        {
            var ofn = new OPENFILENAME
            {
                lStructSize   = Marshal.SizeOf<OPENFILENAME>(),
                lpstrTitle    = title,
                lpstrFilter   = filter,
                nMaxFile      = 512,
                nMaxFileTitle = 256,
                Flags         = OFN_FILEMUSTEXIST | OFN_PATHMUSTEXIST | OFN_NOCHANGEDIR | OFN_EXPLORER,
            };
            return GetOpenFileName(ref ofn) ? ofn.lpstrFile.TrimEnd('\0') : null;
        }
#endif
    }
}
