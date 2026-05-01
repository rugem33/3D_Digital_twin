using System;
using System.Collections;
using System.Collections.Generic;
using CesiumForUnity;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Networking;

namespace Rugem.RoadTools
{
    /// <summary>
    /// 카메라 뷰포트의 건물 메쉬에 반투명 건물 이름 레이블을 표시합니다.
    ///
    /// 동작 흐름:
    ///   1. _checkInterval마다 뷰포트 그리드(_gridSize × _gridSize)에서 Raycast
    ///   2. 지면 위 _buildingMinHeight 이상인 히트만 건물로 간주
    ///   3. 히트 위치를 GPS로 변환 → _gpsQuantizeScale 단위로 양자화하여 캐시 키 생성
    ///   4. 미캐시 키 → Kakao coord2address API 요청, 도로명 주소 building_name 획득
    ///   5. OnGUI에서 WorldToScreenPoint 기준 반투명 레이블 렌더링
    /// </summary>
    [AddComponentMenu("RoadTools/Building Label Manager")]
    public class BuildingLabelManager : MonoBehaviour
    {
        [Header("카카오 API 설정")]
        [Tooltip("developers.kakao.com → 앱 → 앱 키 → REST API 키")]
        [SerializeField] private string _restApiKey = "";
        [SerializeField] private int _timeoutSeconds = 10;

        [Header("레이캐스트 설정")]
        [Tooltip("뷰포트를 N×N 격자로 나눠 각 교점에서 레이를 쏩니다.")]
        [SerializeField, Range(3, 14)] private int _gridSize = 7;
        [Tooltip("레이캐스트 반복 간격 (초)")]
        [SerializeField] private float _checkInterval = 2.5f;
        [Tooltip("지면 위 이 높이 미만의 히트는 지면/도로로 간주해 무시합니다 (미터).")]
        [SerializeField] private float _buildingMinHeight = 4.0f;
        [Tooltip("레이캐스트 최대 거리 (미터)")]
        [SerializeField] private float _maxRayDistance = 2000f;
        [Tooltip("건물로 인식할 레이어. 0이면 전체 레이어.")]
        [SerializeField] private LayerMask _buildingLayerMask = ~0;
        [Tooltip("지면 높이 기준을 찾을 레이어입니다. 기본값은 Road + Land입니다.")]
        [SerializeField] private LayerMask _groundLayerMask = (1 << 6) | (1 << 7);

        [Header("GPS 캐시 설정")]
        [Tooltip("GPS 양자화 해상도. 1e-4 ≈ 11m 격자. 클수록 적은 API 호출.")]
        [SerializeField] private float _gpsQuantizeScale = 1e-4f;

        [Header("레이블 표시 설정")]
        [Tooltip("벽면에서 레이블을 카메라 쪽으로 띄울 거리 (미터). z-클리핑 방지용.")]
        [SerializeField] private float _labelWallOffset = 0.4f;
        [Tooltip("이 거리 이상의 건물 레이블은 숨깁니다 (미터)")]
        [SerializeField] private float _labelMaxDistance = 400.0f;
        [Tooltip("스캔에서 사라진 뒤 라벨을 유지할 시간 (초). 카메라 회전 중 깜빡임을 줄입니다.")]
        [SerializeField] private float _labelVisibleGraceSeconds = 1.2f;
        [SerializeField, Range(10, 36)] private int _fontSize = 15;
        [SerializeField] private Color _textColor = new Color(1f, 1f, 1f, 0.92f);
        [SerializeField] private Color _bgColor   = new Color(0f, 0f, 0f, 0.55f);
        [SerializeField] private Vector2 _padding  = new Vector2(7f, 4f);
        [SerializeField] private bool _debugLogs = false;

        [Header("의존성")]
        [SerializeField] private CesiumGeoreference _georeference;

        // ── 내부 상태 ─────────────────────────────────────────────────────────────

        // (latQ, lonQ) → 건물명 (null = 요청 중, "" = 이름 없음)
        private readonly Dictionary<(int, int), string> _nameCache = new();
        // (latQ, lonQ) → 레이블 월드 위치
        private readonly Dictionary<(int, int), Vector3> _labelPos = new();
        private readonly Dictionary<(int, int), float> _lastVisibleTime = new();
        private readonly HashSet<(int, int)> _scanVisibleKeys = new();

        private GUIStyle _labelStyle;
        private GUIStyle _boxStyle;
        private Texture2D _bgTex;

        private float _nextCheckTime;
        private float _groundY = float.NaN;

        private const string ApiEndpoint = "https://dapi.kakao.com/v2/local/geo/coord2address.json";

        // ── 생명주기 ────────────────────────────────────────────────────────────

        private void Awake()
        {
            if (_georeference == null)
                _georeference = FindAnyObjectByType<CesiumGeoreference>();
        }

        private void Update()
        {
            if (Time.time < _nextCheckTime) return;
            _nextCheckTime = Time.time + _checkInterval;
            StartCoroutine(ScanViewport());
        }

