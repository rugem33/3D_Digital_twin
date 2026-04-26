using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.AI;
using Unity.AI.Navigation;
using CesiumForUnity;
using Unity.Mathematics;
using System.Collections.Generic;

namespace Rugem.RoadTools
{
    public class RoadAssetPlacer : MonoBehaviour
    {
        public GameObject assetPrefab;

        [Tooltip("배치 시 지형/도로를 감지하기 위해 레이를 쏘는 높이")]
        public float raycastHeight = 500f;

        [Tooltip("배치 후 그림자 비활성화")]
        public bool disableShadows = true;

        [Tooltip("배치 후 Static Batching 적용")]
        public bool applyStaticBatching = true;

        [Tooltip("가로수 사이의 간격 (미터)")]
        public float treeInterval = 10f;

        [Tooltip("가로수가 심길 수 있는 레이어 (반드시 Road 레이어 설정 필요)")]
        public LayerMask roadLayerMask;

        [Header("타입별 그룹 (자동 관리)")]
        [SerializeField] private List<TypeGroup> _typeGroups = new List<TypeGroup>();

        private CesiumGeoreference _georeference;

        [System.Serializable]
        private class TypeGroup
        {
            public string name;
            public GameObject parent;
        }

        // ── 타입 그룹 관리 ──────────────────────────────────────────────────────

        public GameObject GetOrCreateTypeGroup(string typeName)
        {
            var group = _typeGroups.Find(g => g.name == typeName);
            if (group != null && group.parent != null)
                return group.parent;

            var go = new GameObject($"[Type] {typeName}");
            go.transform.SetParent(this.transform);

            if (group != null)
            {
                group.parent = go;
            }
            else
            {
                _typeGroups.Add(new TypeGroup { name = typeName, parent = go });
            }
            return go;
        }

        public void SetTypeVisible(string typeName, bool visible)
        {
            var group = _typeGroups.Find(g => g.name == typeName);
            if (group != null && group.parent != null)
                group.parent.SetActive(visible);
        }

        public bool GetTypeVisible(string typeName)
        {
            var group = _typeGroups.Find(g => g.name == typeName);
            if (group != null && group.parent != null)
                return group.parent.activeSelf;
            return true;
        }

        public List<string> GetAllTypeNames()
        {
            var names = new List<string>();
            foreach (var g in _typeGroups)
                if (g.parent != null) names.Add(g.name);
            return names;
        }

        /// <summary>
        /// container 하위의 모든 오브젝트에서 MeshRenderer+MeshFilter를 별도 오브젝트로 분리합니다.
        /// </summary>
        public int DetachMeshes(Transform container, System.Action<GameObject> onCreated = null)
        {
            int count = 0;
            var renderers = new List<MeshRenderer>();
            CollectRenderers(container, renderers);

            foreach (var mr in renderers)
            {
                var mf = mr.GetComponent<MeshFilter>();
                if (mf == null || mf.sharedMesh == null) continue;

                var meshGo = new GameObject($"[Mesh] {mr.gameObject.name}");
                meshGo.transform.SetParent(container);
                meshGo.transform.position = mr.transform.position;
                meshGo.transform.rotation = mr.transform.rotation;
                meshGo.transform.localScale = mr.transform.lossyScale;

                var newMf = meshGo.AddComponent<MeshFilter>();
                newMf.sharedMesh = mf.sharedMesh;

                var newMr = meshGo.AddComponent<MeshRenderer>();
                newMr.sharedMaterials = mr.sharedMaterials;
                newMr.shadowCastingMode = mr.shadowCastingMode;

                onCreated?.Invoke(meshGo);
                count++;
            }
            return count;
        }

        private void CollectRenderers(Transform t, List<MeshRenderer> result)
        {
            var mr = t.GetComponent<MeshRenderer>();
            if (mr != null) result.Add(mr);
            foreach (Transform child in t)
                CollectRenderers(child, result);
        }

        // ── NavMesh 빌드 ────────────────────────────────────────────────────────

