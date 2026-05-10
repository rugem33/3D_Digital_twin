using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.AI;
using Unity.AI.Navigation;
using CesiumForUnity;
using Unity.Mathematics;
using System.Collections.Generic;

namespace Rugem.RoadTools
{
    public enum NavMeshEdgeSide { Right, Left, Both, Nearest }

    /// <summary>OSM 폴리라인 기준 에셋 배치 방향 (NavMesh 불필요)</summary>
    public enum RoadSide { Center, Right, Left, Both }

    public class RoadAssetPlacer : MonoBehaviour
    {
        public GameObject assetPrefab;

        [Tooltip("에셋을 지형 위에 배치하기 위해 레이를 쏘는 시작 높이 (미터)")]
        public float raycastHeight = 500f;

        [Tooltip("배치된 에셋의 그림자 비활성화")]
        public bool disableShadows = true;

        [Tooltip("배치된 에셋에 Static Batching 적용")]
        public bool applyStaticBatching = true;

        [Tooltip("에셋 배치 간격 (미터)")]
        public float treeInterval = 10f;

        [Tooltip("에셋이 올라갈 지형 레이어 (Road 레이어 설정 필요)")]
        public LayerMask roadLayerMask;

        [Header("선 데이터 외곽 배치 (NavMesh 기반)")]
        [Tooltip("활성화 시 NavMesh 외곽에 자동 스냅하여 배치합니다.")]
        public bool useNavMeshEdge = false;

        [Tooltip("배치할 도로 외곽 방향. Right/Left=한쪽, Both=양쪽 동시, Nearest=가장 가까운 외곽")]
        public NavMeshEdgeSide edgeSide = NavMeshEdgeSide.Right;

        [Tooltip("외곽 탐색 반경 (미터). 도로 최대 폭의 절반보다 크게 설정하면 자동으로 도로 경계에 스냅됩니다. 기본 50m.")]
        public float edgeSearchRadius = 50f;

        [Header("OSM 폴리라인 측면 오프셋 (NavMesh 불필요)")]
        [Tooltip("도로 중심선에서 측면으로 이동할 거리 (미터). 0이면 중심선에 배치.")]
        public float roadSideOffset = 4f;

        [Tooltip("배치 방향. Center=중심선, Right=우측, Left=좌측, Both=양쪽")]
        public RoadSide roadSide = RoadSide.Right;

        [Header("타입별 그룹 (자동 관리)")]
        [SerializeField] private List<TypeGroup> _typeGroups = new List<TypeGroup>();

        private CesiumGeoreference _georeference;

        [System.Serializable]
        private class TypeGroup
        {
            public string name;
            public GameObject parent;
        }

        // ── 타입 그룹 관리 ────────────────────────────────────────────────────

        public GameObject GetOrCreateTypeGroup(string typeName)
        {
            var group = _typeGroups.Find(g => g.name == typeName);
            if (group != null && group.parent != null)
                return group.parent;

            var go = new GameObject($"[Type] {typeName}");
            go.transform.SetParent(this.transform);

            if (group != null) group.parent = go;
            else _typeGroups.Add(new TypeGroup { name = typeName, parent = go });
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
            return (group != null && group.parent != null) ? group.parent.activeSelf : true;
        }

        public List<string> GetAllTypeNames()
        {
            var names = new List<string>();
            foreach (var g in _typeGroups)
                if (g.parent != null) names.Add(g.name);
            return names;
        }

        // ── 메쉬 분리 ─────────────────────────────────────────────────────────

        /// <summary>container 하위 모든 오브젝트에서 MeshRenderer+MeshFilter를 별도 오브젝트로 분리합니다.</summary>
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
                meshGo.transform.position   = mr.transform.position;
                meshGo.transform.rotation   = mr.transform.rotation;
                meshGo.transform.localScale = mr.transform.lossyScale;

                meshGo.AddComponent<MeshFilter>().sharedMesh       = mf.sharedMesh;
                var newMr = meshGo.AddComponent<MeshRenderer>();
                newMr.sharedMaterials   = mr.sharedMaterials;
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
            foreach (Transform child in t) CollectRenderers(child, result);
        }

        // ── NavMesh 빌드 ──────────────────────────────────────────────────────