        private void OnDestroy()
        {
            if (_bgTex != null) Destroy(_bgTex);
        }

        private void OnGUI()
        {
            Camera cam = Camera.main;
            if (cam == null) return;
            EnsureStyles();

            foreach (var kv in _labelPos)
            {
                if (!_lastVisibleTime.TryGetValue(kv.Key, out float lastSeen)
                    || Time.time - lastSeen > _labelVisibleGraceSeconds)
                    continue;

                if (!_nameCache.TryGetValue(kv.Key, out string name)) continue;
                if (string.IsNullOrEmpty(name)) continue;

                Vector3 screenPos = cam.WorldToScreenPoint(kv.Value);
                if (screenPos.z <= 0f) continue;

                Vector3 viewportPos = cam.WorldToViewportPoint(kv.Value);
                if (viewportPos.x < 0f || viewportPos.x > 1f || viewportPos.y < 0f || viewportPos.y > 1f)
                    continue;

                float dist = Vector3.Distance(cam.transform.position, kv.Value);
                if (dist > _labelMaxDistance) continue;

                DrawLabel(name, screenPos);
            }
        }

        // ── 뷰포트 스캔 ───────────────────────────────────────────────────────────

        private IEnumerator ScanViewport()
        {
            Camera cam = Camera.main;
            if (cam == null) yield break;

            _scanVisibleKeys.Clear();
            UpdateGroundY(cam);

            LayerMask mask = _buildingLayerMask == 0 ? ~0 : _buildingLayerMask;
            bool hasGroundRef = !float.IsNaN(_groundY);
            int hitCount = 0;
            int requestedCount = 0;

            for (int xi = 0; xi < _gridSize; xi++)
            {
                for (int yi = 0; yi < _gridSize; yi++)
                {
                    float vx = (xi + 0.5f) / _gridSize;
                    float vy = (yi + 0.5f) / _gridSize;
                    Ray ray = cam.ViewportPointToRay(new Vector3(vx, vy, 0f));

                    if (!Physics.Raycast(ray, out RaycastHit hit, _maxRayDistance, mask)) continue;
                    if (hasGroundRef && hit.point.y < _groundY + _buildingMinHeight) continue;

                    // 지붕(법선이 local-up 방향)은 제외 — 벽면만 레이블
                    Vector3 localUp = GetLocalUp(hit.point);
                    if (Vector3.Dot(hit.normal, localUp) > 0.5f) continue;

                    hitCount++;

                    (int, int) key = ToGpsKey(hit.point);
                    _scanVisibleKeys.Add(key);
                    _lastVisibleTime[key] = Time.time;

                    // 히트 포인트에서 벽면 법선 방향(카메라 쪽)으로 살짝 띄움
                    _labelPos[key] = hit.point + hit.normal * _labelWallOffset;

                    if (_nameCache.ContainsKey(key)) continue;

                    string persisted = LoadCachedBuildingName(key);
                    if (persisted != null)
                    {
                        _nameCache[key] = persisted;
                        continue;
                    }

                    _nameCache[key] = null; // 요청 중 표시
                    double3 llh = UnityToLonLatHeight(hit.point);
                    StartCoroutine(FetchBuildingName(key, llh.y, llh.x));
                    requestedCount++;
                }
                yield return null; // 프레임 분산
            }

            PruneInactiveLabels();
            if (_debugLogs)
                Debug.Log($"[BuildingLabel] scan hits={hitCount}, labels={_labelPos.Count}, requests={requestedCount}, ground={(hasGroundRef ? _groundY.ToString("F1") : "none")}");
        }

        private void UpdateGroundY(Camera cam)
        {
            Vector3 localUp = GetLocalUp(cam.transform.position);
            Vector3 origin  = cam.transform.position + localUp * 500f;
            LayerMask mask = _groundLayerMask == 0 ? ~_buildingLayerMask : _groundLayerMask;
            if (Physics.Raycast(origin, -localUp, out RaycastHit hit, 2000f, mask))
                _groundY = hit.point.y;
            else
                _groundY = float.NaN;
        }

        private Vector3 GetLocalUp(Vector3 unityPos)
        {
            if (_georeference == null) return Vector3.up;
            Unity.Mathematics.double3 ecef = _georeference.TransformUnityPositionToEarthCenteredEarthFixed(
                new Unity.Mathematics.double3(unityPos.x, unityPos.y, unityPos.z));
            Unity.Mathematics.double3 ecefUp  = Unity.Mathematics.math.normalize(ecef);
            Unity.Mathematics.double3 unityUp = _georeference.TransformEarthCenteredEarthFixedDirectionToUnity(ecefUp);
            return ((Vector3)(Unity.Mathematics.float3)unityUp).normalized;
        }

        private void PruneInactiveLabels()
        {
            float expireBefore = Time.time - _labelVisibleGraceSeconds;
            var removeKeys = new List<(int, int)>();

            foreach (var kv in _lastVisibleTime)
            {
                if (kv.Value < expireBefore)
                    removeKeys.Add(kv.Key);
            }

            foreach (var key in removeKeys)
            {
                _lastVisibleTime.Remove(key);
                _labelPos.Remove(key);
            }
        }