        public void BuildNavMesh()
        {
            var navMeshSurface = GetComponent<NavMeshSurface>();
            if (navMeshSurface == null)
                navMeshSurface = gameObject.AddComponent<NavMeshSurface>();

            navMeshSurface.collectObjects = CollectObjects.Children;
            navMeshSurface.layerMask = roadLayerMask;
            navMeshSurface.BuildNavMesh();

            UnityEngine.Debug.Log("[RoadTools] NavMesh 빌드 완료");
        }

        // ── 선(Line) 데이터 배치 ────────────────────────────────────────────────

        public void PlaceTreeLine(double startLat, double startLon, double endLat, double endLon, int unusedCount, string typeName = "가로수")
        {
            if (assetPrefab == null) return;

            if (!EnsureGeoreference()) return;

            // 1. 위경도 -> Unity World 좌표 변환
            double3 startEcef = CesiumWgs84Ellipsoid.LongitudeLatitudeHeightToEarthCenteredEarthFixed(new double3(startLon, startLat, 500.0));
            double3 endEcef = CesiumWgs84Ellipsoid.LongitudeLatitudeHeightToEarthCenteredEarthFixed(new double3(endLon, endLat, 500.0));

            // 2. ECEF -> 유니티 월드 좌표로 1차 변환
            Vector3 rawStart = (Vector3)(float3)_georeference.TransformEarthCenteredEarthFixedPositionToUnity(startEcef);
            Vector3 rawEnd = (Vector3)(float3)_georeference.TransformEarthCenteredEarthFixedPositionToUnity(endEcef);

            // 3. 변수 선언: NavMesh 위에 안착된 최종 좌표를 담을 변수
            Vector3 worldStart = Vector3.zero;
            Vector3 worldEnd = Vector3.zero;

            // 4. NavMesh 바닥 찾기 (SamplePosition)
            NavMeshHit hitStart, hitEnd;
            if (NavMesh.SamplePosition(rawStart, out hitStart, 1000f, NavMesh.AllAreas) &&
                NavMesh.SamplePosition(rawEnd, out hitEnd, 1000f, NavMesh.AllAreas))
            {
                worldStart = hitStart.position;
                worldEnd = hitEnd.position;
            }
            else
            {
                UnityEngine.Debug.LogWarning($"[RoadTools] NavMesh 바닥을 찾지 못했습니다. Bake 여부와 레이어를 확인하세요.");
                return;
            }

            // 5. 이제 보정된 worldStart, worldEnd로 경로 계산
            NavMeshPath path = new NavMeshPath();
            if (!NavMesh.CalculatePath(worldStart, worldEnd, NavMesh.AllAreas, path))
            {
                UnityEngine.Debug.LogWarning($"[RoadTools] 경로를 찾을 수 없습니다: {startLat}, {startLon} -> {endLat}, {endLon}");
                return;
            }

            if (path.corners.Length < 2) return;

            GameObject typeGroupParent = GetOrCreateTypeGroup(typeName);
            GameObject lineParent = new GameObject($"Line_{startLat:F4}_{startLon:F4}");
            lineParent.transform.SetParent(typeGroupParent.transform);

            int successCount = 0;

            // 3. 각 Corner 사이를 treeInterval 간격으로 보간하여 배치
            for (int i = 0; i < path.corners.Length - 1; i++)
            {
                Vector3 cStart = path.corners[i];
                Vector3 cEnd = path.corners[i + 1];
                float segmentDistance = Vector3.Distance(cStart, cEnd);
                Vector3 segmentDir = (cEnd - cStart).normalized;

                float currentDist = 0f;
                while (currentDist <= segmentDistance)
                {
                    Vector3 samplePoint = cStart + (segmentDir * currentDist);
                    Vector3 rayOrigin = samplePoint + Vector3.up * raycastHeight;

                    // 4. 수직 하방 레이캐스트로 정확한 지면 높이 및 법선 감지
                    if (Physics.Raycast(rayOrigin, Vector3.down, out RaycastHit hit, raycastHeight * 2f, roadLayerMask))
                    {
                        GameObject tree = Instantiate(assetPrefab, lineParent.transform);
                        tree.name = $"Tree_{successCount}";
                        tree.transform.position = hit.point;

                        // 나무는 수직을 유지하면서 도로 진행 방향을 바라봄
                        if (segmentDir != Vector3.zero)
                            tree.transform.rotation = Quaternion.LookRotation(segmentDir, Vector3.up);

                        // Cesium 요구사항 적용
                        var anchor = tree.AddComponent<CesiumGlobeAnchor>();
                        anchor.detectTransformChanges = false;

                        if (disableShadows)
                        {
                            foreach (var r in tree.GetComponentsInChildren<Renderer>())
                                r.shadowCastingMode = ShadowCastingMode.Off;
                        }
                        successCount++;
                    }

                    currentDist += treeInterval;
                    if (treeInterval <= 0) break; // 무한 루프 방지
                }
            }

            if (applyStaticBatching && successCount > 0)
            {
                StaticBatchingUtility.Combine(lineParent);
            }

            UnityEngine.Debug.Log($"[RoadTools] NavMesh 경로 기반 {successCount}개 나무 배치 완료");
        }