        public void BuildNavMesh()
        {
            var surface = GetComponent<NavMeshSurface>() ?? gameObject.AddComponent<NavMeshSurface>();
            surface.collectObjects = CollectObjects.Children;
            surface.layerMask      = roadLayerMask;
            surface.BuildNavMesh();
            UnityEngine.Debug.Log("[RoadTools] NavMesh 빌드 완료");
        }

        // ── 선(Line) 데이터 에셋 배치 ─────────────────────────────────────────

        public void PlaceTreeLine(double startLat, double startLon, double endLat, double endLon,
                                  int unusedCount, string typeName = "가로수")
        {
            if (assetPrefab == null || !EnsureGeoreference()) return;

            // 1. 위경도 → Unity 월드 좌표 (고도 500m 위에서 레이 시작)
            Vector3 rawStart = LatLonToUnity(startLon, startLat, 500.0);
            Vector3 rawEnd   = LatLonToUnity(endLon,   endLat,   500.0);
            if (!IsFinite(rawStart) || !IsFinite(rawEnd))
            {
                UnityEngine.Debug.LogWarning("[RoadTools] Georeference 변환 실패. 배치 건너뜀.");
                return;
            }

            // 2. 시작/종료점을 NavMesh 표면에 스냅
            if (!NavMesh.SamplePosition(rawStart, out NavMeshHit hitStart, 1000f, NavMesh.AllAreas) ||
                !NavMesh.SamplePosition(rawEnd,   out NavMeshHit hitEnd,   1000f, NavMesh.AllAreas))
            {
                UnityEngine.Debug.LogWarning("[RoadTools] NavMesh 스냅 실패. Bake 여부와 레이어를 확인하세요.");
                return;
            }

            // 3. NavMesh 경로 계산 (중앙선)
            var path = new NavMeshPath();
            if (!NavMesh.CalculatePath(hitStart.position, hitEnd.position, NavMesh.AllAreas, path)
                || path.corners.Length < 2)
            {
                UnityEngine.Debug.LogWarning($"[RoadTools] 경로 계산 실패: ({startLat:F5},{startLon:F5}) → ({endLat:F5},{endLon:F5})");
                return;
            }

            GameObject lineParent = new GameObject($"Line_{startLat:F4}_{startLon:F4}");
            lineParent.transform.SetParent(GetOrCreateTypeGroup(typeName).transform);

            int successCount = 0;

            // 4. 각 코너 세그먼트를 treeInterval 간격으로 샘플링
            for (int i = 0; i < path.corners.Length - 1; i++)
            {
                Vector3 cStart = path.corners[i];
                Vector3 cEnd   = path.corners[i + 1];
                float   segLen = Vector3.Distance(cStart, cEnd);
                if (!IsFinite(cStart) || !IsFinite(cEnd) || segLen <= Mathf.Epsilon) continue;

                Vector3 segDir = (cEnd - cStart).normalized;
                if (!IsFinite(segDir)) continue;

                for (float d = 0f; d <= segLen; d += treeInterval)
                {
                    Vector3 center = cStart + segDir * d;
                    Vector3 localUp = GetLocalUpAtUnityPos(center);
                    if (!IsFinite(localUp) || localUp.sqrMagnitude <= Mathf.Epsilon) continue;

                    // 외곽 배치 포인트 결정
                    var placements = ResolveEdgePlacements(center, segDir, localUp);

                    foreach (var pt in placements)
                    {
                        if (TryPlaceAsset(pt, segDir, localUp, lineParent))
                            successCount++;
                    }

                    if (treeInterval <= 0) break;
                }
            }

            if (applyStaticBatching && successCount > 0 && !ContainsCesiumGlobeAnchor(lineParent))
                StaticBatchingUtility.Combine(lineParent);

            UnityEngine.Debug.Log($"[RoadTools] {successCount}개 에셋 배치 완료");
        }

