using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;

namespace Rugem.RoadTools
{
    /// <summary>
    /// 길찾기 핵심 로직 — 목적지 설정, NavMesh 경로 계산, 도착 감지
    /// </summary>
    public class NavigationService : MonoBehaviour
    {
        [Header("의존성")]
        [SerializeField] private PositionProvider _positionProvider;

        [Header("경로 설정")]
        [Tooltip("도착 판정 반경 (미터 — 수평 거리 기준)")]
        [SerializeField] private float _arrivalRadius = 15f;
        [Tooltip("NavMesh 스냅 최대 탐색 반경 (미터)")]
        [SerializeField] private float _navMeshSampleRadius = 1000f;
        [Tooltip("경로 재계산 주기 (초, 0 = 수동만)")]
        [SerializeField] private float _routeRefreshInterval = 10f;
        [Tooltip("NavMesh 실패 시 도로 메쉬 레이어를 직접 샘플링해 경로를 찾습니다.")]
        [SerializeField] private LayerMask _roadLayerMask;
        [Tooltip("도로 메쉬 A* 샘플 간격 (미터). 작을수록 정밀하지만 느립니다.")]
        [SerializeField] private float _roadGridStep = 8f;
        [Tooltip("도로 메쉬 탐색 경계 여백 (미터)")]
        [SerializeField] private float _roadSearchPadding = 40f;
        [Tooltip("도로 메쉬 샘플 레이캐스트 높이 (미터)")]
        [SerializeField] private float _roadRaycastHeight = 500f;
        [Tooltip("도로 메쉬 경로 탐색 최대 셀 수")]
        [SerializeField] private int _maxRoadGridCells = 30000;

        [Header("카카오 도로 경로 (선택)")]
        [Tooltip("비워 두면 Resources/kakao_api_key.txt 에서 자동 로드됩니다.")]
        [SerializeField] private string _kakaoRestApiKey = "";
        [Tooltip("씬에 있는 KakaoDirectionsService 컴포넌트를 직접 연결합니다. (선택)")]
        [SerializeField] private KakaoDirectionsService _directionsService;

        // ── 이벤트 ─────────────────────────────────────────────────────────────
        public event System.Action<POIData, Vector3[]> OnRouteCalculated;
        public event System.Action<POIData>            OnDestinationSet;
        public event System.Action                     OnNavigationCleared;
        public event System.Action                     OnArrived;

        // ── 공개 상태 ───────────────────────────────────────────────────────────
        public POIData   CurrentDestination     { get; private set; }
        public bool      IsNavigating           { get; private set; }
        public float     DistanceToDestination  { get; private set; } = -1f;
        public Vector3[] CurrentRoute           { get; private set; }
        /// <summary>SetDestination 호출 시 설정된 목적지 Unity 월드 좌표 (오버뷰 마커 표시용)</summary>
        public Vector3   DestinationWorldPos    { get; private set; }

        // ── 내부 상태 ───────────────────────────────────────────────────────────
        private Vector3 _destinationWorldPos;
        private float   _refreshTimer;
        private int     _routeRequestId;
        private float EffectiveNavMeshSampleRadius => Mathf.Max(_navMeshSampleRadius, 1000f);

        private static readonly Vector2Int[] RoadGridDirs =
        {
            new Vector2Int(1, 0), new Vector2Int(-1, 0), new Vector2Int(0, 1), new Vector2Int(0, -1),
            new Vector2Int(1, 1), new Vector2Int(1, -1), new Vector2Int(-1, 1), new Vector2Int(-1, -1)
        };

        // ── 생명주기 ────────────────────────────────────────────────────────────

        private void Awake() => ResolveDependencies();

