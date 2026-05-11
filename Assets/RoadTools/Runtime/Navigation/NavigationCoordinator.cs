using System.Collections.Generic;
using UnityEngine;

namespace Rugem.RoadTools
{
    /// <summary>
    /// NavigationUIController의 단일 진입점.
    /// 내부는 SearchProvider / RoutePresenter / PositionProvider 로 위임한다.
    /// </summary>
    public class NavigationCoordinator : MonoBehaviour
    {
        [SerializeField] private SearchProvider   _search;
        [SerializeField] private RoutePresenter   _route;
        [SerializeField] private PositionProvider _position;

        // ── 미니맵 / 경로 표시 (RoutePresenter) ──────────────────────────────
        public float         MapSizeRatioConst  => _route != null ? _route.MapSizeRatioConst  : 0f;
        public RenderTexture OverviewTexture     => _route?.OverviewTexture;
        public bool          HasMinimap          => _route != null && _route.HasMinimap;
        public Vector3       MinimapCamPosition  => _route != null ? _route.MinimapCamPosition : Vector3.zero;
        public float         MinimapOrthoSize    => _route != null ? _route.MinimapOrthoSize   : 1f;

        // ── 위치 (PositionProvider) ───────────────────────────────────────────
        public Vector3 PlayerPosition => _position?.PlayerPosition ?? Vector3.zero;
        public Vector3 NavPosition    => _position?.NavPosition    ?? Vector3.zero;

        // ── 생명주기 ─────────────────────────────────────────────────────────

        private void Awake() => ResolveDependencies();

        private void ResolveDependencies()
        {
            if (_search   == null) _search   = FindAnyObjectByType<SearchProvider>();
            if (_route    == null) _route    = FindAnyObjectByType<RoutePresenter>();
            if (_position == null) _position = FindAnyObjectByType<PositionProvider>();
        }

        // ── 경로 렌더러 (RoutePresenter) ──────────────────────────────────────
        public void ShowRoute(Vector3[] route)        => _route?.ShowRoute(route);
        public void HideRoute()                       => _route?.HideRoute();
        public void TrimRoute(Vector3 playerPosition) => _route?.TrimRoute(playerPosition);

        // ── 미니맵 (RoutePresenter) ───────────────────────────────────────────
        public void EnterOverviewMode(Vector3 playerPos, Vector3 destPos) => _route?.EnterOverviewMode(playerPos, destPos);
        public void ExitOverviewMode()                                     => _route?.ExitOverviewMode();

        // ── 검색 (SearchProvider) ─────────────────────────────────────────────
        public void Search(string query, int maxResults, int radiusMeters,
            System.Action<List<POIData>, string> callback)
            => _search?.Search(query, maxResults, radiusMeters, callback);
    }
}
