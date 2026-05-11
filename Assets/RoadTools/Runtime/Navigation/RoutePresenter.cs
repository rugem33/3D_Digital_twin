using UnityEngine;

namespace Rugem.RoadTools
{
    public class RoutePresenter : MonoBehaviour
    {
        [SerializeField] private RouteRenderer     _routeRenderer;
        [SerializeField] private MinimapController _minimapController;

        public float         MapSizeRatioConst  => MinimapController.MapSizeRatioConst;
        public RenderTexture OverviewTexture     => _minimapController?.OverviewTexture;
        public bool          HasMinimap          => _minimapController != null;
        public Vector3       MinimapCamPosition  => _minimapController != null ? _minimapController.CurrentCamPosition : Vector3.zero;
        public float         MinimapOrthoSize    => _minimapController != null ? _minimapController.CurrentOrthoSize   : 1f;

        private void Awake() => ResolveDependencies();

        private void ResolveDependencies()
        {
            if (_routeRenderer     == null) _routeRenderer     = FindAnyObjectByType<RouteRenderer>();
            if (_minimapController == null) _minimapController = FindAnyObjectByType<MinimapController>();
        }

        public void ShowRoute(Vector3[] route)        => _routeRenderer?.ShowRoute(route);
        public void HideRoute()                       => _routeRenderer?.HideRoute();
        public void TrimRoute(Vector3 playerPosition) => _routeRenderer?.TrimFromPlayerPosition(playerPosition);

        public void EnterOverviewMode(Vector3 playerPos, Vector3 destPos) => _minimapController?.EnterOverviewMode(playerPos, destPos);
        public void ExitOverviewMode()                                     => _minimapController?.ExitOverviewMode();
    }
}
