using System.Collections;
using CesiumForUnity;
using UnityEngine;
using UnityEngine.Networking;

namespace Rugem.RoadTools
{
    public enum TerrainSourceMode
    {
        CesiumIon,   // 방식 A: Cesium Ion 클라우드 (ionAssetID 사용)
        CustomUrl,   // 방식 C: 자체 서버 URL (quantized-mesh)
        Ellipsoid    // 외부 서비스 없음 — 평탄 타원체
    }

    [AddComponentMenu("RoadTools/Terrain Source Switcher")]
    public class TerrainSourceSwitcher : MonoBehaviour
    {
        [Header("Cesium3DTileset 연결")]
        [SerializeField] private Cesium3DTileset _tileset;

        [Header("시작 시 적용할 모드")]
        [SerializeField] private TerrainSourceMode _mode = TerrainSourceMode.CesiumIon;

        [Header("방식 A — Cesium Ion")]
        [Tooltip("Cesium Ion Asset ID. 기본 World Terrain = 1")]
        [SerializeField] private long _ionAssetId = 1;

        [Header("방식 C — 커스텀 URL")]
        [Tooltip("quantized-mesh 서버의 layer.json URL\n예: http://localhost:5001/layer.json")]
        [SerializeField] private string _terrainUrl = "http://localhost:5001/layer.json";

        [Header("TIF → 지형 변환 서버")]
        [Tooltip("TIF 파일을 변환하는 지형 서버 URL\n예: http://localhost:5001")]
        [SerializeField] private string _terrainConvertServerUrl = "http://localhost:5001";

        [HideInInspector]
        [SerializeField] private string _demTifPath = "";

        public TerrainSourceMode CurrentMode          => _mode;
        public string             TerrainConvertServerUrl => _terrainConvertServerUrl;
        public string             DemTifPath          => _demTifPath;

        void Start()
        {
            Apply(_mode);
        }

        public void Apply(TerrainSourceMode mode)
        {
            if (_tileset == null)
            {
                Debug.LogError("[TerrainSwitcher] Target Tileset이 연결되지 않았습니다.");
                return;
            }

            _mode = mode;

            switch (mode)
            {
                case TerrainSourceMode.CesiumIon:
                    _tileset.tilesetSource = CesiumDataSource.FromCesiumIon;
                    _tileset.ionAssetID    = _ionAssetId;
                    Debug.Log($"[TerrainSwitcher] Ion 모드 적용 — Asset ID: {_ionAssetId}");
                    break;

                case TerrainSourceMode.CustomUrl:
                    if (string.IsNullOrWhiteSpace(_terrainUrl))
                    {
                        Debug.LogError("[TerrainSwitcher] Terrain URL이 비어 있습니다.");
                        return;
                    }
                    _tileset.tilesetSource = CesiumDataSource.FromUrl;
                    _tileset.url           = _terrainUrl;
                    Debug.Log($"[TerrainSwitcher] 커스텀 URL 적용 — {_terrainUrl}");
                    StartCoroutine(ValidateUrlCoroutine(_terrainUrl));
                    break;

                case TerrainSourceMode.Ellipsoid:
                    _tileset.tilesetSource = CesiumDataSource.FromEllipsoid;
                    Debug.Log("[TerrainSwitcher] Ellipsoid 모드 적용");
                    break;
            }
        }

        public void SetCustomUrl(string url)
        {
            _terrainUrl = url;
            if (_mode == TerrainSourceMode.CustomUrl)
                Apply(TerrainSourceMode.CustomUrl);
        }

        // ── URL 접근 및 layer.json + 타일 파일 검증 ──────────────────
        public IEnumerator ValidateUrlCoroutine(string url)
        {
            Debug.Log($"[TerrainSwitcher] layer.json 접근 시도: {url}");

            using var req = UnityWebRequest.Get(url);
            req.timeout = 10;
            yield return req.SendWebRequest();

            if (req.result != UnityWebRequest.Result.Success)
            {
                Debug.LogError(
                    $"[TerrainSwitcher] ✗ URL 접근 실패\n" +
                    $"  URL    : {url}\n" +
                    $"  오류   : {req.error}\n" +
                    $"  HTTP   : {req.responseCode}\n" +
                    $"  확인   : 서버 실행 여부 / 방화벽 / URL 오탈자");
                yield break;
            }

            string body = req.downloadHandler.text;
            Debug.Log($"[TerrainSwitcher] ✓ layer.json 수신 성공 ({body.Length} bytes)\n{body}");

            // 필수 필드 검증
            if (!body.Contains("\"tilejson\"") && !body.Contains("\"format\""))
                Debug.LogWarning("[TerrainSwitcher] layer.json 경고: 'tilejson' 또는 'format' 필드 없음");

            if (!body.Contains("quantized-mesh"))
            {
                Debug.LogWarning(
                    "[TerrainSwitcher] layer.json 형식 경고\n" +
                    "  'quantized-mesh' 포맷이 아닙니다.\n" +
                    "  Cesium이 이 지형을 로드하지 못할 수 있습니다.");
                yield break;
            }

            Debug.Log("[TerrainSwitcher] ✓ quantized-mesh 포맷 확인됨");

            // available 배열 존재 여부 확인
            if (!body.Contains("\"available\""))
                Debug.LogWarning(
                    "[TerrainSwitcher] layer.json 경고: 'available' 배열이 없습니다.\n" +
                    "  Cesium은 available 배열로 어떤 줌 레벨/타일이 존재하는지 판단합니다.\n" +
                    "  ctb-tile 변환 시 --no-overwrite 없이 재실행하거나 서버 설정을 확인하세요.");
            else
                Debug.Log("[TerrainSwitcher] ✓ available 배열 존재");

            // ── 실제 타일 파일 0/0/0.terrain 접근 테스트 ────────────
            string baseUrl = url.Contains("/layer.json")
                ? url.Substring(0, url.LastIndexOf("/layer.json"))
                : url.TrimEnd('/');
            string tileUrl  = $"{baseUrl}/0/0/0.terrain";
            Debug.Log($"[TerrainSwitcher] 루트 타일 접근 테스트: {tileUrl}");

            using var tileReq = UnityWebRequest.Get(tileUrl);
            tileReq.timeout = 10;
            yield return tileReq.SendWebRequest();

            if (tileReq.result != UnityWebRequest.Result.Success)
            {
                Debug.LogError(
                    $"[TerrainSwitcher] ✗ 루트 타일 접근 실패 — HTTP {tileReq.responseCode}\n" +
                    $"  URL  : {tileUrl}\n" +
                    $"  오류 : {tileReq.error}\n" +
                    $"  ▶ 서버가 .terrain 파일을 서빙하고 있지 않습니다.\n" +
                    $"  ▶ ctb-tile 출력 폴더가 서버 루트와 일치하는지 확인하세요.\n" +
                    $"  ▶ 예: Flask 서버의 TILES_DIR이 ctb-tile 출력 폴더를 가리켜야 합니다.");
            }
            else
            {
                int bytes = tileReq.downloadHandler.data?.Length ?? 0;
                if (bytes < 100)
                    Debug.LogWarning(
                        $"[TerrainSwitcher] ⚠ 루트 타일 응답이 너무 작습니다 ({bytes} bytes)\n" +
                        $"  정상 terrain 파일은 수 KB 이상입니다. 빈 파일이거나 변환 오류일 수 있습니다.");
                else
                    Debug.Log($"[TerrainSwitcher] ✓ 루트 타일(0/0/0.terrain) 정상 수신 — {bytes} bytes");
            }
        }
    }
}