        private void ResolveDependencies()
        {
            if (_positionProvider == null)
                _positionProvider = FindAnyObjectByType<PositionProvider>();

            _kakaoRestApiKey = KakaoApiKeyProvider.Resolve(_kakaoRestApiKey);

            System.Func<double, double, Vector3> converter = _positionProvider != null
                ? (lat, lon) => _positionProvider.ConvertToUnityPosition(lat, lon)
                : null;

            if (!string.IsNullOrWhiteSpace(_kakaoRestApiKey))
            {
                if (_directionsService == null)
                    _directionsService = gameObject.GetComponent<KakaoDirectionsService>()
                                      ?? gameObject.AddComponent<KakaoDirectionsService>();
                _directionsService.Initialize(_kakaoRestApiKey, converter);
            }
            else if (_directionsService == null)
            {
                _directionsService = FindAnyObjectByType<KakaoDirectionsService>();
                _directionsService?.Initialize("", converter);
            }

            if (_roadLayerMask == 0)
            {
                int roadLayer = LayerMask.NameToLayer("Road");
                if (roadLayer >= 0)
                    _roadLayerMask = 1 << roadLayer;
            }
        }

        private void Update()
        {
            if (!IsNavigating) return;

            UpdateDistance();
            CheckArrival();

            if (_routeRefreshInterval > 0f)
            {
                _refreshTimer += Time.deltaTime;
                if (_refreshTimer >= _routeRefreshInterval)
                {
                    _refreshTimer = 0f;
                    CalculateRoute();
                }
            }
        }

        // ── 공개 API ────────────────────────────────────────────────────────────

        /// <summary>목적지를 설정하고 경로 계산을 시작합니다.</summary>
        public void SetDestination(POIData poi)
        {
            _routeRequestId++;

            if (poi == null)
            {
                Debug.LogWarning("[NavService] 목적지가 비어 있어 경로를 설정하지 않았습니다.");
                return;
            }

            ResolveDependencies();
            if (_positionProvider == null || !_positionProvider.IsReady)
            {
                Debug.LogError("[NavService] PositionProvider가 연결되지 않았습니다.");
                return;
            }

            CurrentDestination    = poi;
            IsNavigating          = true;
            DistanceToDestination = -1f;
            _refreshTimer         = 0f;

            _destinationWorldPos = _positionProvider.ConvertToUnityPosition(poi.latitude, poi.longitude);
            DestinationWorldPos  = _destinationWorldPos;
            CalculateRoute();
            OnDestinationSet?.Invoke(poi);
            Debug.Log($"[NavService] 목적지 설정: {poi.name} ({poi.latitude:F6}, {poi.longitude:F6})");
        }

        /// <summary>플레이어 카메라를 현재 목적지 위치로 즉시 이동합니다.</summary>
        public void MoveToDestination()
        {
            if (CurrentDestination == null) return;

            ResolveDependencies();
            _positionProvider?.TeleportTo(CurrentDestination.latitude, CurrentDestination.longitude);
        }

        /// <summary>경로 안내를 종료하고 상태를 초기화합니다.</summary>
        public void ClearNavigation()
        {
            _routeRequestId++;

            CurrentDestination    = null;
            IsNavigating          = false;
            DistanceToDestination = -1f;
            CurrentRoute          = null;
            _refreshTimer         = 0f;
            OnNavigationCleared?.Invoke();
            Debug.Log("[NavService] 길찾기 취소");
        }

        // ── 내부 구현 ───────────────────────────────────────────────────────────

        private void UpdateDistance()
        {
            if (_positionProvider == null) return;

            Vector3 playerXZ = new Vector3(_positionProvider.PlayerPosition.x, 0f, _positionProvider.PlayerPosition.z);
            Vector3 destXZ   = new Vector3(_destinationWorldPos.x, 0f, _destinationWorldPos.z);
            DistanceToDestination = Vector3.Distance(playerXZ, destXZ);
        }

        private void CheckArrival()
        {
            if (DistanceToDestination >= 0f && DistanceToDestination < _arrivalRadius)
            {
                IsNavigating = false;
                OnArrived?.Invoke();
                Debug.Log($"[NavService] 도착: {CurrentDestination?.name}");
            }
        }

