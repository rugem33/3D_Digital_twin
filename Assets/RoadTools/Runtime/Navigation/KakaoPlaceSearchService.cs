using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Networking;

namespace Rugem.RoadTools
{
    /// <summary>
    /// 카카오 로컬 API — 키워드 장소 검색 (v2/local/search/keyword.json)
    /// REST API 키는 Inspector에서 입력하세요. 코드에 직접 하드코딩하지 마세요.
    /// </summary>
    public class KakaoPlaceSearchService : MonoBehaviour
    {
        [Header("카카오 API 설정")]
        [Tooltip("developers.kakao.com → 앱 → 앱 키 → REST API 키")]
        [SerializeField] private string _restApiKey = "";

        [Tooltip("현재 위치 기준 검색 반경 (미터). 25000 = 25 km. Kakao API 최대 20 km, 초과분은 클라이언트 필터링.")]
        [SerializeField, Range(500, 25000)] private int _searchRadius = 25000;

        [Tooltip("페이지당 결과 수 (최대 15)")]
        [SerializeField, Range(1, 15)] private int _pageSize = 15;

        [Tooltip("요청 타임아웃 (초)")]
        [SerializeField] private int _timeoutSeconds = 10;

        [Header("의존성")]
        [SerializeField] private GPSLocationService _gpsService;

        private const string Endpoint = "https://dapi.kakao.com/v2/local/search/keyword.json";

        // ── 생명주기 ────────────────────────────────────────────────────────────

        private void Awake()
        {
            if (_gpsService == null)
                _gpsService = FindAnyObjectByType<GPSLocationService>();
        }

        // ── 공개 API ────────────────────────────────────────────────────────────

        /// <summary>
        /// 키워드로 주변 장소를 검색합니다.
        /// onComplete(results, errorMessage) — 성공 시 errorMessage == null
        /// </summary>
        public void Search(string query, Action<List<POIData>, string> onComplete)
        {
            if (string.IsNullOrWhiteSpace(_restApiKey))
            {
                onComplete?.Invoke(null, "REST API 키가 설정되지 않았습니다.\nInspector에서 _restApiKey를 입력하세요.");
                return;
            }

            if (string.IsNullOrWhiteSpace(query))
            {
                onComplete?.Invoke(new List<POIData>(), null);
                return;
            }

            double lat = _gpsService != null ? _gpsService.CurrentLatitude  : 0.0;
            double lon = _gpsService != null ? _gpsService.CurrentLongitude : 0.0;
            StartCoroutine(SearchCoroutine(query.Trim(), lat, lon, onComplete));
        }

        // ── 내부 구현 ────────────────────────────────────────────────────────────

        private IEnumerator SearchCoroutine(string query, double lat, double lon,
                                            Action<List<POIData>, string> onComplete)
        {
            // 위경도가 있으면 거리순 정렬, 없으면 정확도순
            // Kakao API radius 파라미터 최대값은 20000m — 초과분은 ParseDocuments에서 필터링
            string url = $"{Endpoint}?query={UnityWebRequest.EscapeURL(query)}&size={_pageSize}";
            if (lat != 0.0 && lon != 0.0)
            {
                int apiRadius = Mathf.Min(_searchRadius, 20000);
                url += $"&x={lon:F6}&y={lat:F6}&radius={apiRadius}&sort=distance";
            }

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
                    429 => "요청 한도 초과 — 잠시 후 다시 시도하세요.",
                    _   => $"네트워크 오류 ({req.responseCode}): {req.error}"
                };
                onComplete?.Invoke(null, msg);
                yield break;
            }

            try
            {
                var response = JsonUtility.FromJson<KakaoResponse>(req.downloadHandler.text);
                var results  = ParseDocuments(response, _searchRadius);
                onComplete?.Invoke(results, null);
                Debug.Log($"[Kakao] '{query}' 검색 결과: {results.Count}개 (전체 {response?.meta.total_count}개)");
            }
            catch (Exception e)
            {
                onComplete?.Invoke(null, $"응답 파싱 오류: {e.Message}");
                Debug.LogError($"[Kakao] 파싱 오류: {e}\n응답: {req.downloadHandler.text}");
            }
        }

        private static List<POIData> ParseDocuments(KakaoResponse response, int maxDistanceMeters)
        {
            var list = new List<POIData>();
            if (response?.documents == null) return list;

            foreach (var doc in response.documents)
            {
                // distance 필드가 있으면 maxDistanceMeters 초과 결과를 제외
                if (!string.IsNullOrEmpty(doc.distance)
                    && int.TryParse(doc.distance, out int distM)
                    && distM > maxDistanceMeters)
                    continue;

                if (!double.TryParse(doc.y, System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out double docLat)) continue;
                if (!double.TryParse(doc.x, System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out double docLon)) continue;

                list.Add(new POIData(
                    name:      doc.place_name,
                    category:  SimplifyCategory(doc.category_name),
                    latitude:  docLat,
                    longitude: docLon
                ));
            }
            return list;
        }

        /// <summary>
        /// "음식점 > 한식 > 국밥" → "음식점"  /  "IT,과학 > 인터넷" → "IT"
        /// </summary>
        private static string SimplifyCategory(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return "기타";
            string top = raw.Split('>')[0].Trim();
            int comma = top.IndexOf(',');
            return comma > 0 ? top[..comma].Trim() : top;
        }

        // ── JSON 매핑 클래스 ─────────────────────────────────────────────────────

        [Serializable] private class KakaoResponse   { public KakaoDocument[] documents; public KakaoMeta meta; }
        [Serializable] private class KakaoMeta       { public int total_count; public bool is_end; }
        [Serializable] private class KakaoDocument
        {
            public string place_name;
            public string category_name;
            public string x;               // 경도 (longitude)
            public string y;               // 위도 (latitude)
            public string address_name;
            public string road_address_name;
            public string distance;        // 현재 위치에서 거리 (미터, 문자열)
            public string phone;
            public string place_url;
        }
    }
}
