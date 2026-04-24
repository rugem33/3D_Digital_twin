using System.Collections;
using UnityEngine;
using CesiumForUnity;
using Unity.Mathematics;

namespace Rugem.RoadTools
{
    /// <summary>
    /// US-04: GPS 위치 수신 및 WGS84 → Unity 월드 좌표 변환 엔진
    /// CesiumGeoreference를 활용한 좌표 변환과 Lerp 보간으로 카메라 떨림을 방지합니다.
    /// </summary>
    public class GPSLocationService : MonoBehaviour
    {
        [Header("Cesium 설정")]
        [Tooltip("씬의 CesiumGeoreference 오브젝트 (미설정 시 자동 탐색)")]
        [SerializeField] private CesiumGeoreference _georeference;

        [Header("GPS 수신 설정")]
        [Tooltip("GPS 정확도 요청값 (미터, 낮을수록 정밀)")]
        [SerializeField] private float _desiredAccuracyInMeters = 1f;
        [Tooltip("GPS 업데이트 최소 이동 거리 (미터)")]
        [SerializeField] private float _updateDistanceInMeters = 0.5f;
        [Tooltip("GPS 폴링 간격 (초)")]
        [SerializeField] private float _pollIntervalSeconds = 1f;

        [Header("Lerp 보간 설정")]
        [Tooltip("위치 보간 속도 (값이 높을수록 GPS를 빠르게 추적, 낮을수록 부드러움)")]
        [SerializeField] private float _lerpSpeed = 5f;

        // ── 공개 상태 ──────────────────────────────────────────────────────────

        /// <summary>Lerp 보간이 적용된 현재 Unity 월드 좌표</summary>
        public Vector3 SmoothedUnityPosition { get; private set; }

        /// <summary>GPS 원시 데이터로부터 변환된 목표 Unity 월드 좌표</summary>
        public Vector3 TargetUnityPosition { get; private set; }

        /// <summary>마지막으로 수신된 위도</summary>
        public double CurrentLatitude { get; private set; }

        /// <summary>마지막으로 수신된 경도</summary>
        public double CurrentLongitude { get; private set; }

        /// <summary>마지막으로 수신된 고도 (미터)</summary>
        public double CurrentAltitude { get; private set; }

        /// <summary>GPS 서비스 실행 여부</summary>
        public bool IsRunning { get; private set; }

        /// <summary>새 GPS 좌표 수신 시 호출 (변환 전 원시 Unity 좌표 전달)</summary>
        public System.Action<Vector3> OnRawPositionUpdated;

        // ── 내부 상태 ──────────────────────────────────────────────────────────

        private Coroutine _gpsCoroutine;
        private bool _hasFirstFix;

        // ── 유니티 생명주기 ────────────────────────────────────────────────────

        private void Awake()
        {
            if (_georeference == null)
                _georeference = FindAnyObjectByType<CesiumGeoreference>();

            if (_georeference == null)
                Debug.LogError("[GPSService] 씬에서 CesiumGeoreference를 찾을 수 없습니다.");
        }

        private void Update()
        {
            if (!IsRunning) return;

            // Lerp 보간: GPS 업데이트마다 갑자기 튀지 않고 부드럽게 이동
            SmoothedUnityPosition = Vector3.Lerp(
                SmoothedUnityPosition,
                TargetUnityPosition,
                Time.deltaTime * _lerpSpeed);
        }

        private void OnDestroy() => StopGPS();

        // ── 공개 메서드 ────────────────────────────────────────────────────────

        /// <summary>
        /// GPS 수신을 시작합니다. LocationPermissionHandler에서 권한 허용 후 호출하세요.
        /// </summary>
        public void StartGPS()
        {
            if (_gpsCoroutine != null)
                StopCoroutine(_gpsCoroutine);

            _gpsCoroutine = StartCoroutine(GPSUpdateLoop());
        }

        /// <summary>
        /// GPS 수신을 중단합니다.
        /// </summary>
        public void StopGPS()
        {
            IsRunning = false;

            if (_gpsCoroutine != null)
            {
                StopCoroutine(_gpsCoroutine);
                _gpsCoroutine = null;
            }

            if (Input.location.status == LocationServiceStatus.Running)
                Input.location.Stop();
        }

