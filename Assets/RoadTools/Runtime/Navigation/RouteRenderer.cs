using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.Rendering;

namespace Rugem.RoadTools
{
    /// <summary>
    /// 경로 waypoints를 도로 NavMesh + 도로 메쉬 표면에 투영하여 LineRenderer로 표시합니다.
    ///
    /// 경로 XZ 형상:
    ///   각 waypoint 구간에서 NavMesh.CalculatePath를 호출해 도로 굴곡을 추출합니다.
    ///   NavMesh 실패 시 _terrainSampleStep 간격으로 선형 보간 + NavMesh 스냅 폴백.
    ///
    /// 경로 Y(높이):
    ///   1순위: _roadLayerMask 레이어 하방 Raycast (도로 메쉬 콜라이더)
    ///   2순위: _terrainLayerMask 레이어 하방 Raycast (지형 전체)
    ///   폴백: 카메라 지면 추정 Y
    ///
    /// Inspector 설정:
    ///   _roadLayerMask → "Road" 레이어 (NavigationService._roadLayerMask와 동일)
    ///   _terrainLayerMask → 0이면 전체 레이어
    /// </summary>
    public class RouteRenderer : MonoBehaviour
    {
        [Header("경로 선 설정")]
        [SerializeField] private Color _routeStartColor = new Color(0.0f, 0.55f, 1.0f, 0.95f);
        [SerializeField] private Color _routeEndColor   = new Color(0.0f, 0.55f, 1.0f, 0.30f);
        [SerializeField] private float _lineWidth       = 4.0f;
        [Tooltip("바닥면 위 선 높이 오프셋 (미터) — Z파이팅 방지 + 가시성 확보")]
        [SerializeField] private float _groundOffset    = 0.3f;
        [Tooltip("Final route line vertices are snapped this far above the Land terrain surface to prevent z-fighting.")]
        [SerializeField] private float _terrainSnapOffset = 0.05f;

        [Header("도로 NavMesh · 레이어")]
        [Tooltip("도로 메쉬 레이어. 설정 시 일반 지형보다 우선해 도로면 Y를 획득합니다.\n" +
                 "NavigationService의 _roadLayerMask(Road 레이어)와 동일하게 설정하세요.")]
        [SerializeField] private LayerMask _roadLayerMask;
        [Tooltip("지형/도로로 인식할 레이어. 0이면 전체 레이어 사용")]
        [SerializeField] private LayerMask _terrainLayerMask = ~0;

        [Header("NavMesh · 지형 투영 설정")]
        [Tooltip("NavMesh 실패 시 선형 보간 간격 (미터). 권장: 3~8")]
        [SerializeField] private float _terrainSampleStep = 5f;
        [Tooltip("각 점을 NavMesh에 스냅할 탐색 반경 (미터)")]
        [SerializeField] private float _navMeshSnapRadius = 30f;
        [Tooltip("표면 Y 감지 레이캐스트 시작 오프셋 (미터). Kakao 웨이포인트 Y가 해수면일 수 있으므로\n" +
                 "Camera.main.y 와 waypoint.y 중 높은 값의 위에서 이 높이만큼 올려 시작합니다.\n" +
                 "지역 최고 지형보다 크게 설정하세요. 권장: 500")]
        [SerializeField] private float _raycastAltitude = 500f;
        [Tooltip("구간당 최대 보간 점 수 — 긴 직선 구간에서 성능 보호")]
        [SerializeField] private int _maxSubdivisionsPerSegment = 60;

        [Header("목적지 마커")]
        [SerializeField] private Color _markerColor  = new Color(1.0f, 0.35f, 0.0f, 1.0f);
        [SerializeField] private float _markerRadius = 4.0f;

        [Header("재질 (URP 프로젝트: Inspector에서 URP/Unlit 재질 직접 지정)")]
        [Tooltip("미설정 시 Sprites/Default 폴백 — URP에서 색상이 다를 수 있음")]
        [SerializeField] private Material _routeMaterialOverride;
        [SerializeField] private Material _markerMaterialOverride;

        // ── 내부 상태 ───────────────────────────────────────────────────────────
        private LineRenderer _line;
        private GameObject   _destinationMarker;
        private Vector3[]    _fullRoute;

        // 도로 NavMesh 영역 마스크 — "Road" area 자동 탐지, 없으면 AllAreas
        private int _roadNavMeshAreaMask;

        // ── 생명주기 ────────────────────────────────────────────────────────────