        private void CalculateRoute()
        {
            ResolveDependencies();
            if (_positionProvider == null) return;

            // 우선순위: 카카오 Directions API → 도로 메쉬 A* → NavMesh → 직선 폴백
            int requestId = _routeRequestId;
            POIData destinationSnapshot = CurrentDestination;

            if (_directionsService != null)
            {
                _directionsService.RequestRoute(
                    _positionProvider.CurrentLatitude,  _positionProvider.CurrentLongitude,
                    destinationSnapshot.latitude,        destinationSnapshot.longitude,
                    (waypoints, error) =>
                    {
                        if (requestId != _routeRequestId || destinationSnapshot != CurrentDestination)
                            return;

                        if (error == null && waypoints != null && waypoints.Length >= 2)
                        {
                            // 현재 위치 → 카카오 경로 시작점을 직선으로 보간
                            var fullRoute = new Vector3[waypoints.Length + 1];
                            fullRoute[0] = _positionProvider.NavPosition;
                            System.Array.Copy(waypoints, 0, fullRoute, 1, waypoints.Length);
                            CurrentRoute = fullRoute;
                            OnRouteCalculated?.Invoke(destinationSnapshot, CurrentRoute);
                        }
                        else
                        {
                            Debug.LogWarning($"[NavService] Directions API 실패 ({error}) — NavMesh/도로 메쉬 폴백");
                            CalculateNavMeshRoute();
                        }
                    });
            }
            else
            {
                CalculateNavMeshRoute();
            }
        }

        private void CalculateNavMeshRoute()
        {
            if (_positionProvider == null) return;

            Vector3 startPos = _positionProvider.NavPosition;

            if (TryCalculateRoadMeshRoute(startPos, _destinationWorldPos, out Vector3[] roadRoute))
            {
                CurrentRoute = roadRoute;
                OnRouteCalculated?.Invoke(CurrentDestination, CurrentRoute);
                return;
            }

            if (TryCalculateNavMeshRoute(startPos, _destinationWorldPos, out Vector3[] navMeshRoute))
            {
                CurrentRoute = navMeshRoute;
                OnRouteCalculated?.Invoke(CurrentDestination, CurrentRoute);
                return;
            }

            CurrentRoute = new[] { startPos, _destinationWorldPos };
            OnRouteCalculated?.Invoke(CurrentDestination, CurrentRoute);
            Debug.LogWarning("[NavService] NavMesh와 도로 메쉬에서 유효한 경로를 찾지 못해 직선 경로를 표시합니다.");
        }

        private bool TryCalculateNavMeshRoute(Vector3 startPos, Vector3 destPos, out Vector3[] route)
        {
            route = null;

            // "Road" area 우선, 없으면 전체 영역 사용
            int roadArea = NavMesh.GetAreaFromName("Road");
            int areaMask = roadArea >= 0 ? 1 << roadArea : NavMesh.AllAreas;

            float sampleRadius = EffectiveNavMeshSampleRadius;
            bool hasStart = NavMesh.SamplePosition(startPos, out NavMeshHit startHit, sampleRadius, areaMask);
            bool hasDest  = NavMesh.SamplePosition(destPos,  out NavMeshHit destHit,  sampleRadius, areaMask);

            // Road area 스냅 실패 시 AllAreas로 재시도
            if (!hasStart) hasStart = NavMesh.SamplePosition(startPos, out startHit, sampleRadius, NavMesh.AllAreas);
            if (!hasDest)  hasDest  = NavMesh.SamplePosition(destPos,  out destHit,  sampleRadius, NavMesh.AllAreas);

            if (!hasStart || !hasDest)
            {
                Debug.LogWarning($"[NavService] NavMesh 스냅 실패 start={hasStart}, dest={hasDest}");
                return false;
            }

            var path = new NavMeshPath();
            NavMesh.CalculatePath(startHit.position, destHit.position, areaMask, path);

            // Road area 경로 실패 시 AllAreas로 재시도
            if (path.status == NavMeshPathStatus.PathInvalid || path.corners == null || path.corners.Length < 2)
                NavMesh.CalculatePath(startHit.position, destHit.position, NavMesh.AllAreas, path);

            if (path.status == NavMeshPathStatus.PathInvalid || path.corners == null || path.corners.Length < 2)
            {
                Debug.LogWarning($"[NavService] NavMesh 경로 실패 status={path.status}");
                return false;
            }

            if (path.status == NavMeshPathStatus.PathPartial)
            {
                // 목적지가 NavMesh 외부 — 부분 경로(도로 굴곡 포함) + 목적지 직선 연결
                var partial = new Vector3[path.corners.Length + 1];
                System.Array.Copy(path.corners, partial, path.corners.Length);
                partial[path.corners.Length] = destPos;
                route = partial;
                Debug.LogWarning($"[NavService] NavMesh 부분 경로 사용: {route.Length} corners (PathPartial) — 목적지가 NavMesh 외부입니다.");
                return true;
            }

            route = path.corners;
            Debug.Log($"[NavService] NavMesh 경로 계산 완료: {route.Length} corners");
            return true;
        }

