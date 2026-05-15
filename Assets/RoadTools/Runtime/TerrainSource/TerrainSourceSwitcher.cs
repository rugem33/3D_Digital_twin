using CesiumForUnity;
using UnityEngine;

namespace Rugem.RoadTools
{
    public enum TerrainSourceMode
    {
        CesiumIon,   // 방식 A: Cesium Ion 클라우드 (ionAssetID 사용)
        CustomUrl,   // 방식 C: 자체 서버 URL (quantized-mesh)
        Ellipsoid    // 외부 서비스 없음 — 평탄 타원체
    }

    /// <summary>
    /// Cesium3DTileset의 지형 데이터 소스를 런타임에 전환합니다.
    ///
    /// 사용법:
    ///   1. 씬의 아무 GameObject에 이 컴포넌트를 추가합니다.
    ///   2. Target Tileset 에 Cesium3DTileset GameObject를 연결합니다.
    ///   3. 방식 C 테스트 시 Tools/terrain_test_server.py 를 먼저 실행합니다.
    ///      > pip install flask
    ///      > python Tools/terrain_test_server.py
    ///   4. Terrain Url 에 서버 주소를 입력하고 Play 하면 됩니다.
    /// </summary>
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
        [Tooltip("quantized-mesh 서버의 layer.json 전체 URL\n" +
                 "로컬 테스트: http://localhost:5001/layer.json\n" +
                 "실서버: https://your-server.com/terrain/layer.json")]
        [SerializeField] private string _terrainUrl = "http://localhost:5001/layer.json";

        public TerrainSourceMode CurrentMode => _mode;

        void Start()
        {
            Apply(_mode);
        }

        /// <summary>지형 소스를 지정 모드로 전환합니다.</summary>
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
                    Debug.Log($"[TerrainSwitcher] Ion 모드 (Asset ID: {_ionAssetId})");
                    break;

                case TerrainSourceMode.CustomUrl:
                    if (string.IsNullOrWhiteSpace(_terrainUrl))
                    {
                        Debug.LogError("[TerrainSwitcher] Terrain Url이 비어 있습니다.");
                        return;
                    }
                    _tileset.tilesetSource = CesiumDataSource.FromUrl;
                    _tileset.url           = _terrainUrl;
                    Debug.Log($"[TerrainSwitcher] 커스텀 URL 모드: {_terrainUrl}");
                    break;

                case TerrainSourceMode.Ellipsoid:
                    _tileset.tilesetSource = CesiumDataSource.FromEllipsoid;
                    Debug.Log("[TerrainSwitcher] Ellipsoid 모드 (외부 서비스 없음)");
                    break;
            }
        }

        /// <summary>커스텀 URL을 런타임에 교체하고 즉시 재로드합니다.</summary>
        public void SetCustomUrl(string url)
        {
            _terrainUrl = url;
            if (_mode == TerrainSourceMode.CustomUrl)
                Apply(TerrainSourceMode.CustomUrl);
        }
    }
}