        private void Awake()
        {
            _line = GetComponent<LineRenderer>();
            if (_line == null)
                _line = gameObject.AddComponent<LineRenderer>();
            ConfigureLine();
            CreateDestinationMarker();

            int roadArea = NavMesh.GetAreaFromName("Road");
            _roadNavMeshAreaMask = roadArea >= 0 ? 1 << roadArea : NavMesh.AllAreas;

            // Inspector에 _roadLayerMask 미설정 시 RoadAssetPlacer에서 자동 상속
            if (_roadLayerMask == 0)
            {
                int roadLayer = LayerMask.NameToLayer("Road");
                if (roadLayer >= 0)
                    _roadLayerMask = 1 << roadLayer;
            }
            if (_roadLayerMask == 0)
            {
                var placer = FindAnyObjectByType<RoadAssetPlacer>();
                if (placer != null && placer.roadLayerMask != 0)
                    _roadLayerMask = placer.roadLayerMask;
            }
        }

        private void OnDestroy()
        {
            if (_destinationMarker != null)
                Destroy(_destinationMarker);
        }

        // ── 공개 API ────────────────────────────────────────────────────────────

        /// <summary>
        /// waypoints를 도로 NavMesh + 도로 메쉬 표면에 투영하여 경로를 표시합니다.
        /// </summary>
        public void ShowRoute(Vector3[] waypoints)
        {
            EnsureInitialized();
            if (waypoints == null || waypoints.Length < 2)
            {
                HideRoute();
                return;
            }

            _fullRoute = SnapRouteVerticesToTerrain(BuildRoadRoute(waypoints));
            DrawLine(_fullRoute);

            if (_destinationMarker == null)
                CreateDestinationMarker();

            Vector3 dest = _fullRoute[^1];
            _destinationMarker.transform.position = dest + Vector3.up * _markerRadius;
            _destinationMarker.SetActive(true);
        }

        /// <summary>경로 선과 마커를 숨깁니다.</summary>
        public void HideRoute()
        {
            if (_line != null)
                _line.enabled = false;
            if (_destinationMarker != null)
                _destinationMarker.SetActive(false);
            _fullRoute = null;
        }

        /// <summary>
        /// 플레이어 위치에서 하방 Raycast로 도로면 Y를 보정하고 지나친 구간을 제거합니다.
        /// </summary>
        public void TrimFromPlayerPosition(Vector3 playerWorldPos)
        {
            EnsureInitialized();
            if (_fullRoute == null || _fullRoute.Length < 2) return;

            int     nearestIdx = FindNearestRouteIndex(playerWorldPos);
            Vector3 projStart  = SampleGroundBelowCamera(playerWorldPos, _fullRoute[nearestIdx].y);

            if (nearestIdx == 0)
            {
                var trimmed = new Vector3[_fullRoute.Length];
                trimmed[0] = projStart;
                System.Array.Copy(_fullRoute, 1, trimmed, 1, _fullRoute.Length - 1);
                DrawLine(trimmed);
            }
            else
            {
                int remaining = _fullRoute.Length - nearestIdx;
                var trimmed   = new Vector3[remaining + 1];
                trimmed[0]    = projStart;
                System.Array.Copy(_fullRoute, nearestIdx, trimmed, 1, remaining);
                DrawLine(trimmed);
            }
        }

        // ── 도로 경로 생성 ──────────────────────────────────────────────────────

        /// <summary>
        /// 입력 waypoints 각 구간을 NavMesh.CalculatePath로 도로 형상을 추출한 뒤
        /// 각 코너를 도로 메쉬 표면에 Y 투영합니다.
        ///
        /// NavMesh 실패(bake 없음) 시: _terrainSampleStep 간격 보간 + NavMesh 스냅 폴백.
        /// </summary>
        private Vector3[] BuildRoadRoute(Vector3[] waypoints)
        {
            var result = new List<Vector3>(waypoints.Length * 16);
            result.Add(ProjectToRoadSurface(waypoints[0]));

            for (int i = 0; i < waypoints.Length - 1; i++)
            {
                Vector3 a = waypoints[i];
                Vector3 b = waypoints[i + 1];

                // 구간별 NavMesh 경로 → 도로 굴곡 코너 추출
                if (TryNavMeshSegmentCorners(a, b, out Vector3[] corners))
                {
                    for (int k = 1; k < corners.Length; k++)
                        result.Add(ProjectToRoadSurface(corners[k]));
                }
                else
                {
                    // NavMesh 없음 → 선형 보간 + 도로면 Y 투영
                    float segLen = new Vector2(b.x - a.x, b.z - a.z).magnitude;
                    int   steps  = Mathf.Clamp(
                        Mathf.FloorToInt(segLen / _terrainSampleStep),
                        1, _maxSubdivisionsPerSegment);
                    for (int s = 1; s <= steps; s++)
                        result.Add(ProjectToRoadSurface(Vector3.Lerp(a, b, (float)s / steps)));
                }
            }

            return result.ToArray();
        }