        private bool TryCalculateRoadMeshRoute(Vector3 startPos, Vector3 destPos, out Vector3[] route)
        {
            route = null;
            LayerMask roadMask = _roadLayerMask;
            if (roadMask == 0)
                return false;

            if (!TrySnapToRoadMesh(startPos, out Vector3 roadStart) ||
                !TrySnapToRoadMesh(destPos, out Vector3 roadDest))
            {
                Debug.LogWarning("[NavService] 도로 메쉬 스냅 실패 — Road 레이어/콜라이더를 확인하세요.");
                return false;
            }

            float step = Mathf.Max(2f, _roadGridStep);
            float minX = Mathf.Min(roadStart.x, roadDest.x) - _roadSearchPadding;
            float maxX = Mathf.Max(roadStart.x, roadDest.x) + _roadSearchPadding;
            float minZ = Mathf.Min(roadStart.z, roadDest.z) - _roadSearchPadding;
            float maxZ = Mathf.Max(roadStart.z, roadDest.z) + _roadSearchPadding;

            int width = Mathf.CeilToInt((maxX - minX) / step) + 1;
            int height = Mathf.CeilToInt((maxZ - minZ) / step) + 1;
            if (width * height > _maxRoadGridCells)
            {
                float area = Mathf.Max(1f, (maxX - minX) * (maxZ - minZ));
                step = Mathf.Sqrt(area / Mathf.Max(1, _maxRoadGridCells)) * 1.15f;
                step = Mathf.Max(step, _roadGridStep);
                width = Mathf.CeilToInt((maxX - minX) / step) + 1;
                height = Mathf.CeilToInt((maxZ - minZ) / step) + 1;
                Debug.LogWarning($"[NavService] 도로 메쉬 탐색 영역이 커서 샘플 간격을 {step:F1}m로 자동 조정합니다.");
            }

            if (width <= 1 || height <= 1 || width * height > _maxRoadGridCells)
            {
                Debug.LogWarning($"[NavService] 도로 메쉬 탐색 영역 과대: {width}x{height}. step/padding을 조정하세요.");
                return false;
            }

            var walkable = new bool[width, height];
            var points = new Vector3[width, height];
            int walkableCount = 0;
            for (int x = 0; x < width; x++)
            for (int z = 0; z < height; z++)
            {
                Vector3 p = new Vector3(minX + x * step, startPos.y, minZ + z * step);
                if (RaycastRoadAtXZ(p, out Vector3 hit))
                {
                    walkable[x, z] = true;
                    points[x, z] = hit;
                    walkableCount++;
                }
            }

            // 도로 메쉬 불연속 구간 보완: 1셀 팽창으로 인접 walkable 셀 사이 공백 연결
            // (도로 세그먼트 간 작은 틈을 메워 A* 연결성 확보)
            BridgeWalkableGaps(walkable, points, minX, minZ, step);

            Vector2Int startCell = FindNearestWalkableCell(roadStart, minX, minZ, step, walkable);
            Vector2Int destCell = FindNearestWalkableCell(roadDest, minX, minZ, step, walkable);

            Debug.Log($"[NavService] 도로 메쉬 A* 그리드: {width}x{height}, 도로 셀={walkableCount}, step={step:F1}m, start={startCell}, dest={destCell}");

            if (startCell.x < 0 || destCell.x < 0)
            {
                Debug.LogWarning($"[NavService] 도로 메쉬 그래프 시작/목적 셀 탐색 실패 — walkable 셀 {walkableCount}개. Road 레이어 레이캐스트 히트 없음 (roadLayerMask={_roadLayerMask.value})");
                return false;
            }

            if (!FindRoadGridPath(startCell, destCell, walkable, points, out List<Vector3> pathPoints))
            {
                Debug.LogWarning($"[NavService] 도로 메쉬 A* 경로 실패 — 그리드 {width}x{height} ({walkableCount} walkable), start={startCell}, dest={destCell}. 도로 세그먼트가 연결되지 않았거나 _roadSearchPadding({_roadSearchPadding}m)이 너무 작을 수 있습니다.");
                return false;
            }

            pathPoints[0] = roadStart;
            pathPoints[pathPoints.Count - 1] = roadDest;
            route = SimplifyRoute(pathPoints).ToArray();
            Debug.Log($"[NavService] 도로 메쉬 경로 계산 완료: {route.Length} points");
            return route.Length >= 2;
        }