        /// <summary>
        /// WGS84 좌표(위도, 경도, 고도)를 Unity 월드 좌표로 변환합니다.
        /// CesiumGeoreference → ECEF → Unity World 변환을 수행합니다.
        /// </summary>
        /// <param name="latitude">위도 (도)</param>
        /// <param name="longitude">경도 (도)</param>
        /// <param name="altitude">고도 (미터, 기본값 0)</param>
        /// <returns>Unity 월드 좌표 (Vector3)</returns>
        public Vector3 ConvertToUnityPosition(double latitude, double longitude, double altitude = 0.0)
        {
            if (_georeference == null)
            {
                Debug.LogError("[GPSService] CesiumGeoreference가 없어 좌표 변환을 수행할 수 없습니다.");
                return Vector3.zero;
            }

            // 1. WGS84(경도, 위도, 고도) → ECEF (지구 중심 고정 좌표계)
            double3 ecef = CesiumWgs84Ellipsoid.LongitudeLatitudeHeightToEarthCenteredEarthFixed(
                new double3(longitude, latitude, altitude));

            // 2. ECEF → Unity 월드 좌표계
            double3 unityCoords = _georeference.TransformEarthCenteredEarthFixedPositionToUnity(ecef);

            return (Vector3)(float3)unityCoords;
        }

        // ── 내부 구현 ──────────────────────────────────────────────────────────

        private IEnumerator GPSUpdateLoop()
        {
#if !UNITY_EDITOR
            // 사용자가 기기 설정에서 위치를 아예 꺼놓은 경우 바로 실패 처리
            if (!Input.location.isEnabledByUser)
            {
                Debug.LogError("[GPSService] 기기 위치 서비스가 비활성화 상태입니다. 기기 설정에서 위치를 켜주세요.");
                yield break;
            }

            Input.location.Start(_desiredAccuracyInMeters, _updateDistanceInMeters);
            Debug.Log("[GPSService] GPS 서비스 초기화 중...");

            // 초기화 대기 (최대 20초)
            float timeout = 20f;
            while (Input.location.status == LocationServiceStatus.Initializing && timeout > 0f)
            {
                yield return new WaitForSeconds(0.5f);
                timeout -= 0.5f;
            }

            if (Input.location.status != LocationServiceStatus.Running)
            {
                Debug.LogError($"[GPSService] GPS 서비스 시작 실패. 상태: {Input.location.status}");
                yield break;
            }

            IsRunning = true;
            Debug.Log("[GPSService] GPS 서비스가 시작되었습니다.");

            while (IsRunning)
            {
                switch (Input.location.status)
                {
                    case LocationServiceStatus.Running:
                        ProcessLocationData(
                            Input.location.lastData.latitude,
                            Input.location.lastData.longitude,
                            Input.location.lastData.altitude);
                        break;
                    case LocationServiceStatus.Failed:
                    case LocationServiceStatus.Stopped:
                        Debug.LogWarning($"[GPSService] 위치 서비스 중단 감지 (상태: {Input.location.status}). 재시작 시도...");
                        Input.location.Stop();
                        yield return new WaitForSeconds(2f);
                        Input.location.Start(_desiredAccuracyInMeters, _updateDistanceInMeters);
                        break;
                }

                yield return new WaitForSeconds(_pollIntervalSeconds);
            }

            Input.location.Stop();
#else
            // 에디터: GPS 시뮬레이션 (CesiumGeoreference 원점 좌표)
            double simLat = _georeference != null ? _georeference.latitude : 37.5662952;
            double simLon = _georeference != null ? _georeference.longitude : 126.9779692;
            Debug.Log($"[GPSService] 에디터 환경 - GPS 시뮬레이션 위치: ({simLat:F6}, {simLon:F6})");
            IsRunning = true;

            ProcessLocationData(simLat, simLon, 0.0);
            yield break;
#endif
        }

        private void ProcessLocationData(double lat, double lon, double alt)
        {
            CurrentLatitude = lat;
            CurrentLongitude = lon;
            CurrentAltitude = alt;

            Vector3 rawUnityPos = ConvertToUnityPosition(lat, lon, alt);
            TargetUnityPosition = rawUnityPos;

            // 첫 수신 시 SmoothedUnityPosition을 즉시 동기화하여 초기 Lerp 도약 방지
            if (!_hasFirstFix)
            {
                SmoothedUnityPosition = rawUnityPos;
                _hasFirstFix = true;
            }

            OnRawPositionUpdated?.Invoke(rawUnityPos);
            Debug.Log($"[GPSService] GPS 수신 - 위도: {lat:F6}, 경도: {lon:F6}, 고도: {alt:F1}m");
        }
    }
}