        /// <summary>
        /// useNavMeshEdge 설정과 edgeSide에 따라 실제 배치할 월드 포인트 목록을 반환합니다.
        /// Nearest  → 중앙에서 가장 가까운 NavMesh 경계 1개
        /// Right    → 진행방향 우측 외곽 1개
        /// Left     → 진행방향 좌측 외곽 1개
        /// Both     → 우측 + 좌측 외곽 2개
        /// 비활성   → 중앙선 1개 (기존 동작)
        /// </summary>
        private List<Vector3> ResolveEdgePlacements(Vector3 center, Vector3 segDir, Vector3 localUp)
        {
            var result = new List<Vector3>();

            if (!useNavMeshEdge)
            {
                result.Add(center);
                return result;
            }

            // 진행방향 기준 우측 수직벡터
            Vector3 right = Vector3.Cross(segDir, localUp).normalized;
            if (!IsFinite(right) || right.sqrMagnitude <= Mathf.Epsilon)
            {
                result.Add(center);
                return result;
            }

            switch (edgeSide)
            {
                case NavMeshEdgeSide.Right:
                    result.Add(SnapToEdge(center, right));
                    break;

                case NavMeshEdgeSide.Left:
                    result.Add(SnapToEdge(center, -right));
                    break;

                case NavMeshEdgeSide.Both:
                    result.Add(SnapToEdge(center,  right));
                    result.Add(SnapToEdge(center, -right));
                    break;

                case NavMeshEdgeSide.Nearest:
                    // 중앙에서 가장 가까운 NavMesh 경계에 바로 스냅
                    if (NavMesh.FindClosestEdge(center, out NavMeshHit nh, NavMesh.AllAreas))
                        result.Add(nh.position);
                    else
                        result.Add(center);
                    break;
            }

            return result;
        }

        /// <summary>
        /// center 에서 dir 방향으로 edgeSearchRadius 만큼 이동한 지점을 탐색 기준으로
        /// NavMesh.FindClosestEdge를 호출합니다.
        /// 탐색 기준이 NavMesh 밖에 있으면 가장 가까운 도로 경계가 반환됩니다.
        /// </summary>
        private Vector3 SnapToEdge(Vector3 center, Vector3 dir)
        {
            Vector3 searchOrigin = center + dir * edgeSearchRadius;
            if (NavMesh.FindClosestEdge(searchOrigin, out NavMeshHit hit, NavMesh.AllAreas))
                return hit.position;
            return center;
        }

        /// <summary>
        /// roadSide / roadSideOffset 설정에 따라 도로 중심선에서 실제 배치할 포인트를 반환합니다.
        /// NavMesh 없이 순수 벡터 연산만 사용합니다.
        /// </summary>
        private List<Vector3> ResolveSideOffsets(Vector3 center, Vector3 right)
        {
            var result = new List<Vector3>(2);
            float off = roadSideOffset;

            if (roadSide == RoadSide.Center || !IsFinite(right) || right.sqrMagnitude <= Mathf.Epsilon || off <= 0f)
            {
                result.Add(center);
                return result;
            }

            switch (roadSide)
            {
                case RoadSide.Right:  result.Add(center + right * off);  break;
                case RoadSide.Left:   result.Add(center - right * off);  break;
                case RoadSide.Both:
                    result.Add(center + right * off);
                    result.Add(center - right * off);
                    break;
            }
            return result;
        }

        /// <summary>주어진 월드 포인트 위에서 레이캐스트로 지형 표면을 찾아 에셋을 배치합니다.</summary>
        private bool TryPlaceAsset(Vector3 worldPoint, Vector3 segDir, Vector3 localUp, GameObject parent)
        {
            Vector3 rayOrigin = worldPoint + localUp * raycastHeight;
            if (!IsFinite(rayOrigin)) return false;

            if (!Physics.Raycast(rayOrigin, -localUp, out RaycastHit hit, raycastHeight * 2f, roadLayerMask))
                return false;
            if (!IsFinite(hit.point)) return false;

            GameObject obj = Instantiate(assetPrefab, parent.transform);
            obj.transform.position = hit.point;

            Vector3 forward = Vector3.ProjectOnPlane(segDir, localUp).normalized;
            if (!IsFinite(forward) || forward.sqrMagnitude <= Mathf.Epsilon)
                forward = Vector3.forward;
            obj.transform.rotation = Quaternion.LookRotation(forward, localUp);

            obj.AddComponent<CesiumGlobeAnchor>().detectTransformChanges = false;

            if (disableShadows)
                foreach (var r in obj.GetComponentsInChildren<Renderer>())
                    r.shadowCastingMode = ShadowCastingMode.Off;

            return true;
        }