        private bool TrySnapToRoadMesh(Vector3 pos, out Vector3 snapped)
        {
            if (RaycastRoadAtXZ(pos, out snapped))
                return true;

            float step = Mathf.Max(2f, _roadGridStep);
            float maxRadius = EffectiveNavMeshSampleRadius;
            for (float radius = step; radius <= maxRadius; radius += step)
            {
                int samples = Mathf.Max(8, Mathf.CeilToInt(radius * Mathf.PI / step));
                for (int i = 0; i < samples; i++)
                {
                    float a = i * Mathf.PI * 2f / samples;
                    Vector3 p = pos + new Vector3(Mathf.Cos(a) * radius, 0f, Mathf.Sin(a) * radius);
                    if (RaycastRoadAtXZ(p, out snapped))
                        return true;
                }
            }

            snapped = pos;
            return false;
        }

        private bool RaycastRoadAtXZ(Vector3 pos, out Vector3 hitPoint)
        {
            Vector3 origin = new Vector3(pos.x, pos.y + _roadRaycastHeight, pos.z);
            if (Physics.Raycast(origin, Vector3.down, out RaycastHit hit, _roadRaycastHeight * 2f, _roadLayerMask))
            {
                hitPoint = hit.point;
                return true;
            }

            hitPoint = pos;
            return false;
        }

        private static Vector2Int FindNearestWalkableCell(Vector3 pos, float minX, float minZ, float step, bool[,] walkable)
        {
            int width = walkable.GetLength(0);
            int height = walkable.GetLength(1);
            int cx = Mathf.Clamp(Mathf.RoundToInt((pos.x - minX) / step), 0, width - 1);
            int cz = Mathf.Clamp(Mathf.RoundToInt((pos.z - minZ) / step), 0, height - 1);

            if (walkable[cx, cz])
                return new Vector2Int(cx, cz);

            int maxRadius = Mathf.Max(width, height);
            for (int r = 1; r < maxRadius; r++)
            {
                for (int x = cx - r; x <= cx + r; x++)
                for (int z = cz - r; z <= cz + r; z++)
                {
                    if (x < 0 || z < 0 || x >= width || z >= height) continue;
                    if (Mathf.Abs(x - cx) != r && Mathf.Abs(z - cz) != r) continue;
                    if (walkable[x, z])
                        return new Vector2Int(x, z);
                }
            }

            return new Vector2Int(-1, -1);
        }

        private static bool FindRoadGridPath(
            Vector2Int start,
            Vector2Int goal,
            bool[,] walkable,
            Vector3[,] points,
            out List<Vector3> path)
        {
            path = null;
            int width = walkable.GetLength(0);
            int height = walkable.GetLength(1);

            var open = new List<Vector2Int> { start };
            var cameFrom = new Dictionary<Vector2Int, Vector2Int>();
            var gScore = new Dictionary<Vector2Int, float> { [start] = 0f };
            var closed = new HashSet<Vector2Int>();

            while (open.Count > 0)
            {
                int bestIdx = 0;
                float bestF = float.MaxValue;
                for (int i = 0; i < open.Count; i++)
                {
                    Vector2Int c = open[i];
                    float g = gScore.TryGetValue(c, out float score) ? score : float.MaxValue;
                    float f = g + Vector2Int.Distance(c, goal);
                    if (f < bestF)
                    {
                        bestF = f;
                        bestIdx = i;
                    }
                }

                Vector2Int current = open[bestIdx];
                open.RemoveAt(bestIdx);
                if (current == goal)
                {
                    path = ReconstructRoadPath(current, cameFrom, points);
                    return path.Count >= 2;
                }

                closed.Add(current);
                foreach (Vector2Int dir in RoadGridDirs)
                {
                    Vector2Int next = current + dir;
                    if (next.x < 0 || next.y < 0 || next.x >= width || next.y >= height) continue;
                    if (!walkable[next.x, next.y] || closed.Contains(next)) continue;

                    float moveCost = dir.x != 0 && dir.y != 0 ? 1.4142f : 1f;
                    float tentative = gScore[current] + moveCost;
                    if (!gScore.TryGetValue(next, out float existing) || tentative < existing)
                    {
                        cameFrom[next] = current;
                        gScore[next] = tentative;
                        if (!open.Contains(next))
                            open.Add(next);
                    }
                }
            }

            return false;
        }

