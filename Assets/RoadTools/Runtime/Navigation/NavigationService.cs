using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;

namespace Rugem.RoadTools
{
    /// <summary>
    /// 길찾기 핵심 로직 — POI 검색, 목적지 설정, NavMesh 경로 계산, 도착 감지
    /// </summary>
    public class NavigationService : MonoBehaviour
    {
        [Header("의존성")]
        [SerializeField] private GPSLocationService _gpsService;
        [SerializeField] private FirstPersonGPSController _playerController;
        [Tooltip("카메라 수직 하방 지형 지점을 추적하는 앵커. 없으면 GPS 스무딩 위치 폴백.")]
        [SerializeField] private CameraNavAnchor _navAnchor;

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
        [Tooltip("씬에 직접 저장하지 마세요. Assets/Resources/kakao_api_key.txt 에서 자동 로드됩니다.\n" +
                 "(해당 파일을 .gitignore에 추가하여 커밋에서 제외하세요)")]
        // API 키는 씬 파일에 직렬화하지 않음 — Resources/kakao_api_key.txt 에서 런타임 로드
        private string _kakaoRestApiKey = "";
        [Tooltip("직접 키를 입력하지 않고 씬에 있는 KakaoDirectionsService 컴포넌트를 참조할 경우 여기에 연결합니다. (선택)")]
        [SerializeField] private KakaoDirectionsService _directionsService;

        [Header("POI 목록")]
        [Tooltip("Inspector에서 직접 편집하거나 InitializeSamplePOIs()를 통해 기본값 로드")]
        [SerializeField] private List<POIData> _poiList = new();

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

        private void Awake()
        {
            if (_poiList.Count == 0)
                InitializeSamplePOIs();

            ResolveDependencies();
        }

