using UnityEngine;

namespace Rugem.RoadTools
{
    /// <summary>
    /// GPS 위치(GPSLocationService)와 카메라 NavAnchor(CameraNavAnchor)를 통합해
    /// 하나의 인터페이스로 위치 정보를 제공하는 퍼사드 컴포넌트입니다.
    ///
    /// NavPosition 우선순위:
    ///   1순위: CameraNavAnchor.NavTransform (지면 투영 앵커 — 경로 계산 시작점)
    ///   2순위: Camera.main 위치
    ///   3순위: GPSLocationService.SmoothedUnityPosition (GPS 보간값)
    /// </summary>
    public class PositionProvider : MonoBehaviour
    {
        /// <summary>GPS 좌표 수신 및 WGS84 → Unity 월드 좌표 변환 서비스</summary>
        [SerializeField] private GPSLocationService _gpsService;

        /// <summary>카메라 직하 지면 투영 앵커 (경로 시작점으로 사용)</summary>
        [SerializeField] private CameraNavAnchor    _navAnchor;

        // ── GPS 좌표 / Unity 위치 ─────────────────────────────────────────────

        /// <summary>GPS 서비스가 연결되어 있는지 여부</summary>
        public bool   IsReady          => _gpsService != null;

        /// <summary>마지막으로 수신된 GPS 위도 (도 단위)</summary>
        public double CurrentLatitude  => _gpsService != null ? _gpsService.CurrentLatitude  : 0.0;

        /// <summary>마지막으로 수신된 GPS 경도 (도 단위)</summary>
        public double CurrentLongitude => _gpsService != null ? _gpsService.CurrentLongitude : 0.0;

        /// <summary>Lerp 보간이 적용된 GPS 기반 플레이어 Unity 월드 좌표</summary>
        public Vector3 PlayerPosition  => _gpsService?.SmoothedUnityPosition ?? Vector3.zero;

        // ── 네비게이션 앵커 위치 (NavAnchor > Camera > GPS 순) ────────────────

        /// <summary>
        /// 경로 계산 시작점으로 사용할 위치.
        /// NavAnchor 지면 투영 → Camera.main → GPS 스무딩 순서로 폴백합니다.
        /// </summary>
        public Vector3 NavPosition
        {
            get
            {
                if (_navAnchor != null && _navAnchor.NavTransform != null)
                    return _navAnchor.NavTransform.position;
                if (Camera.main != null)
                    return Camera.main.transform.position;
                return _gpsService?.SmoothedUnityPosition ?? Vector3.zero;
            }
        }

        // ── 변환 ──────────────────────────────────────────────────────────────

        /// <summary>
        /// WGS84 위경도를 Unity 월드 좌표로 변환합니다.
        /// GPS 서비스 미연결 시 Vector3.zero를 반환합니다.
        /// </summary>
        public Vector3 ConvertToUnityPosition(double latitude, double longitude)
            => _gpsService != null
                ? _gpsService.ConvertToUnityPosition(latitude, longitude)
                : Vector3.zero;

        // ── 생명주기 ──────────────────────────────────────────────────────────

        private void Awake() => ResolveDependencies();

        /// <summary>Inspector 미연결 시 씬에서 자동으로 의존 컴포넌트를 탐색합니다.</summary>
        private void ResolveDependencies()
        {
            if (_gpsService == null) _gpsService = FindAnyObjectByType<GPSLocationService>();
            if (_navAnchor  == null) _navAnchor  = FindAnyObjectByType<CameraNavAnchor>();
        }
    }
}
