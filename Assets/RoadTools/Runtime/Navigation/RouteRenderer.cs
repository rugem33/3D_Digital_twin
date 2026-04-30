using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;

namespace Rugem.RoadTools
{
    /// <summary>
    /// NavMesh 경로를 도로면(World Terrain) 위에 실선으로 표시합니다.
    ///
    /// 시작점:
    ///   Camera.main 위치에서 수직 하방 Raycast → 카메라 아래 바닥면 Y 획득
    ///   (카메라는 항상 도로 위에 있으므로 하방 레이가 정확한 도로면을 감지)
    ///
    /// 경로 중간점:
    ///   1. NavMesh.SamplePosition으로 도로면 Y 스냅
    ///   2. 스냅 Y 기준으로 상향 Raycast → 지형/도로 표면에 미세 보정
    ///      (건물 지붕은 NavMesh Y보다 위에 있으므로 절대 감지 안 됨)
    ///
    /// Raycast 실패(타일 미로드) 시 NavMesh Y + _groundOffset 폴백.
    /// </summary>
    [RequireComponent(typeof(LineRenderer))]
    public class RouteRenderer : MonoBehaviour
    {
        [Header("경로 선 설정")]
        [SerializeField] private Color _routeStartColor = new Color(0.0f, 0.55f, 1.0f, 0.95f);
        [SerializeField] private Color _routeEndColor   = new Color(0.0f, 0.55f, 1.0f, 0.30f);
        [SerializeField] private float _lineWidth       = 4.0f;
        [Tooltip("바닥면 위 선 높이 오프셋 (미터) — Z파이팅 방지 + 가시성 확보")]
        [SerializeField] private float _groundOffset    = 0.3f;

        [Header("NavMesh · 지형 투영 설정")]
        [Tooltip("세그먼트 보간 간격 (미터). 작을수록 도로 굴곡을 세밀하게 따름. 권장: 3~8")]
        [SerializeField] private float _terrainSampleStep = 5f;
        [Tooltip("각 보간 점을 NavMesh에 스냅할 탐색 반경 (미터). _terrainSampleStep 이상 권장")]
        [SerializeField] private float _navMeshSnapRadius = 10f;
        [Tooltip("Camera.main 기준 레이캐스트 상단 오프셋 (미터).\n" +
                 "카메라 높이 + 이 값 위에서 하방으로 레이를 쏘아 지형 표면을 탐색합니다. 권장: 50~200")]
        [SerializeField] private float _groundSearchRange = 100f;
        [Tooltip("지형/도로로 인식할 레이어. 0이면 전체 레이어 사용")]
        [SerializeField] private LayerMask _terrainLayerMask = ~0;
        [Tooltip("세그먼트당 최대 보간 점 수 — 긴 직선 구간에서 성능 보호")]
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

        // NavMesh 스냅 + 지형 투영이 완료된 경로
        private Vector3[] _fullRoute;

        // ── 생명주기 ────────────────────────────────────────────────────────────

        private void Awake()
        {
            _line = GetComponent<LineRenderer>();
            ConfigureLine();
            CreateDestinationMarker();
        }

        private void OnDestroy()
        {
            if (_destinationMarker != null)
                Destroy(_destinationMarker);
        }

        // ── 공개 API ────────────────────────────────────────────────────────────