        private void ResolveDependencies()
        {
            if (_gpsService == null)
                _gpsService = FindAnyObjectByType<GPSLocationService>();
            if (_playerController == null)
                _playerController = FindAnyObjectByType<FirstPersonGPSController>();
            // API 키를 Resources에서 로드 (씬 파일 직렬화 방지)
            if (string.IsNullOrWhiteSpace(_kakaoRestApiKey))
            {
                var cfg = Resources.Load<TextAsset>("kakao_api_key");
                if (cfg != null) _kakaoRestApiKey = cfg.text.Trim();
            }

            // 키 직접 입력 우선 — 컴포넌트 참조 없이 자동 구성
            if (!string.IsNullOrWhiteSpace(_kakaoRestApiKey))
            {
                if (_directionsService == null)
                    _directionsService = gameObject.GetComponent<KakaoDirectionsService>()
                                      ?? gameObject.AddComponent<KakaoDirectionsService>();
                _directionsService.Initialize(_kakaoRestApiKey);
            }
            else if (_directionsService == null)
            {
                _directionsService = FindAnyObjectByType<KakaoDirectionsService>();
            }
            if (_navAnchor == null)
                _navAnchor = FindAnyObjectByType<CameraNavAnchor>();

            if (_roadLayerMask == 0)
            {
                int roadLayer = LayerMask.NameToLayer("Road");
                if (roadLayer >= 0)
                    _roadLayerMask = 1 << roadLayer;
            }

            // "Road" 레이어가 없어도 RoadAssetPlacer에서 도로 레이어 자동 상속
            if (_roadLayerMask == 0)
            {
                var placer = FindAnyObjectByType<RoadAssetPlacer>();
                if (placer != null && placer.roadLayerMask != 0)
                {
                    _roadLayerMask = placer.roadLayerMask;
                    Debug.Log($"[NavService] RoadAssetPlacer에서 도로 레이어 자동 감지: {_roadLayerMask.value}");
                }
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

        /// <summary>query를 이름·카테고리에 포함 검색. 빈 문자열이면 전체 반환.</summary>
        public List<POIData> SearchPOIs(string query)
        {
            if (string.IsNullOrWhiteSpace(query))
                return new List<POIData>(_poiList);

            string lower = query.Trim().ToLower();
            return _poiList.FindAll(p =>
                p.name.ToLower().Contains(lower) ||
                p.category.ToLower().Contains(lower));
        }

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
            if (_gpsService == null)
            {
                Debug.LogError("[NavService] GPSLocationService가 연결되지 않았습니다.");
                return;
            }

            CurrentDestination    = poi;
            IsNavigating          = true;
            DistanceToDestination = -1f;
            _refreshTimer         = 0f;

            _destinationWorldPos = _gpsService.ConvertToUnityPosition(poi.latitude, poi.longitude);
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
            if (_playerController != null)
                _playerController.TeleportTo(CurrentDestination.latitude, CurrentDestination.longitude);
            else
                Debug.LogWarning("[NavService] FirstPersonGPSController가 연결되지 않았습니다.");
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
            ResolveDependencies();
            if (_gpsService == null) return;

            Vector3 playerXZ = new Vector3(
                _gpsService.SmoothedUnityPosition.x, 0f,
                _gpsService.SmoothedUnityPosition.z);
            Vector3 destXZ = new Vector3(_destinationWorldPos.x, 0f, _destinationWorldPos.z);
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
            if (_gpsService == null) return;

            // 우선순위: 카카오 Directions API → 도로 메쉬 A* → NavMesh → 직선 폴백
            int requestId = _routeRequestId;
            POIData destinationSnapshot = CurrentDestination;

            if (_directionsService != null)
            {
                _directionsService.RequestRoute(
                    _gpsService.CurrentLatitude,  _gpsService.CurrentLongitude,
                    destinationSnapshot.latitude,  destinationSnapshot.longitude,
                    (waypoints, error) =>
                    {
                        if (requestId != _routeRequestId || destinationSnapshot != CurrentDestination)
                            return;

                        if (error == null && waypoints != null && waypoints.Length >= 2)
                        {
                            // 현재 위치 → 카카오 경로 시작점을 직선으로 보간
                            Vector3 playerPos = (_navAnchor != null && _navAnchor.NavTransform != null)
                                ? _navAnchor.NavTransform.position
                                : _gpsService.SmoothedUnityPosition;

                            var fullRoute = new Vector3[waypoints.Length + 1];
                            fullRoute[0] = playerPos;
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
            if (_gpsService == null) return;

            // mainCameraNav(지형 표면 정사영)를 시작점으로 우선 사용, 없으면 GPS 스무딩 위치 폴백
            Vector3 startPos = (_navAnchor != null && _navAnchor.NavTransform != null)
                ? _navAnchor.NavTransform.position
                : _gpsService.SmoothedUnityPosition;

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

        // ── 샘플 POI 초기화 ─────────────────────────────────────────────────────

        private void InitializeSamplePOIs()
        {
            // CesiumGeoreference 원점(37.5662952, 126.9779692) 기준 주변 샘플 데이터
            // 실제 서비스에서는 Inspector 또는 CSV로 교체하세요.
            _poiList = new List<POIData>
            {
                new("버스정류장 A", "교통",     37.5665,  126.9782),
                new("버스정류장 B", "교통",     37.5658,  126.9786),
                new("중앙공원 입구", "공원",    37.5671,  126.9774),
                new("편의점 GS25",   "상업",    37.5663,  126.9785),
                new("스타벅스 카페", "상업",    37.5660,  126.9777),
                new("약국",          "의료",    37.5657,  126.9783),
                new("내과 의원",     "의료",    37.5668,  126.9780),
                new("구립 도서관",   "문화",    37.5673,  126.9778),
                new("초등학교",      "교육",    37.5654,  126.9784),
                new("우체국",        "공공기관",37.5669,  126.9775),
                new("공중화장실",    "편의시설",37.5661,  126.9788),
                new("경찰서",        "공공기관",37.5656,  126.9773),
                new("자전거 대여소", "교통",    37.5666,  126.9781),
                new("ATM",           "편의시설",37.5662,  126.9776),
            };
        }
    }
}
