using UnityEngine;
using CesiumForUnity;

public class MapManager : MonoBehaviour
{
    [Header("여기에 찰흙(Cesium World Terrain)을 끌어다 놓으세요")]
    public GameObject cesiumTerrain;

    // 스티커 두 개를 기억해둘 공간
    private CesiumWebMapTileServiceRasterOverlay satelliteOverlay;
    private CesiumWebMapTileServiceRasterOverlay baseOverlay;

    void Start()
    {
        // 1. 찰흙에 붙어있는 스티커들을 전부 찾아옵니다.
        CesiumWebMapTileServiceRasterOverlay[] overlays = cesiumTerrain.GetComponents<CesiumWebMapTileServiceRasterOverlay>();

        foreach (var overlay in overlays)
        {
            if (overlay.layer == "Satellite")
                satelliteOverlay = overlay;
            else if (overlay.layer == "Base")
                baseOverlay = overlay;
        }

        // 2. 처음 시작할 때는 위성지도가 보이게 세팅!
        ShowSatelliteMap();
    }

    // 🔴 [위성 지도] 버튼용
    public void ShowSatelliteMap()
    {
        if (satelliteOverlay != null) satelliteOverlay.enabled = true;
        if (baseOverlay != null) baseOverlay.enabled = false;
    }

    // 🔵 [일반 지도] 버튼용
    public void ShowBaseMap()
    {
        if (satelliteOverlay != null) satelliteOverlay.enabled = false;
        if (baseOverlay != null) baseOverlay.enabled = true;
    }

    // 🟡 (보너스) 지형 높낮이만 보고 싶을 때 스티커를 다 끄는 기능
    public void ShowTerrainOnly()
    {
        if (satelliteOverlay != null) satelliteOverlay.enabled = false;
        if (baseOverlay != null) baseOverlay.enabled = false;
    }
}