        /// <summary>
        /// 구간 [a→b]에 대해 도로 NavMesh 경로를 계산하고 코너 배열을 반환합니다.
        /// "Road" area 없으면 AllAreas로 폴백.
        /// </summary>
        private bool TryNavMeshSegmentCorners(Vector3 a, Vector3 b, out Vector3[] corners)
        {
            corners = null;
            int mask = _roadNavMeshAreaMask;

            if (!NavMesh.SamplePosition(a, out NavMeshHit ah, _navMeshSnapRadius, mask)) return false;
            if (!NavMesh.SamplePosition(b, out NavMeshHit bh, _navMeshSnapRadius, mask)) return false;

            var path = new NavMeshPath();
            NavMesh.CalculatePath(ah.position, bh.position, mask, path);

            if (path.status == NavMeshPathStatus.PathInvalid || path.corners == null || path.corners.Length < 2)
                return false;

            if (path.status == NavMeshPathStatus.PathPartial)
            {
                // 구간 끝이 NavMesh 외부 — 부분 경로 + 구간 끝점 직선 연결
                var partial = new Vector3[path.corners.Length + 1];
                System.Array.Copy(path.corners, partial, path.corners.Length);
                partial[path.corners.Length] = bh.position;
                corners = partial;
                return true;
            }

            corners = path.corners;
            return true;
        }

        // ── 도로 표면 투영 ──────────────────────────────────────────────────────

        /// <summary>
        /// 주어진 점을 도로 메쉬 표면에 투영합니다.
        ///   Y 1순위: _roadLayerMask 하방 Raycast (도로 콜라이더)
        ///   Y 2순위: _terrainLayerMask 하방 Raycast (지형 전체)
        ///   폴백: 카메라 지면 추정 Y
        /// </summary>
        private Vector3 ProjectToRoadSurface(Vector3 pos)
        {
            if (TrySampleTerrainDownward(pos, out Vector3 landPt))
                return landPt;

            if (_roadLayerMask != 0 && TrySampleDownward(pos, _roadLayerMask, out Vector3 roadPt))
                return roadPt;

            return FallbackAboveCamera(pos);
        }

        /// <summary>
        /// 지정 레이어 마스크로 하방 Raycast.
        /// 시작점: max(Camera.main.y, pos.y) + _raycastAltitude
        /// 사거리: _raycastAltitude × 2  → Kakao 웨이포인트 Y가 해수면(0)이어도 지형 표면을 안정적으로 감지.
        /// </summary>
        private bool TrySampleDownward(Vector3 pos, LayerMask mask, out Vector3 result)
        {
            float   raycastAltitude = Mathf.Max(_raycastAltitude, 500f);
            float   camY   = Camera.main != null ? Camera.main.transform.position.y : pos.y;
            float   baseY  = Mathf.Max(camY, pos.y);
            float   origY  = baseY + raycastAltitude;
            Vector3 origin = new Vector3(pos.x, origY, pos.z);

            if (Physics.Raycast(origin, Vector3.down, out RaycastHit hit, raycastAltitude * 2f, mask))
            {
                result = new Vector3(pos.x, hit.point.y + _groundOffset, pos.z);
                return true;
            }
            result = default;
            return false;
        }

        /// <summary>
        /// _terrainLayerMask(또는 전체)로 하방 Raycast.
        /// 실패 시 Camera.main.y + _groundOffset 폴백 (지하로 내려가지 않도록 camY 기준 사용).
        /// </summary>
        private Vector3 SampleTerrainDownward(Vector3 pos)
        {
            if (TrySampleTerrainDownward(pos, out Vector3 result))
                return result;
            return FallbackAboveCamera(pos);
        }

        private bool TrySampleTerrainDownward(Vector3 pos, out Vector3 result)
        {
            LayerMask mask = _terrainLayerMask == 0 ? ~0 : _terrainLayerMask;
            return TrySampleDownward(pos, mask, out result);
        }

        private Vector3[] SnapRouteVerticesToTerrain(Vector3[] route)
        {
            if (route == null || route.Length == 0)
                return route;

            var snapped = new Vector3[route.Length];
            for (int i = 0; i < route.Length; i++)
                snapped[i] = SnapRouteVertexToTerrain(route[i]);
            return snapped;
        }

        private Vector3 SnapRouteVertexToTerrain(Vector3 pos)
        {
            LayerMask mask = _terrainLayerMask == 0 ? ~0 : _terrainLayerMask;
            if (TrySampleTerrainSurfaceDownward(pos, mask, out Vector3 terrainPt))
                return terrainPt;

            return pos;
        }

