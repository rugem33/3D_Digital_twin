using System.Collections.Generic;
using UnityEngine;

namespace Rugem.RoadTools
{
    /// <summary>
    /// NavigationUIController의 단일 진입점.
    /// 내부는 KakaoPlaceSearchService / RoutePresenter / PositionProvider 로 위임한다.
    /// </summary>
    public class NavigationCoordinator : MonoBehaviour
    {
        [SerializeField] private NavigationService     _navigation;
        [SerializeField] private KakaoPlaceSearchService _kakaoSearch;
        [SerializeField] private RoutePresenter          _route;
        [SerializeField] private PositionProvider        _position;

        public event System.Action<POIData, Vector3[]> OnRouteCalculated
        {
            add { ResolveDependencies(); if (_navigation != null) _navigation.OnRouteCalculated += value; }
            remove { if (_navigation != null) _navigation.OnRouteCalculated -= value; }
        }

        public event System.Action OnNavigationCleared
        {
            add { ResolveDependencies(); if (_navigation != null) _navigation.OnNavigationCleared += value; }
            remove { if (_navigation != null) _navigation.OnNavigationCleared -= value; }
        }

        public event System.Action OnArrived
        {
            add { ResolveDependencies(); if (_navigation != null) _navigation.OnArrived += value; }
            remove { if (_navigation != null) _navigation.OnArrived -= value; }
        }

        // ── 내비게이션 상태 (NavigationService) ──────────────────────────────
        public POIData   CurrentDestination    => _navigation?.CurrentDestination;
        public bool      IsNavigating          => _navigation != null && _navigation.IsNavigating;
        public float     DistanceToDestination => _navigation != null ? _navigation.DistanceToDestination : -1f;
        public Vector3[] CurrentRoute          => _navigation?.CurrentRoute;
        public Vector3   DestinationWorldPos   => _navigation != null ? _navigation.DestinationWorldPos : Vector3.zero;

        // ── 미니맵 / 경로 표시 (RoutePresenter) ──────────────────────────────
        public float         MapSizeRatioConst => _route != null ? _route.MapSizeRatioConst  : 0f;
        public RenderTexture OverviewTexture   => _route?.OverviewTexture;
        public bool          HasMinimap        => _route != null && _route.HasMinimap;
        public Vector3       MinimapCamPosition => _route != null ? _route.MinimapCamPosition : Vector3.zero;
        public float         MinimapOrthoSize   => _route != null ? _route.MinimapOrthoSize   : 1f;

        // ── 위치 (PositionProvider) ───────────────────────────────────────────
        public Vector3 PlayerPosition => _position?.PlayerPosition ?? Vector3.zero;
        public Vector3 NavPosition    => _position?.NavPosition    ?? Vector3.zero;

        // ── 생명주기 ─────────────────────────────────────────────────────────

        private void Awake() => ResolveDependencies();

        private void ResolveDependencies()
        {
            if (_navigation  == null) _navigation  = FindAnyObjectByType<NavigationService>();
            if (_kakaoSearch == null) _kakaoSearch = FindAnyObjectByType<KakaoPlaceSearchService>();
            if (_route       == null) _route       = FindAnyObjectByType<RoutePresenter>();
            if (_position    == null) _position    = FindAnyObjectByType<PositionProvider>();
        }

        public void SetDestination(POIData poi) => _navigation?.SetDestination(poi);
        public void ClearNavigation()           => _navigation?.ClearNavigation();

        // ── 경로 렌더러 (RoutePresenter) ──────────────────────────────────────
        public void ShowRoute(Vector3[] route)        => _route?.ShowRoute(route);
        public void HideRoute()                       => _route?.HideRoute();
        public void TrimRoute(Vector3 playerPosition) => _route?.TrimRoute(playerPosition);

        // ── 미니맵 (RoutePresenter) ───────────────────────────────────────────
        public void EnterOverviewMode(Vector3 playerPos, Vector3 destPos) => _route?.EnterOverviewMode(playerPos, destPos);
        public void ExitOverviewMode()                                     => _route?.ExitOverviewMode();

        // ── 검색 (KakaoPlaceSearchService) ───────────────────────────────────
        public void Search(string query, int maxResults, int radiusMeters,
            System.Action<List<POIData>, string> callback)
        {
            if (_kakaoSearch == null) { callback?.Invoke(new List<POIData>(), null); return; }
            double lat = _position != null ? _position.CurrentLatitude  : 0.0;
            double lon = _position != null ? _position.CurrentLongitude : 0.0;
            _kakaoSearch.Search(query, maxResults, radiusMeters, lat, lon, callback);
        }
    }
}
