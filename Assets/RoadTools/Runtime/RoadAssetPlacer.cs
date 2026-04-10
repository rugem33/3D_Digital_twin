using UnityEngine;
using CesiumForUnity;
using Unity.Mathematics;

namespace Rugem.RoadTools
{
    public class RoadAssetPlacer : MonoBehaviour
    {
        public GameObject assetPrefab; // LOD가 있는 나무 프리팹

        public void PlaceTreeLine(double startLat, double startLon, double endLat, double endLon, int count)
        {
            if (assetPrefab == null) return;

            // 노선별로 관리할 부모 오브젝트 (합치지는 않지만 정리용)
            GameObject lineParent = new GameObject($"Line_{startLat}_{startLon}");
            lineParent.transform.SetParent(this.transform);

            for (int i = 0; i < count; i++)
            {
                float t = (count > 1) ? (float)i / (count - 1) : 0.5f;
                double currLat = startLat + (endLat - startLat) * t;
                double currLon = startLon + (endLon - startLon) * t;

                // 1. 나무 생성
                GameObject tree = Instantiate(assetPrefab, lineParent.transform);
                tree.name = $"Tree_{i}";

                // 2. Cesium 앵커 설정
                var anchor = tree.AddComponent<CesiumGlobeAnchor>();
                anchor.longitudeLatitudeHeight = new double3(currLon, currLat, 210);

                // 3. [최적화 핵심] 연산 차단
                // 배치가 끝난 정적 물체는 위치 변화를 감지할 필요가 없습니다.
                // 이 설정이 꺼져야 600개의 CPU 연산이 멈춥니다.
                anchor.detectTransformChanges = false;
            }

            Debug.Log($"{count}개의 나무 배치가 완료되었습니다. (연산 차단 적용)");
        }
    }
}