        private static List<Vector3> ReconstructRoadPath(
            Vector2Int current,
            Dictionary<Vector2Int, Vector2Int> cameFrom,
            Vector3[,] points)
        {
            var path = new List<Vector3> { points[current.x, current.y] };
            while (cameFrom.TryGetValue(current, out Vector2Int prev))
            {
                current = prev;
                path.Add(points[current.x, current.y]);
            }
            path.Reverse();
            return path;
        }

        /// <summary>
        /// 도로 메쉬 불연속 구간 보완 — walkable 셀 주변 1칸을 팽창해 인접 세그먼트 연결.
        /// 이웃 walkable 셀이 있더라도 해당 XZ 위치에 실제 Road 레이캐스트가 성공해야만 walkable로 표시합니다.
        /// </summary>
        private void BridgeWalkableGaps(bool[,] walkable, Vector3[,] points, float minX, float minZ, float step)
        {
            int w = walkable.GetLength(0);
            int h = walkable.GetLength(1);

            var toFill = new List<(int x, int z, Vector3 pt)>();

            for (int x = 0; x < w; x++)
            for (int z = 0; z < h; z++)
            {
                if (walkable[x, z]) continue;

                // 8방향 이웃 중 walkable 셀이 있는지 확인
                bool hasWalkableNeighbor = false;
                float neighborY = 0f;
                for (int dx = -1; dx <= 1 && !hasWalkableNeighbor; dx++)
                for (int dz = -1; dz <= 1 && !hasWalkableNeighbor; dz++)
                {
                    if (dx == 0 && dz == 0) continue;
                    int nx = x + dx, nz = z + dz;
                    if (nx < 0 || nz < 0 || nx >= w || nz >= h) continue;
                    if (!walkable[nx, nz]) continue;
                    hasWalkableNeighbor = true;
                    neighborY = points[nx, nz].y;
                }

                if (!hasWalkableNeighbor) continue;

                // Road 레이어 레이캐스트로 실제 도로 표면 재검증
                float worldX = minX + x * step;
                float worldZ = minZ + z * step;
                Vector3 origin = new Vector3(worldX, neighborY + _roadRaycastHeight, worldZ);
                if (Physics.Raycast(origin, Vector3.down, out RaycastHit hit, _roadRaycastHeight * 2f, _roadLayerMask))
                {
                    toFill.Add((x, z, hit.point));
                }
            }

            foreach (var (x, z, pt) in toFill)
            {
                walkable[x, z] = true;
                points[x, z]   = pt;
            }
        }

        private static List<Vector3> SimplifyRoute(List<Vector3> input)
        {
            if (input == null || input.Count <= 2)
                return input ?? new List<Vector3>();

            var simplified = new List<Vector3> { input[0] };
            Vector2 prevDir = Vector2.zero;
            for (int i = 1; i < input.Count - 1; i++)
            {
                Vector2 a = new Vector2(input[i].x - input[i - 1].x, input[i].z - input[i - 1].z).normalized;
                if (i == 1 || Vector2.Dot(prevDir, a) < 0.985f)
                    simplified.Add(input[i]);
                prevDir = a;
            }
            simplified.Add(input[input.Count - 1]);
            return simplified;
        }

    }
}