        // ── OSM 폴리라인 에셋 배치 (NavMesh 불필요) ───────────────────────────

        /// <summary>
        /// OSM way 노드 좌표 목록을 따라 treeInterval 간격으로 에셋을 배치합니다.
        /// NavMesh 없이 Physics.Raycast만으로 지형을 탐지합니다.
        /// RoadAssetAutoLoader에서 Overpass API 결과를 받아 호출합니다.
        /// </summary>
        public int PlaceLineAlongPolyline(List<(double lat, double lon)> wayNodes, string typeName)
        {
            if (assetPrefab == null || !EnsureGeoreference() || wayNodes == null || wayNodes.Count < 2)
                return 0;

            string groupName = $"Way_{wayNodes[0].lat:F4}_{wayNodes[0].lon:F4}";
            GameObject lineParent = new GameObject(groupName);
            lineParent.transform.SetParent(GetOrCreateTypeGroup(typeName).transform);

            int successCount = 0;
            float distToNext = 0f;

            for (int i = 0; i < wayNodes.Count - 1; i++)
            {
                Vector3 segStart = LatLonToUnity(wayNodes[i].lon,     wayNodes[i].lat,     500.0);
                Vector3 segEnd   = LatLonToUnity(wayNodes[i + 1].lon, wayNodes[i + 1].lat, 500.0);
                if (!IsFinite(segStart) || !IsFinite(segEnd)) { distToNext = 0f; continue; }

                Vector3 segVec = segEnd - segStart;
                float   segLen = segVec.magnitude;
                if (segLen <= Mathf.Epsilon) continue;

                Vector3 segDir  = segVec / segLen;
                Vector3 localUp = GetLocalUpAtUnityPos(segStart);

                // 진행 방향 기준 우측 수직벡터 (도로 측면 오프셋용)
                Vector3 right = Vector3.Cross(segDir, localUp).normalized;

                for (float d = distToNext; d <= segLen; d += treeInterval)
                {
                    Vector3 center = segStart + segDir * d;
                    foreach (var pt in ResolveSideOffsets(center, right))
                    {
                        if (TryPlaceAsset(pt, segDir, localUp, lineParent))
                            successCount++;
                    }
                    if (treeInterval <= 0f) break;
                }

                float lastD = distToNext + Mathf.Floor((segLen - distToNext) / Mathf.Max(treeInterval, 0.01f)) * treeInterval;
                distToNext = treeInterval - (segLen - lastD);
                if (distToNext < 0f) distToNext = 0f;
            }

            if (applyStaticBatching && successCount > 0 && !ContainsCesiumGlobeAnchor(lineParent))
                StaticBatchingUtility.Combine(lineParent);

            UnityEngine.Debug.Log($"[RoadTools] PlaceLineAlongPolyline: {successCount}개 배치");
            return successCount;
        }

        // ── 점(Point) 데이터 에셋 배치 ───────────────────────────────────────

        /// <summary>
        /// 단일 위경도 좌표에 에셋 하나를 배치합니다.
        /// NavMesh 없이 Raycast로 지형을 탐지하여 배치하므로
        /// 버스정류장, 표지판 등 단발성 데이터에 사용하세요.
        /// </summary>
        public bool PlacePointAsset(double latitude, double longitude, Transform parent = null)
        {
            if (assetPrefab == null || !EnsureGeoreference()) return false;

            Vector3 rayOrigin = LatLonToUnity(longitude, latitude, raycastHeight);
            if (!IsFinite(rayOrigin)) return false;

            Vector3 localUp = GetLocalUpAtUnityPos(rayOrigin);
            if (!IsFinite(localUp) || localUp.sqrMagnitude <= Mathf.Epsilon) return false;

            if (!Physics.Raycast(rayOrigin, -localUp, out RaycastHit hit, raycastHeight * 2f, roadLayerMask))
            {
                UnityEngine.Debug.LogWarning(
                    $"[RoadTools] 지형 탐지 실패 - 위도: {latitude:F6}, 경도: {longitude:F6}. 레이어 마스크와 콜라이더를 확인하세요.");
                return false;
            }

            Transform attachTo = parent != null ? parent : this.transform;
            GameObject obj = Instantiate(assetPrefab, attachTo);
            if (!IsFinite(hit.point))
            {
#if UNITY_EDITOR
                DestroyImmediate(obj);
#else
                Destroy(obj);
#endif
                return false;
            }

            obj.transform.position = hit.point;
            obj.transform.rotation = GetRotationFacingNearestRoad(hit.point, localUp);
            obj.AddComponent<CesiumGlobeAnchor>().detectTransformChanges = false;

            if (disableShadows)
                foreach (var r in obj.GetComponentsInChildren<Renderer>())
                    r.shadowCastingMode = ShadowCastingMode.Off;

            return true;
        }