        /// <summary>
        /// NavMesh 경로 waypoints를 도로면 위에 실선으로 표시합니다.
        /// 내부적으로 NavMesh 스냅 + 지형 투영을 수행합니다.
        /// </summary>
        public void ShowRoute(Vector3[] waypoints)
        {
            EnsureInitialized();
            if (waypoints == null || waypoints.Length < 2)
            {
                HideRoute();
                return;
            }

            _fullRoute = ProjectOnNavMeshAndTerrain(waypoints);
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
        /// 플레이어 위치(mainCameraNav 지형 정사영 위치 또는 카메라 위치)를 받아
        /// 바로 아래 바닥면을 경로 시작점으로 설정하고 지나친 구간을 제거합니다.
        /// </summary>
        public void TrimFromPlayerPosition(Vector3 playerWorldPos)
        {
            EnsureInitialized();
            if (_fullRoute == null || _fullRoute.Length < 2) return;

            int nearestIdx = FindNearestRouteIndex(playerWorldPos);

            // 전달받은 위치에서 하방 Raycast로 도로면 Y 정밀 보정
            // 이미 지형 표면(mainCameraNav)인 경우에도 동일 로직이 정확히 동작함
            Vector3 projStart = SampleGroundBelowCamera(playerWorldPos, _fullRoute[nearestIdx].y);

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

        // ── NavMesh · 지형 투영 ──────────────────────────────────────────────────

        /// <summary>
        /// NavMesh 경로를 _terrainSampleStep 간격으로 보간하고
        /// 각 점을 NavMesh 스냅 → 지형 상향 Raycast로 도로면에 투영합니다.
        /// </summary>
        private Vector3[] ProjectOnNavMeshAndTerrain(Vector3[] waypoints)
        {
            var result = new List<Vector3>(waypoints.Length * 8);

            for (int i = 0; i < waypoints.Length; i++)
            {
                result.Add(SnapToRoadSurface(waypoints[i]));

                if (i < waypoints.Length - 1)
                {
                    Vector3 a = waypoints[i];
                    Vector3 b = waypoints[i + 1];

                    float segLen = new Vector2(b.x - a.x, b.z - a.z).magnitude;
                    int steps = Mathf.Clamp(
                        Mathf.FloorToInt(segLen / _terrainSampleStep),
                        0, _maxSubdivisionsPerSegment);

                    for (int s = 1; s < steps; s++)
                    {
                        float t = (float)s / steps;
                        result.Add(SnapToRoadSurface(Vector3.Lerp(a, b, t)));
                    }
                }
            }

            return result.ToArray();
        }

        /// <summary>
        /// 주어진 점을 도로면에 투영합니다.
        ///   1단계: NavMesh.SamplePosition으로 도로면 Y 획득
        ///   2단계: 그 Y 기준으로 상향 Raycast → 지형 표면 Y로 보정
        /// </summary>
        private Vector3 SnapToRoadSurface(Vector3 pos)
        {
            // NavMesh가 없는 Cesium 환경에서는 스냅 실패 → pos 그대로 사용
            Vector3 refPos = NavMesh.SamplePosition(pos, out NavMeshHit navHit, _navMeshSnapRadius, NavMesh.AllAreas)
                ? navHit.position
                : pos;

            return SampleTerrainDownward(refPos);
        }

        /// <summary>
        /// 카메라 높이를 기준으로 수직 하방 Raycast로 지형 표면 Y를 샘플링합니다.
        ///
        /// Kakao API 웨이포인트는 고도 없이 변환되어 pos.y가 실제 지형 Y와 크게 다를 수 있습니다.
        /// Camera.main.y (항상 GPS+Cesium으로 보정된 정확한 높이)를 기준으로 레이를 쏘므로
        /// pos.y 오차에 관계없이 Cesium 타일 지형 표면을 안정적으로 감지합니다.
        /// </summary>
        private Vector3 SampleTerrainDownward(Vector3 pos)
        {
            LayerMask mask = _terrainLayerMask == 0 ? ~0 : _terrainLayerMask;

            // 카메라 높이 + 오프셋 위에서 하방으로 충분히 긴 레이캐스트
            // Camera.main이 없으면 pos.y + _groundSearchRange 폴백
            float    camY   = Camera.main != null ? Camera.main.transform.position.y : pos.y;
            float    origY  = camY + _groundSearchRange;
            float    maxD   = origY - (pos.y - _groundSearchRange) + 50f;
            Vector3  origin = new Vector3(pos.x, origY, pos.z);

            if (Physics.Raycast(origin, Vector3.down, out RaycastHit hit, maxD, mask))
                return new Vector3(pos.x, hit.point.y + _groundOffset, pos.z);

            // 폴백: 타일 미로드 상태 — 카메라 지면 Y 추정값 사용
            return new Vector3(pos.x, camY - 2f + _groundOffset, pos.z);
        }

        /// <summary>
        /// 카메라 위치에서 수직 하방 Raycast로 바로 아래 도로면 Y를 샘플링합니다.
        /// 카메라는 항상 도로/지형 위에 있으므로 하방 레이가 정확한 도로면을 감지합니다.
        /// </summary>
        /// <param name="cameraPos">Camera.main.transform.position</param>
        /// <param name="routeRefY">Raycast 실패 시 폴백으로 사용할 경로 참조 Y</param>
        private Vector3 SampleGroundBelowCamera(Vector3 cameraPos, float routeRefY)
        {
            LayerMask mask   = _terrainLayerMask == 0 ? ~0 : _terrainLayerMask;
            // 카메라보다 100m 위에서 아래로 쏘면 카메라 위치 직하 지면을 확실히 감지
            Vector3   origin = new Vector3(cameraPos.x, cameraPos.y + 100f, cameraPos.z);

            if (Physics.Raycast(origin, Vector3.down, out RaycastHit hit, 1000f, mask))
                return new Vector3(cameraPos.x, hit.point.y + _groundOffset, cameraPos.z);

            // 폴백: 가장 가까운 경로 점 Y (이미 도로면에 투영된 값)
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

            _line.startWidth        = _lineWidth;
            _line.endWidth          = _lineWidth;
            _line.startColor        = _routeStartColor;
            _line.endColor          = _routeEndColor;
            _line.useWorldSpace     = true;
            _line.numCornerVertices = 4;
            _line.numCapVertices    = 4;
            _line.enabled           = false;

            if (_routeMaterialOverride != null)
            {
                _line.material = _routeMaterialOverride;
            }
            else
            {
                // URP 우선, Built-in 폴백 순서
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

        /// <summary>NavMesh + 지형 투영이 완료된 점 배열을 LineRenderer에 직접 설정합니다.</summary>
        private void DrawLine(Vector3[] projected)
        {
            EnsureInitialized();
            if (_line == null) return;

            if (projected == null || projected.Length < 2)
            {
                _line.enabled = false;
                return;
            }
            _line.positionCount = projected.Length;
            _line.SetPositions(projected);
            _line.enabled = true;
        }

        private void EnsureInitialized()
        {
            if (_line == null)
            {
                _line = GetComponent<LineRenderer>();
                ConfigureLine();
            }
        }
    }
}
