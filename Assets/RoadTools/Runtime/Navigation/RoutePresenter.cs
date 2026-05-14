using UnityEngine;

namespace Rugem.RoadTools
{
    /// <summary>
    /// 경로 시각화(RouteRenderer)와 미니맵(MinimapController)을 하나의 인터페이스로 묶는 퍼사드입니다.
    /// NavigationCoordinator → RoutePresenter → RouteRenderer / MinimapController 순으로 호출을 위임합니다.
    /// </summary>
    public class RoutePresenter : MonoBehaviour
    {
        /// <summary>LineRenderer로 경로 선과 목적지 마커를 그리는 컴포넌트</summary>
        [SerializeField] private RouteRenderer     _routeRenderer;

        /// <summary>상단 미니맵 카메라와 Canvas UI(플레이어 화살표 등)를 관리하는 컴포넌트</summary>
        [SerializeField] private MinimapController _minimapController;

        /// <summary>미니맵 크기 상수 (화면 높이 대비 비율, 0.22 = 22%)</summary>
        public float         MapSizeRatioConst  => MinimapController.MapSizeRatioConst;

        /// <summary>미니맵 또는 경로 오버뷰에 사용하는 RenderTexture (NavigationUIController에서 표시)</summary>
        public RenderTexture OverviewTexture     => _minimapController?.OverviewTexture;

        /// <summary>미니맵 컴포넌트가 연결되어 있는지 여부</summary>
        public bool          HasMinimap          => _minimapController != null;

        /// <summary>현재 미니맵 카메라의 Unity 월드 위치 (오버뷰 좌표 변환에 사용)</summary>
        public Vector3       MinimapCamPosition  => _minimapController != null ? _minimapController.CurrentCamPosition : Vector3.zero;

        /// <summary>현재 미니맵 카메라의 직교 투영 사이즈 (클수록 넓은 범위를 표시)</summary>
        public float         MinimapOrthoSize    => _minimapController != null ? _minimapController.CurrentOrthoSize   : 1f;

        private void Awake() => ResolveDependencies();

        /// <summary>Inspector 미연결 시 씬에서 자동으로 의존 컴포넌트를 탐색합니다.</summary>
        private void ResolveDependencies()
        {
            if (_routeRenderer     == null) _routeRenderer     = FindAnyObjectByType<RouteRenderer>();
            if (_minimapController == null) _minimapController = FindAnyObjectByType<MinimapController>();
        }

        /// <summary>waypoints를 받아 도로 NavMesh + 지면에 투영된 경로 선을 표시합니다.</summary>
        public void ShowRoute(Vector3[] route)        => _routeRenderer?.ShowRoute(route);

        /// <summary>경로 선과 목적지 구 마커를 비활성화합니다.</summary>
        public void HideRoute()                       => _routeRenderer?.HideRoute();

        /// <summary>플레이어가 이미 지나친 경로 앞부분을 잘라냅니다.</summary>
        public void TrimRoute(Vector3 playerPosition) => _routeRenderer?.TrimFromPlayerPosition(playerPosition);

        /// <summary>미니맵을 오버뷰(전체 경로 확인) 모드로 전환합니다. 카메라가 경로 중심점으로 이동합니다.</summary>
        public void EnterOverviewMode(Vector3 playerPos, Vector3 destPos) => _minimapController?.EnterOverviewMode(playerPos, destPos);

        /// <summary>미니맵을 플레이어 팔로우 일반 모드로 복귀합니다.</summary>
        public void ExitOverviewMode()                                     => _minimapController?.ExitOverviewMode();
    }
}