        private bool TrySampleTerrainSurfaceDownward(Vector3 pos, LayerMask mask, out Vector3 result)
        {
            float   raycastAltitude = Mathf.Max(_raycastAltitude, 500f);
            float   camY   = Camera.main != null ? Camera.main.transform.position.y : pos.y;
            float   baseY  = Mathf.Max(camY, pos.y);
            float   origY  = baseY + raycastAltitude;
            Vector3 origin = new Vector3(pos.x, origY, pos.z);

            if (Physics.Raycast(origin, Vector3.down, out RaycastHit hit, raycastAltitude * 2f, mask))
            {
                result = new Vector3(pos.x, hit.point.y + _terrainSnapOffset, pos.z);
                return true;
            }

            result = default;
            return false;
        }

        private Vector3 FallbackAboveCamera(Vector3 pos)
        {
            float camY = Camera.main != null ? Camera.main.transform.position.y : pos.y;
            return new Vector3(pos.x, camY + _groundOffset, pos.z);
        }

        /// <summary>
        /// 카메라 위치 직하 도로면 Y를 샘플링합니다 (TrimFromPlayerPosition 시작점 보정용).
        /// </summary>
        private Vector3 SampleGroundBelowCamera(Vector3 cameraPos, float routeRefY)
        {
            LayerMask mask = _terrainLayerMask == 0 ? ~0 : _terrainLayerMask;
            if (TrySampleDownward(cameraPos, mask, out Vector3 terrainPt))
                return terrainPt;

            if (_roadLayerMask != 0 && TrySampleDownward(cameraPos, _roadLayerMask, out Vector3 roadPt))
                return roadPt;

            return new Vector3(cameraPos.x, routeRefY, cameraPos.z);
        }

        /// <summary>XZ 거리 기준으로 _fullRoute에서 가장 가까운 점의 인덱스를 반환합니다.</summary>
        private int FindNearestRouteIndex(Vector3 pos)
        {
            int   nearestIdx  = 0;
            float nearestDist = float.MaxValue;
            for (int i = 0; i < _fullRoute.Length - 1; i++)
            {
                float d = Vector3.Distance(
                    new Vector3(pos.x, 0f, pos.z),
                    new Vector3(_fullRoute[i].x, 0f, _fullRoute[i].z));
                if (d < nearestDist) { nearestDist = d; nearestIdx = i; }
            }
            return nearestIdx;
        }

        // ── 내부 구현 ────────────────────────────────────────────────────────────

        private void ConfigureLine()
        {
            if (_line == null) return;

            _line.startWidth    = _lineWidth;
            _line.endWidth      = _lineWidth;
            _line.startColor    = _routeStartColor;
            _line.endColor      = _routeEndColor;
            _line.useWorldSpace = true;
            _line.numCornerVertices = 4;
            _line.numCapVertices    = 4;
            _line.shadowCastingMode = ShadowCastingMode.Off;
            _line.receiveShadows    = false;
            _line.enabled       = false;

            if (_routeMaterialOverride != null)
            {
                _line.material = _routeMaterialOverride;
            }
            else
            {
                var shader = Shader.Find("Universal Render Pipeline/Unlit")
                          ?? Shader.Find("Unlit/Color")
                          ?? Shader.Find("Sprites/Default");
                if (shader != null)
                {
                    var mat = new Material(shader) { renderQueue = 3000 };
                    mat.color = _routeStartColor;
                    _line.material = mat;
                }
            }
        }

        private void CreateDestinationMarker()
        {
            if (_destinationMarker != null) return;

            _destinationMarker = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            _destinationMarker.name = "[NavDestinationMarker]";
            _destinationMarker.transform.localScale = Vector3.one * (_markerRadius * 2f);
            Destroy(_destinationMarker.GetComponent<Collider>());

            Material mat;
            if (_markerMaterialOverride != null)
            {
                mat = _markerMaterialOverride;
            }
            else
            {
                var shader = Shader.Find("Universal Render Pipeline/Unlit")
                          ?? Shader.Find("Unlit/Color")
                          ?? Shader.Find("Sprites/Default")
                          ?? Shader.Find("Standard");
                mat = shader != null ? new Material(shader) : new Material(Shader.Find("Standard"));
                mat.color = _markerColor;
            }
            _destinationMarker.GetComponent<MeshRenderer>().material = mat;
            _destinationMarker.SetActive(false);
        }

        private void DrawLine(Vector3[] projected)
        {
            EnsureInitialized();
            if (_line == null) return;

            if (projected == null || projected.Length < 2)
            {
                _line.enabled = false;
                return;
            }
            Vector3[] snapped = SnapRouteVerticesToTerrain(projected);
            _line.positionCount = snapped.Length;
            _line.SetPositions(snapped);
            _line.enabled = true;
        }

        private void EnsureInitialized()
        {
            if (_line == null)
            {
                _line = GetComponent<LineRenderer>();
                if (_line == null)
                    _line = gameObject.AddComponent<LineRenderer>();
                ConfigureLine();
            }
        }
    }
}
