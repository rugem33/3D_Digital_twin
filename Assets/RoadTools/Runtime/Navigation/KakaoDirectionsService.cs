using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Networking;

namespace Rugem.RoadTools
{
    /// <summary>
    /// 카카오 모빌리티 API — 자동차 도로 경로 요청 (v1/directions)
    /// 응답 vertexes(경도·위도 쌍)를 GPSLocationService를 통해 Unity 월드 좌표로 변환합니다.
    /// REST API 키는 KakaoPlaceSearchService와 동일한 키를 사용합니다.
    /// </summary>
    public class KakaoDirectionsService : MonoBehaviour
    {
        [Header("카카오 모빌리티 API 설정")]
        [Tooltip("developers.kakao.com → 앱 → 앱 키 → REST API 키 (KakaoPlaceSearchService와 동일)")]
        [SerializeField] private string _restApiKey = "";

        [Tooltip("요청 타임아웃 (초)")]
        [SerializeField] private int _timeoutSeconds = 15;

        [Header("의존성")]
        [SerializeField] private GPSLocationService _gpsService;

        private const string Endpoint = "https://apis-navi.kakaomobility.com/v1/directions";

        // ── 생명주기 ────────────────────────────────────────────────────────────

        private void Awake()
        {
            if (_gpsService == null)
                _gpsService = FindAnyObjectByType<GPSLocationService>();
        }

        // ── 공개 API ────────────────────────────────────────────────────────────

        /// <summary>
        /// 카카오 모빌리티 API로 도로 경로를 요청합니다.
        /// onComplete(unityWaypoints, errorMessage) — 성공 시 errorMessage == null
        /// </summary>
        public void RequestRoute(double originLat, double originLon,
                                  double destLat,   double destLon,
                                  Action<Vector3[], string> onComplete)
        {
            if (string.IsNullOrWhiteSpace(_restApiKey))
            {
                onComplete?.Invoke(null, "REST API 키가 설정되지 않았습니다. Inspector에서 _restApiKey를 입력하세요.");
                return;
            }
            StartCoroutine(RequestCoroutine(originLat, originLon, destLat, destLon, onComplete));
        }

        // ── 내부 구현 ────────────────────────────────────────────────────────────

        private IEnumerator RequestCoroutine(double originLat, double originLon,
                                              double destLat,   double destLon,
                                              Action<Vector3[], string> onComplete)
        {
            string url = $"{Endpoint}" +
                         $"?origin={originLon:F6},{originLat:F6}" +
                         $"&destination={destLon:F6},{destLat:F6}" +
                         $"&priority=DISTANCE";

            using var req = UnityWebRequest.Get(url);
            req.SetRequestHeader("Authorization", $"KakaoAK {_restApiKey}");
            req.timeout = _timeoutSeconds;

            yield return req.SendWebRequest();

            if (req.result != UnityWebRequest.Result.Success)
            {
                string msg = req.responseCode switch
                {
                    401 => "인증 실패 — REST API 키를 확인하세요.",
                    403 => "접근 권한 없음 — 카카오 앱 설정을 확인하세요.",
                    404 => "경로를 찾을 수 없습니다.",
                    429 => "요청 한도 초과 — 잠시 후 다시 시도하세요.",
                    _   => $"네트워크 오류 ({req.responseCode}): {req.error}"
                };
                onComplete?.Invoke(null, msg);
                yield break;
            }

            try
            {
                var response = JsonUtility.FromJson<KakaoDirectionsResponse>(req.downloadHandler.text);
                var waypoints = ParseWaypoints(response);

                if (waypoints == null || waypoints.Length < 2)
                {
                    onComplete?.Invoke(null, "유효한 도로 경로 좌표가 없습니다.");
                    yield break;
                }

                Debug.Log($"[KakaoDirections] 도로 경로 {waypoints.Length}개 좌표 수신");
                onComplete?.Invoke(waypoints, null);
            }
            catch (Exception e)
            {
                onComplete?.Invoke(null, $"응답 파싱 오류: {e.Message}");
                Debug.LogError($"[KakaoDirections] 파싱 오류: {e}\n응답: {req.downloadHandler.text}");
            }
        }

        private Vector3[] ParseWaypoints(KakaoDirectionsResponse response)
        {
            if (response?.routes == null || response.routes.Length == 0) return null;

            var route = response.routes[0];
            if (route.result_code != 0)
            {
                Debug.LogWarning($"[KakaoDirections] 경로 없음 (result_code={route.result_code}: {route.result_msg})");
                return null;
            }

            if (route.sections == null) return null;

            var points = new List<Vector3>(256);
            foreach (var section in route.sections)
            {
                if (section.roads == null) continue;
                foreach (var road in section.roads)
                {
                    if (road.vertexes == null || road.vertexes.Length < 2) continue;

                    // vertexes 배열: [경도0, 위도0, 경도1, 위도1, ...]
                    for (int i = 0; i + 1 < road.vertexes.Length; i += 2)
                    {
                        float lon = road.vertexes[i];
                        float lat = road.vertexes[i + 1];
                        if (_gpsService != null)
                            points.Add(_gpsService.ConvertToUnityPosition(lat, lon));
                    }
                }
            }

            return points.Count >= 2 ? points.ToArray() : null;
        }

        // ── JSON 매핑 클래스 ─────────────────────────────────────────────────────

        [Serializable] private class KakaoDirectionsResponse
        {
            public KakaoRoute[] routes;
        }

        [Serializable] private class KakaoRoute
        {
            public int            result_code;
            public string         result_msg;
            public KakaoSection[] sections;
        }

        [Serializable] private class KakaoSection
        {
            public KakaoRoad[] roads;
        }

        [Serializable] private class KakaoRoad
        {
            public float[] vertexes; // [경도0, 위도0, 경도1, 위도1, ...]
        }
    }
}