        // ── Kakao coord2address API ───────────────────────────────────────────────

        private IEnumerator FetchBuildingName((int, int) key, double lat, double lon)
        {
            string apiKey = KakaoApiKeyProvider.Resolve(_restApiKey);
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                _nameCache[key] = "";
                if (_debugLogs)
                    Debug.LogWarning("[BuildingLabel] Kakao REST API key missing. Check Assets/Resources/kakao_api_key.txt.");
                yield break;
            }

            string url = $"{ApiEndpoint}?x={lon:F6}&y={lat:F6}";
            using var req = UnityWebRequest.Get(url);
            req.SetRequestHeader("Authorization", $"KakaoAK {apiKey}");
            req.timeout = _timeoutSeconds;

            yield return req.SendWebRequest();

            if (req.result != UnityWebRequest.Result.Success)
            {
                _nameCache[key] = "";
                if (_debugLogs)
                    Debug.LogWarning($"[BuildingLabel] Kakao coord2address failed ({req.responseCode}): {req.error}");
                yield break;
            }

            try
            {
                var resp = JsonUtility.FromJson<Coord2AddrResponse>(req.downloadHandler.text);
                string building = (resp?.documents != null && resp.documents.Length > 0)
                    ? resp.documents[0].road_address?.building_name ?? ""
                    : "";
                _nameCache[key] = building;
                SaveCachedBuildingName(key, building);
                if (_debugLogs)
                    Debug.Log($"[BuildingLabel] fetched '{building}' at {lat:F6},{lon:F6}");
            }
            catch
            {
                _nameCache[key] = "";
                SaveCachedBuildingName(key, "");
                if (_debugLogs)
                    Debug.LogWarning("[BuildingLabel] Kakao coord2address response parse failed.");
            }
        }

        private static string CacheKey((int, int) key) => $"BuildingLabelCache_v2_{key.Item1}_{key.Item2}";

        private static string LoadCachedBuildingName((int, int) key)
        {
            string prefKey = CacheKey(key);
            return PlayerPrefs.HasKey(prefKey) ? PlayerPrefs.GetString(prefKey, "") : null;
        }

        private static void SaveCachedBuildingName((int, int) key, string name)
        {
            PlayerPrefs.SetString(CacheKey(key), name ?? "");
            PlayerPrefs.Save();
        }

        // ── 좌표 변환 ────────────────────────────────────────────────────────────

        private (int, int) ToGpsKey(Vector3 worldPos)
        {
            double3 llh = UnityToLonLatHeight(worldPos);
            int latQ = (int)Math.Round(llh.y / _gpsQuantizeScale);
            int lonQ = (int)Math.Round(llh.x / _gpsQuantizeScale);
            return (latQ, lonQ);
        }

        private double3 UnityToLonLatHeight(Vector3 worldPos)
        {
            if (_georeference == null) return double3.zero;
            double3 ecef = _georeference.TransformUnityPositionToEarthCenteredEarthFixed(
                new double3(worldPos.x, worldPos.y, worldPos.z));
            return CesiumWgs84Ellipsoid.EarthCenteredEarthFixedToLongitudeLatitudeHeight(ecef);
        }

        // ── OnGUI 레이블 렌더링 ─────────────────────────────────────────────────

        private void EnsureStyles()
        {
            if (_labelStyle != null) return;

            _bgTex = new Texture2D(2, 2);
            var pix = new Color[4];
            for (int i = 0; i < 4; i++) pix[i] = _bgColor;
            _bgTex.SetPixels(pix);
            _bgTex.Apply();

            _boxStyle = new GUIStyle(GUI.skin.box);
            _boxStyle.normal.background = _bgTex;
            _boxStyle.border = new RectOffset(0, 0, 0, 0);

            _labelStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize  = _fontSize,
                alignment = TextAnchor.MiddleCenter,
                wordWrap  = false
            };
            _labelStyle.normal.textColor = _textColor;
        }

        private void DrawLabel(string text, Vector3 screenPos)
        {
            float guiY = Screen.height - screenPos.y;

            GUIContent content = new GUIContent(text);
            Vector2 textSize = _labelStyle.CalcSize(content);
            float w = textSize.x + _padding.x * 2f;
            float h = textSize.y + _padding.y * 2f;

            var bgRect   = new Rect(screenPos.x - w * 0.5f, guiY - h * 0.5f, w, h);
            var textRect = new Rect(bgRect.x + _padding.x, bgRect.y + _padding.y, textSize.x, textSize.y);

            GUI.Box(bgRect, GUIContent.none, _boxStyle);
            GUI.Label(textRect, content, _labelStyle);
        }

        // ── JSON 매핑 ────────────────────────────────────────────────────────────

        [Serializable] private class Coord2AddrResponse { public AddrDocument[] documents; }
        [Serializable] private class AddrDocument { public RoadAddr road_address; }
        [Serializable] private class RoadAddr { public string building_name; }
    }
}