        // ── 점(Point) 데이터 배치 ───────────────────────────────────────────────

        /// <summary>
        /// 단일 위경도 좌표에 에셋을 하나 배치합니다.
        /// NavMesh 없이 Raycast로 지면을 감지하여 배치하므로
        /// 버스정류장·가로등 등 개별 점 데이터에 사용하세요.
        /// </summary>
        public bool PlacePointAsset(double latitude, double longitude, Transform parent = null)
        {
            if (assetPrefab == null) return false;

            if (!EnsureGeoreference()) return false;

            // 1. WGS84 → ECEF → Unity 월드 좌표
            double3 ecef = CesiumWgs84Ellipsoid.LongitudeLatitudeHeightToEarthCenteredEarthFixed(
                new double3(longitude, latitude, 500.0));
            Vector3 rawPos = (Vector3)(float3)_georeference.TransformEarthCenteredEarthFixedPositionToUnity(ecef);

            // 2. Raycast로 실제 지면 높이 감지
            Vector3 rayOrigin = new Vector3(rawPos.x, raycastHeight, rawPos.z);
            if (!Physics.Raycast(rayOrigin, Vector3.down, out RaycastHit hit, raycastHeight * 2f, roadLayerMask))
            {
                UnityEngine.Debug.LogWarning(
                    $"[RoadTools] 지면 감지 실패 - 위도: {latitude:F6}, 경도: {longitude:F6}. " +
                    "레이어 마스크 및 지형 콜라이더를 확인하세요.");
                return false;
            }

            // 3. 에셋 배치
            Transform attachTo = parent != null ? parent : this.transform;
            GameObject obj = Instantiate(assetPrefab, attachTo);
            obj.transform.position = hit.point;

            var anchor = obj.AddComponent<CesiumGlobeAnchor>();
            anchor.detectTransformChanges = false;

            if (disableShadows)
            {
                foreach (var r in obj.GetComponentsInChildren<Renderer>())
                    r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            }

            return true;
        }

        // ── 정리 ────────────────────────────────────────────────────────────────

        public void ClearAllAssets()
        {
            for (int i = transform.childCount - 1; i >= 0; i--)
            {
#if UNITY_EDITOR
                DestroyImmediate(transform.GetChild(i).gameObject);
#else
                Destroy(transform.GetChild(i).gameObject);
#endif
            }
            _typeGroups.Clear();
        }

        /// <summary>하위 호환 — 기존 코드와 이름이 연결된 경우를 위해 유지</summary>
        public void ClearAllTrees() => ClearAllAssets();

        // ── 내부 헬퍼 ───────────────────────────────────────────────────────────

        private bool EnsureGeoreference()
        {
            if (_georeference != null) return true;
            _georeference = GetComponentInParent<CesiumGeoreference>();
            if (_georeference == null) _georeference = Object.FindAnyObjectByType<CesiumGeoreference>();
            if (_georeference == null)
            {
                UnityEngine.Debug.LogError("[RoadTools] 씬에서 CesiumGeoreference를 찾을 수 없습니다.");
                return false;
            }
            return true;
        }
    }
}
