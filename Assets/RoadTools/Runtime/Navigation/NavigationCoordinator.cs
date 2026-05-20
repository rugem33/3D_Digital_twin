using System.Collections.Generic;
using UnityEngine;

namespace Rugem.RoadTools
{
    /// <summary>
    /// NavigationUIController의 단일 진입점 퍼사드입니다.
    /// 내부 동작은 각 서비스 컴포넌트로 위임합니다:
    ///   - 경로 계산 / 도착 감지 → NavigationService
    ///   - 장소 검색             → KakaoPlaceSearchService
    ///   - 경로 선 / 미니맵 표시 → RoutePresenter
    ///   - 위치 정보             → PositionProvider
    /// </summary>
    public class NavigationCoordinator : MonoBehaviour
    {
        /// <summary>NavMesh / 카카오 API 경로 계산 및 도착 감지 서비스</summary>
        [SerializeField] private NavigationService      _navigation;

        /// <summary>카카오 로컬 API 키워드 장소 검색 서비스</summary>
        [SerializeField] private KakaoPlaceSearchService _kakaoSearch;

        /// <summary>RouteRenderer + MinimapController 통합 퍼사드</summary>
        [SerializeField] private RoutePresenter          _route;

        /// <summary>GPS 위치 및 카메라 NavAnchor 통합 위치 제공자</summary>
        [SerializeField] private PositionProvider        _position;

        // ── 이벤트 (NavigationService 이벤트를 외부로 중계) ─────────────────────

        /// <summary>경로 계산 완료 시 발생. (POIData 목적지, Vector3[] 경로) 전달.</summary>
        public event System.Action<POIData, Vector3[]> OnRouteCalculated
        {
            add { ResolveDependencies(); if (_navigation != null) _navigation.OnRouteCalculated += value; }
            remove { if (_navigation != null) _navigation.OnRouteCalculated -= value; }
        }

        /// <summary>ClearNavigation() 호출로 길찾기가 취소될 때 발생.</summary>
        public event System.Action OnNavigationCleared
        {
            add { ResolveDependencies(); if (_navigation != null) _navigation.OnNavigationCleared += value; }
            remove { if (_navigation != null) _navigation.OnNavigationCleared -= value; }
        }

        /// <summary>플레이어가 목적지 도착 판정 반경 안에 들어왔을 때 발생.</summary>
        public event System.Action OnArrived
        {
            add { ResolveDependencies(); if (_navigation != null) _navigation.OnArrived += value; }
            remove { if (_navigation != null) _navigation.OnArrived -= value; }
        }

        // ── 네비게이션 상태 (NavigationService 위임) ────────────────────────────

        /// <summary>현재 설정된 목적지 POI. 길찾기 중이 아니면 null.</summary>
        public POIData   CurrentDestination    => _navigation?.CurrentDestination;

        /// <summary>현재 길찾기 중 여부</summary>
        public bool      IsNavigating          => _navigation != null && _navigation.IsNavigating;

        /// <summary>목적지까지 수평 직선 거리 (미터). 미설정 시 -1.</summary>
        public float     DistanceToDestination => _navigation != null ? _navigation.DistanceToDestination : -1f;

        /// <summary>현재 계산된 경로 waypoints 배열 (null이면 경로 없음)</summary>
        public Vector3[] CurrentRoute          => _navigation?.CurrentRoute;

        /// <summary>목적지 Unity 월드 좌표 (오버뷰 마커 및 좌표 변환에 사용)</summary>
        public Vector3   DestinationWorldPos   => _navigation != null ? _navigation.DestinationWorldPos : Vector3.zero;

        // ── 미니맵 / 경로 표시 (RoutePresenter 위임) ────────────────────────────

        /// <summary>미니맵 크기 상수 (화면 높이 대비 비율)</summary>
        public float         MapSizeRatioConst  => _route != null ? _route.MapSizeRatioConst  : 0f;

        /// <summary>미니맵 RenderTexture (오버뷰 UI 표시용)</summary>
        public RenderTexture OverviewTexture    => _route?.OverviewTexture;

        /// <summary>미니맵 컴포넌트 존재 여부</summary>
        public bool          HasMinimap         => _route != null && _route.HasMinimap;