        // ── 전체 삭제 ─────────────────────────────────────────────────────────

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

        public void ClearAllTrees() => ClearAllAssets();

        // ── 점 데이터 도로 방향 정렬 ──────────────────────────────────────────

        /// <summary>
        /// 배치 위치에서 가장 가까운 NavMesh 표면 방향을 forward로 삼아 회전을 반환합니다.
        /// NavMesh가 없거나 거리가 너무 짧으면 localUp 정렬만 적용합니다.
        /// </summary>
        private Quaternion GetRotationFacingNearestRoad(Vector3 position, Vector3 localUp)
        {
            if (NavMesh.SamplePosition(position, out NavMeshHit roadHit, 200f, NavMesh.AllAreas))
            {
                Vector3 toRoad = Vector3.ProjectOnPlane(roadHit.position - position, localUp);
                if (IsFinite(toRoad) && toRoad.sqrMagnitude > 0.25f)
                    return Quaternion.LookRotation(toRoad.normalized, localUp);

                // 도로 위에 있는 경우: 가장 가까운 도로 경계의 내측 방향을 forward로 사용
                if (NavMesh.FindClosestEdge(position, out NavMeshHit edgeHit, NavMesh.AllAreas))
                {
                    Vector3 inward = Vector3.ProjectOnPlane(-edgeHit.normal, localUp).normalized;
                    if (IsFinite(inward) && inward.sqrMagnitude > Mathf.Epsilon)
                        return Quaternion.LookRotation(inward, localUp);
                }
            }
            return Quaternion.FromToRotation(Vector3.up, localUp);
        }

        // ── 내부 유틸리티 ─────────────────────────────────────────────────────

        private Vector3 LatLonToUnity(double lon, double lat, double altitudeM)
        {
            double3 ecef = CesiumWgs84Ellipsoid.LongitudeLatitudeHeightToEarthCenteredEarthFixed(
                new double3(lon, lat, altitudeM));
            return (Vector3)(float3)_georeference.TransformEarthCenteredEarthFixedPositionToUnity(ecef);
        }

        /// <summary>
        /// Unity 월드 좌표에서 Cesium 지구 구형에 맞는 로컬 "up" 방향을 반환합니다.
        /// ECEF 좌표 단위벡터(지구 중심 → 해당 지점)를 Unity 방향으로 변환합니다.
        /// </summary>
        private Vector3 GetLocalUpAtUnityPos(Vector3 unityPos)
        {
            if (!EnsureGeoreference()) return Vector3.up;
            double3 ecef    = _georeference.TransformUnityPositionToEarthCenteredEarthFixed(
                                  new double3(unityPos.x, unityPos.y, unityPos.z));
            double3 upDir   = _georeference.TransformEarthCenteredEarthFixedDirectionToUnity(math.normalize(ecef));
            return ((Vector3)(float3)upDir).normalized;
        }

        private static bool ContainsCesiumGlobeAnchor(GameObject root) =>
            root != null && root.GetComponentInChildren<CesiumGlobeAnchor>(true) != null;

        private static bool IsFinite(Vector3 v) => IsFinite(v.x) && IsFinite(v.y) && IsFinite(v.z);
        private static bool IsFinite(float v)   => !float.IsNaN(v) && !float.IsInfinity(v);

        private bool EnsureGeoreference()
        {
            if (_georeference != null) return true;
            _georeference = GetComponentInParent<CesiumGeoreference>()
                            ?? Object.FindAnyObjectByType<CesiumGeoreference>();
            if (_georeference == null)
            {
                UnityEngine.Debug.LogError("[RoadTools] 씬에서 CesiumGeoreference를 찾을 수 없습니다.");
                return false;
            }
            return true;
        }
    }
}
