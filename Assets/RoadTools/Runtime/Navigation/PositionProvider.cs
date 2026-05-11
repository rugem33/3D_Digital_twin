using UnityEngine;

namespace Rugem.RoadTools
{
    public class PositionProvider : MonoBehaviour
    {
        [SerializeField] private GPSLocationService       _gpsService;
        [SerializeField] private CameraNavAnchor          _navAnchor;
        [SerializeField] private FirstPersonGPSController _playerController;

        // ── GPS 좌표 / Unity 위치 ─────────────────────────────────────────────
        public bool   IsReady          => _gpsService != null;
        public double CurrentLatitude  => _gpsService != null ? _gpsService.CurrentLatitude  : 0.0;
        public double CurrentLongitude => _gpsService != null ? _gpsService.CurrentLongitude : 0.0;
        public Vector3 PlayerPosition  => _gpsService?.SmoothedUnityPosition ?? Vector3.zero;

        // ── 네비게이션 앵커 위치 (NavAnchor > Camera > GPS 순) ────────────────
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

        // ── 변환 / 이동 ───────────────────────────────────────────────────────
        public Vector3 ConvertToUnityPosition(double latitude, double longitude)
            => _gpsService != null
                ? _gpsService.ConvertToUnityPosition(latitude, longitude)
                : Vector3.zero;

        public void TeleportTo(double latitude, double longitude)
        {
            if (_playerController != null)
                _playerController.TeleportTo(latitude, longitude);
            else
                Debug.LogWarning("[PositionProvider] FirstPersonGPSController가 연결되지 않았습니다.");
        }

        // ── 생명주기 ──────────────────────────────────────────────────────────

        private void Awake() => ResolveDependencies();

        private void ResolveDependencies()
        {
            if (_gpsService       == null) _gpsService       = FindAnyObjectByType<GPSLocationService>();
            if (_navAnchor        == null) _navAnchor        = FindAnyObjectByType<CameraNavAnchor>();
            if (_playerController == null) _playerController = FindAnyObjectByType<FirstPersonGPSController>();
        }
    }
}