        /// <summary>미니맵 카메라 Unity 월드 위치 (오버뷰 좌표 변환용)</summary>
        public Vector3       MinimapCamPosition => _route != null ? _route.MinimapCamPosition : Vector3.zero;

        /// <summary>미니맵 카메라 직교 투영 사이즈 (오버뷰 좌표 변환용)</summary>
        public float         MinimapOrthoSize   => _route != null ? _route.MinimapOrthoSize   : 1f;

        // ── 위치 (PositionProvider 위임) ─────────────────────────────────────────

        /// <summary>GPS Lerp 보간이 적용된 플레이어 Unity 월드 좌표</summary>
        public Vector3 PlayerPosition => _position?.PlayerPosition ?? Vector3.zero;

        /// <summary>경로 시작점으로 사용할 위치 (NavAnchor > Camera.main > GPS 순 폴백)</summary>
        public Vector3 NavPosition    => _position?.NavPosition    ?? Vector3.zero;

        // ── 생명주기 ─────────────────────────────────────────────────────────────

        private void Awake() => ResolveDependencies();

        /// <summary>Inspector 미연결 시 씬에서 각 서비스 컴포넌트를 자동 탐색합니다.</summary>
        private void ResolveDependencies()
        {
            if (_navigation  == null) _navigation  = FindAnyObjectByType<NavigationService>();
            if (_kakaoSearch == null) _kakaoSearch = FindAnyObjectByType<KakaoPlaceSearchService>();
            if (_route       == null) _route       = FindAnyObjectByType<RoutePresenter>();
            if (_position    == null) _position    = FindAnyObjectByType<PositionProvider>();
        }

        // ── 공개 API ─────────────────────────────────────────────────────────────

        /// <summary>목적지를 설정하고 경로 계산을 시작합니다.</summary>
        public void SetDestination(POIData poi) => _navigation?.SetDestination(poi);

        /// <summary>길찾기를 취소하고 모든 상태를 초기화합니다.</summary>
        public void ClearNavigation()           => _navigation?.ClearNavigation();

        // ── 경로 렌더러 (RoutePresenter 위임) ───────────────────────────────────

        /// <summary>waypoints를 받아 도로면에 투영된 경로 선을 표시합니다.</summary>
        public void ShowRoute(Vector3[] route)        => _route?.ShowRoute(route);

        /// <summary>경로 선과 목적지 마커를 숨깁니다.</summary>
        public void HideRoute()                       => _route?.HideRoute();

        /// <summary>플레이어가 지나친 앞 구간을 잘라냅니다.</summary>
        public void TrimRoute(Vector3 playerPosition) => _route?.TrimRoute(playerPosition);

        // ── 미니맵 (RoutePresenter 위임) ─────────────────────────────────────────

        /// <summary>미니맵을 경로 전체가 보이는 오버뷰 모드로 전환합니다.</summary>
        public void EnterOverviewMode(Vector3 playerPos, Vector3 destPos) => _route?.EnterOverviewMode(playerPos, destPos);

        /// <summary>미니맵을 플레이어 팔로우 일반 모드로 복귀합니다.</summary>
        public void ExitOverviewMode()                                     => _route?.ExitOverviewMode();

        // ── 검색 (KakaoPlaceSearchService 위임) ─────────────────────────────────

        /// <summary>
        /// 현재 GPS 위치를 기준으로 키워드 장소를 검색합니다.
        /// 결과는 callback(List&lt;POIData&gt;, errorMessage)으로 전달됩니다.
        /// </summary>
        public void Search(string query, int maxResults, int radiusMeters,
            System.Action<List<POIData>, string> callback)
        {
            if (_kakaoSearch == null) { callback?.Invoke(new List<POIData>(), null); return; }
            // 현재 GPS 위경도를 검색 중심으로 전달 (0,0이면 카카오 API에서 정확도순 정렬)
            double lat = _position != null ? _position.CurrentLatitude  : 0.0;
            double lon = _position != null ? _position.CurrentLongitude : 0.0;
            _kakaoSearch.Search(query, maxResults, radiusMeters, lat, lon, callback);
        }
    }
}
