using System.Collections.Generic;
using UnityEngine;

namespace Rugem.RoadTools
{
    /// <summary>
    /// 길찾기 UI 컨트롤러 (OnGUI 기반)
    /// 상태: None → SearchOpen → MapOverview → Navigating → Arrived
    ///
    /// 화면 배치:
    ///   하단 중앙    : 길찾기 버튼            (None)
    ///   하단 풀패널  : 검색 오버레이          (SearchOpen)
    ///   전체화면     : 경로 확인 지도 오버뷰  (MapOverview)
    ///   하단 바      : 목적지 정보 + 버튼     (Navigating)
    ///   화면 중앙    : 도착 알림             (Arrived)
    /// </summary>
    public class NavigationUIController : MonoBehaviour
    {
        [Header("의존성")]
        [SerializeField] private NavigationService         _navService;
        [SerializeField] private RouteRenderer             _routeRenderer;
        [SerializeField] private GPSLocationService        _gpsService;
        [SerializeField] private KakaoPlaceSearchService   _kakaoSearch;
        [SerializeField] private MinimapController         _minimapController;
        [Tooltip("카메라 수직 하방 지형 지점 앵커 (mainCameraNav). 없으면 Camera.main 위치 폴백.")]
        [SerializeField] private CameraNavAnchor           _navAnchor;

        [Header("UI 설정")]
        [Tooltip("검색 패널이 덮는 화면 비율 (0~1)")]
        [SerializeField, Range(0.4f, 0.85f)] private float _searchPanelHeightRatio = 0.65f;
        [Tooltip("도착 알림 표시 시간 (초)")]
        [SerializeField] private float _arrivedDisplayDuration = 3.5f;

        // ── 내부 상태 ────────────────────────────────────────────────────────
        private enum NavUIState { None, SearchOpen, MapOverview, Navigating, Arrived }
        private NavUIState _state = NavUIState.None;

        private string          _searchQuery      = "";
        private List<POIData>   _searchResults    = new();
        private List<POIData>   _recentSearches   = new();
        private bool            _showingRecents   = true;
        private Vector2         _scrollPos;
        private bool            _isDraggingSearchList;
        private float           _lastSearchDragY;
        private float           _searchDragDistance;
        private float           _arrivedTimer;
        private bool            _focusSearchOnce;
        private bool            _isSearching;
        private string          _searchError;
        private bool            _hasPendingSuggestionSearch;
        private float           _lastSearchInputChangeTime;
        private int             _searchRequestVersion;
        private Rect            _overviewWorldBounds; // XZ 범위 (다이어그램 모드용)

        private const int    MaxRecentSearches = 10;
        private const int    MaxSuggestionResults = 10;
        private const int    SuggestionSearchRadiusMeters = 2000;
        private const float  SuggestionSearchDelaySeconds = 0.35f;
        private const string PrefKeyCount      = "NavRecent_Count";
        private const string PrefKeyPOI        = "NavRecent_";

        // ── GUI 스타일 ────────────────────────────────────────────────────────
        private GUIStyle _styleMainBtn;
        private GUIStyle _styleSecondaryBtn;
        private GUIStyle _styleDangerBtn;
        private GUIStyle _stylePanelTitle;
        private GUIStyle _styleInfoLabel;
        private GUIStyle _styleDistLabel;
        private GUIStyle _styleResultBtn;
        private GUIStyle _styleArrivedMsg;
        private GUIStyle _styleSearchField;
        private GUIStyle _styleErrorLabel;
        private GUIStyle _styleMapTitle;
        private GUIStyle _styleMapDestName;
        private bool     _stylesReady;
        private int      _styleScreenWidth;
        private int      _styleScreenHeight;

        // ── 오버뷰 마커 텍스처 ────────────────────────────────────────────────
        private Texture2D _playerMarkerTex;
        private Texture2D _destMarkerTex;

        // ── 생명주기 ─────────────────────────────────────────────────────────

        private void Awake()
        {
            ResolveDependencies();
        }

        private void ResolveDependencies()
        {
            if (_navService == null)
                _navService = FindAnyObjectByType<NavigationService>();
            if (_gpsService == null)
                _gpsService = FindAnyObjectByType<GPSLocationService>();
            if (_routeRenderer == null)
                _routeRenderer = FindAnyObjectByType<RouteRenderer>();
            if (_kakaoSearch == null)
                _kakaoSearch = FindAnyObjectByType<KakaoPlaceSearchService>();
            if (_minimapController == null)
                _minimapController = FindAnyObjectByType<MinimapController>();
            if (_navAnchor == null)
                _navAnchor = FindAnyObjectByType<CameraNavAnchor>();
        }

        private void OnEnable()
        {
            ResolveDependencies();
            if (_navService == null) return;
            _navService.OnRouteCalculated   += HandleRouteCalculated;
            _navService.OnNavigationCleared += HandleNavigationCleared;
            _navService.OnArrived           += HandleArrived;
        }

        private void OnDisable()
        {
            if (_navService == null) return;
            _navService.OnRouteCalculated   -= HandleRouteCalculated;
            _navService.OnNavigationCleared -= HandleNavigationCleared;
            _navService.OnArrived           -= HandleArrived;
        }

        private void OnDestroy()
        {
            if (_playerMarkerTex != null) Destroy(_playerMarkerTex);
            if (_destMarkerTex   != null) Destroy(_destMarkerTex);
        }

        private void Update()
        {
            UpdateSuggestionSearch();

            if (_state == NavUIState.Arrived)
            {
                _arrivedTimer -= Time.deltaTime;
                if (_arrivedTimer <= 0f)
                    TransitionTo(NavUIState.None);
            }

            if (_state == NavUIState.Navigating && _routeRenderer != null)
            {
                // mainCameraNav(지형 표면 정사영)를 경로 트리밍 기준점으로 사용
                // 없으면 Camera.main → GPS 순서로 폴백
                Vector3 navPos = _navAnchor?.NavTransform?.position
                    ?? (Camera.main != null
                        ? Camera.main.transform.position
                        : (_gpsService != null ? _gpsService.SmoothedUnityPosition : Vector3.zero));
                _routeRenderer.TrimFromPlayerPosition(navPos);
            }
        }

        // ── OnGUI ────────────────────────────────────────────────────────────

        private void OnGUI()
        {
            EnsureStyles();

            switch (_state)
            {
                case NavUIState.None:        DrawSearchButton();  break;
                case NavUIState.SearchOpen:  DrawSearchPanel();   break;
                case NavUIState.MapOverview: DrawMapOverview();   break;
                case NavUIState.Navigating:  DrawNavigationBar(); break;
                case NavUIState.Arrived:     DrawArrivedOverlay(); break;
            }
        }

        // ── None 상태 — 하단 중앙 "길찾기" 버튼 ─────────────────────────────

        private void DrawSearchButton()
        {
            float margin  = Mathf.Clamp(Screen.width * 0.03f, 14f, 28f);
            float mapSize = Screen.height * MinimapController.MapSizeRatioConst;
            float mapX    = Screen.width - mapSize - margin;

            float btnW = Mathf.Clamp(Screen.width * 0.26f, 116f, 190f);
            float btnH = Mathf.Clamp(Screen.height * 0.056f, 44f, 60f);
            float gap  = Mathf.Clamp(Screen.width * 0.012f, 8f, 14f);
            float btnX = Mathf.Max(margin, mapX - btnW - gap);
            float btnY = margin;

            DrawShadowedRect(new Rect(btnX - 2, btnY - 2, btnW + 4, btnH + 4), new Color(0, 0, 0, 0.6f));
            if (GUI.Button(new Rect(btnX, btnY, btnW, btnH), "길찾기", _styleMainBtn))
                OpenSearch();
        }

        // ── SearchOpen 상태 — 검색 오버레이 패널 ────────────────────────────

        private void DrawSearchPanel()
        {
            GUI.color = new Color(0, 0, 0, 0.55f);
            GUI.DrawTexture(new Rect(0, 0, Screen.width, Screen.height), Texture2D.whiteTexture);
            GUI.color = Color.white;

            float margin  = Mathf.Clamp(Screen.width * 0.04f, 16f, 34f);
            float panelW  = Screen.width  - margin * 2f;
            float panelH  = Screen.height * _searchPanelHeightRatio;
            float panelX  = margin;
            float panelY  = Screen.height - panelH - margin;

            DrawShadowedRect(new Rect(panelX, panelY, panelW, panelH), new Color(0.08f, 0.10f, 0.18f, 0.97f));

            float pad     = Mathf.Clamp(margin * 0.6f, 10f, 20f);
            float inner   = panelX + pad;
            float innerW  = panelW - pad * 2f;
            float fieldH  = Mathf.Clamp(Screen.height * 0.072f, 52f, 72f);
            float closeSz = Mathf.Clamp(fieldH * 0.86f, 40f, 54f);

            GUI.Label(new Rect(inner, panelY + pad * 0.5f, innerW - closeSz - pad, fieldH * 0.7f),
                "목적지 검색", _stylePanelTitle);

            if (GUI.Button(new Rect(panelX + panelW - closeSz - pad, panelY + pad * 0.45f, closeSz, closeSz),
                "✕", _styleDangerBtn))
            {
                CloseSearch();
                return;
            }

            float fieldY     = panelY + pad + fieldH * 0.7f + pad * 0.3f;
            float searchBtnW = Mathf.Clamp(innerW * 0.24f, 88f, 134f);
            float fieldActW  = innerW - searchBtnW - pad * 0.4f;

            if (_focusSearchOnce && Event.current.type == EventType.Layout)
            {
                GUI.FocusControl("SearchField");
                _focusSearchOnce = false;
            }

            GUI.SetNextControlName("SearchField");
            string newQuery = GUI.TextField(
                new Rect(inner, fieldY, fieldActW, fieldH),
                _searchQuery, _styleSearchField);
            if (newQuery != _searchQuery)
            {
                _searchQuery = newQuery;
                QueueSuggestionSearch();
            }

            if (Event.current.type == EventType.KeyDown && Event.current.keyCode == KeyCode.Return
                && !_isSearching && !string.IsNullOrWhiteSpace(_searchQuery))
            {
                StartKakaoSearch();
                Event.current.Use();
            }

            bool searchBtnPressed = GUI.Button(
                new Rect(inner + fieldActW + pad * 0.4f, fieldY, searchBtnW, fieldH),
                _isSearching ? "..." : "검색", _styleMainBtn);
            if (searchBtnPressed && !_isSearching)
                StartKakaoSearch();

            float countY = fieldY + fieldH + pad * 0.3f;
            if (_isSearching)
            {
                GUI.Label(new Rect(inner, countY, innerW, fieldH * 0.55f), "검색 중...", _styleDistLabel);
            }
            else if (_searchError != null)
            {
                GUI.Label(new Rect(inner, countY, innerW, fieldH * 0.65f), _searchError, _styleErrorLabel);
            }
            else if (_showingRecents)
            {
                if (_recentSearches.Count > 0)
                    GUI.Label(new Rect(inner, countY, innerW, fieldH * 0.55f), "최근 검색", _styleDistLabel);
                // 최근 검색 내역 없으면 공란 — 아무것도 표시하지 않음
            }
            else
            {
                string countText = _searchResults.Count > 0
                    ? $"{_searchResults.Count}개 연관검색어"
                    : "연관검색어 없음";
                GUI.Label(new Rect(inner, countY, innerW, fieldH * 0.55f), countText, _styleDistLabel);
            }

            var displayList  = _showingRecents ? _recentSearches : _searchResults;
            bool isRecentList = _showingRecents;

            float listY   = countY + fieldH * 0.6f;
            float listH   = Mathf.Max(40f, panelY + panelH - listY - pad);
            float itemH   = Mathf.Clamp(Screen.height * 0.072f, 56f, 76f);
            float gap     = Mathf.Clamp(pad * 0.25f, 3f, 6f);
            float totalH  = displayList.Count * (itemH + gap);

            Rect viewRect    = new Rect(inner, listY, innerW, listH);
            Rect contentRect = new Rect(0, 0, innerW - 22f, Mathf.Max(totalH, listH + 1f));

            HandleSearchListScroll(viewRect, contentRect);

            _scrollPos = GUI.BeginScrollView(viewRect, _scrollPos, contentRect, false, true);
            {
                float iy = 0f;
                foreach (var poi in displayList)
                {
                    DrawResultItem(poi, new Rect(0, iy, contentRect.width, itemH), isRecentList);
                    iy += itemH + gap;
                }
            }
            GUI.EndScrollView();
        }

        private void HandleSearchListScroll(Rect viewRect, Rect contentRect)
        {
            float maxScrollY = Mathf.Max(0f, contentRect.height - viewRect.height);
            if (maxScrollY <= 0f)
            {
                _scrollPos = Vector2.zero;
                _isDraggingSearchList = false;
                _searchDragDistance = 0f;
                return;
            }

            Event e = Event.current;
            Vector2 mouse = e.mousePosition;

            if (e.type == EventType.ScrollWheel && viewRect.Contains(mouse))
            {
                _scrollPos.y = Mathf.Clamp(_scrollPos.y + e.delta.y * 18f, 0f, maxScrollY);
                e.Use();
                return;
            }

            if (e.type == EventType.MouseDown && e.button == 0 && viewRect.Contains(mouse))
            {
                _isDraggingSearchList = true;
                _lastSearchDragY = mouse.y;
                _searchDragDistance = 0f;
            }
            else if (e.type == EventType.MouseDrag && _isDraggingSearchList)
            {
                float deltaY = _lastSearchDragY - mouse.y;
                _lastSearchDragY = mouse.y;
                _searchDragDistance += Mathf.Abs(deltaY);
                _scrollPos.y = Mathf.Clamp(_scrollPos.y + deltaY, 0f, maxScrollY);
                e.Use();
            }
            else if (e.type == EventType.MouseUp && e.button == 0)
            {
                _isDraggingSearchList = false;
            }
        }

        private void DrawResultItem(POIData poi, Rect rect, bool isRecent = false)
        {
            bool clicked = GUI.Button(rect, "", _styleResultBtn);
            if (clicked && _searchDragDistance <= 8f)
                SelectDestination(poi);
            if (clicked)
                _searchDragDistance = 0f;

            float sidePad    = Mathf.Clamp(rect.height * 0.18f, 10f, 14f);
            float nameLabelW = rect.width * 0.66f;
            GUI.Label(new Rect(rect.x + sidePad, rect.y + rect.height * 0.15f, nameLabelW, rect.height * 0.7f),
                poi.name, _styleInfoLabel);

            float tagW   = Mathf.Clamp(rect.width * 0.24f, 72f, 122f);
            var   tagClr = isRecent
                ? new Color(0.45f, 0.30f, 0.70f, 0.85f)  // 최근: 보라색
                : new Color(0.20f, 0.55f, 1.00f, 0.85f);  // 검색결과: 파란색
            string tagText = isRecent ? "최근" : CompactCategory(poi.category);

            GUI.color = tagClr;
            GUI.DrawTexture(new Rect(rect.x + rect.width - tagW - 4f,
                rect.y + rect.height * 0.2f, tagW, rect.height * 0.58f), Texture2D.whiteTexture);
            GUI.color = Color.white;
            GUI.Label(new Rect(rect.x + rect.width - tagW - 4f,
                rect.y + rect.height * 0.18f, tagW, rect.height * 0.62f),
                tagText, _styleDistLabel);
        }

        // ── MapOverview 상태 — 경로 확인 전체화면 지도 ──────────────────────

        private void DrawMapOverview()
        {
            // 배경 오버레이
            GUI.color = new Color(0.04f, 0.06f, 0.14f, 0.97f);
            GUI.DrawTexture(new Rect(0, 0, Screen.width, Screen.height), Texture2D.whiteTexture);
            GUI.color = Color.white;

            float margin = Mathf.Clamp(Screen.width * 0.04f, 16f, 34f);

            // ── 타이틀 ──────────────────────────────────────────────────────
            float titleH = Mathf.Clamp(Screen.height * 0.07f, 44f, 72f);
            GUI.Label(new Rect(0, margin * 0.5f, Screen.width, titleH), "경로 확인", _styleMapTitle);

            // ── 지도 패널 ────────────────────────────────────────────────────
            float btnH = Mathf.Clamp(Screen.height * 0.082f, 54f, 78f);
            float bannerH = Mathf.Clamp(Screen.height * 0.052f, 34f, 52f);
            float availableMapH = Screen.height - titleH - btnH - bannerH - margin * 3.2f;
            float mapSize = Mathf.Min(Screen.width * 0.92f, Mathf.Max(180f, availableMapH));
            float mapX    = (Screen.width - mapSize) * 0.5f;
            float mapY    = margin * 0.5f + titleH + margin * 0.3f;
            var   mapRect = new Rect(mapX, mapY, mapSize, mapSize);

            // 테두리
            GUI.color = new Color(0.2f, 0.45f, 0.8f, 0.9f);
            GUI.DrawTexture(new Rect(mapX - 2, mapY - 2, mapSize + 4, mapSize + 4), Texture2D.whiteTexture);
            GUI.color = Color.white;

            // ── 지도 배경 ────────────────────────────────────────────────────
            var rt = _minimapController?.OverviewTexture;
            if (rt != null)
            {
                GUI.DrawTexture(mapRect, rt, ScaleMode.ScaleToFit, false);
            }
            else
            {
                // 미니맵 없음 — 격자 다이어그램 배경
                GUI.color = new Color(0.10f, 0.14f, 0.22f, 1f);
                GUI.DrawTexture(mapRect, Texture2D.whiteTexture);
                GUI.color = new Color(1f, 1f, 1f, 0.05f);
                for (int gi = 1; gi < 6; gi++)
                {
                    float t = gi / 6f;
                    GUI.DrawTexture(new Rect(mapRect.x + mapRect.width * t, mapRect.y, 1f, mapRect.height), Texture2D.whiteTexture);
                    GUI.DrawTexture(new Rect(mapRect.x, mapRect.y + mapRect.height * t, mapRect.width, 1f), Texture2D.whiteTexture);
                }
                GUI.color = Color.white;
            }

            // ── 경로 선 ──────────────────────────────────────────────────────
            var route = _navService?.CurrentRoute;
            if (route != null && route.Length >= 2)
            {
                float lw = Mathf.Max(mapSize * 0.010f, 3f);
                for (int i = 0; i < route.Length - 1; i++)
                {
                    Vector2 a = GetMapPos(route[i],     mapRect);
                    Vector2 b = GetMapPos(route[i + 1], mapRect);
                    DrawGUILine(a, b, lw + 2f, new Color(0f, 0f, 0f, 0.5f));   // 그림자
                    DrawGUILine(a, b, lw,       new Color(0.0f, 0.65f, 1.0f, 0.95f));
                }
            }

            // ── 내 위치 마커 (파란 원) ────────────────────────────────────────
            if (_gpsService != null && _playerMarkerTex != null)
            {
                Vector2 pm = GetMapPos(_gpsService.SmoothedUnityPosition, mapRect);
                float   sz = mapSize * 0.055f;
                DrawMapMarker(pm, sz + 4f, Color.black);          // 외곽선
                DrawMapMarker(pm, sz, _playerMarkerTex);
            }

            // ── 목적지 마커 (주황 원 + 이름 라벨) ────────────────────────────
            if (_navService?.CurrentDestination != null && _destMarkerTex != null)
            {
                Vector2 dm = GetMapPos(_navService.DestinationWorldPos, mapRect);
                float   sz = mapSize * 0.065f;
                DrawMapMarker(dm, sz + 4f, Color.black);           // 외곽선
                DrawMapMarker(dm, sz, _destMarkerTex);

                // 목적지 이름 라벨 (마커 위)
                float lblW = mapSize * 0.55f;
                float lblH = Screen.height * 0.030f;
                GUI.color = Color.white;
                GUI.Label(new Rect(dm.x - lblW * 0.5f, dm.y - sz * 0.5f - lblH - 2f, lblW, lblH),
                    _navService.CurrentDestination.name, _styleMapDestName);
            }

            // ── N 방향 표시 ──────────────────────────────────────────────────
            GUI.color = Color.white;
            GUI.Label(new Rect(mapX + 6f, mapY + 4f, 30f, 30f), "N", _styleMapTitle);

            // ── 하단 목적지 배너 ─────────────────────────────────────────────
            string destName = _navService?.CurrentDestination?.name ?? "";
            float  bannerY  = mapY + mapSize + margin * 0.4f;
            GUI.Label(new Rect(margin, bannerY, Screen.width - margin * 2f, bannerH),
                $"목적지: {destName}", _styleMapDestName);

            // ── 버튼 (취소 | 결정) ───────────────────────────────────────────
            float btnW = (Screen.width - margin * 3f) * 0.5f;
            float btnY = Screen.height - btnH - margin;

            if (GUI.Button(new Rect(margin, btnY, btnW, btnH), "취소", _styleDangerBtn))
            {
                _minimapController?.ExitOverviewMode();
                _navService?.ClearNavigation();
                TransitionTo(NavUIState.None);
            }

            if (GUI.Button(new Rect(margin * 2f + btnW, btnY, btnW, btnH), "결정", _styleMainBtn))
            {
                _minimapController?.ExitOverviewMode();
                TransitionTo(NavUIState.Navigating);
            }
        }

        // ── Navigating 상태 — 하단 정보 바 + 버튼 ───────────────────────────

        private void DrawNavigationBar()
        {
            float margin = Mathf.Clamp(Screen.width * 0.04f, 16f, 34f);
            float btnH   = Mathf.Clamp(Screen.height * 0.078f, 52f, 76f);
            float barH   = btnH + Mathf.Clamp(Screen.height * 0.095f, 64f, 96f);
            float barY   = Screen.height - barH - margin;
            float barW   = Screen.width  - margin * 2f;

            DrawShadowedRect(new Rect(margin, barY, barW, barH), new Color(0.06f, 0.09f, 0.16f, 0.95f));

            float pad    = Mathf.Clamp(margin * 0.55f, 10f, 18f);
            float innerX = margin + pad;
            float innerW = barW  - pad * 2f;

            string name = _navService.CurrentDestination?.name ?? "목적지";
            GUI.Label(new Rect(innerX, barY + pad * 0.5f, innerW, btnH * 0.65f),
                $"목적지:  {name}", _stylePanelTitle);

            float dist    = _navService.DistanceToDestination;
            string distStr = dist >= 0f ? FormatDistance(dist) : "계산 중...";
            GUI.Label(new Rect(innerX, barY + btnH * 0.62f, innerW * 0.55f, btnH * 0.55f),
                distStr, _styleDistLabel);

            float btnY  = barY + barH - btnH - pad * 0.5f;
            float halfW = (innerW - pad) * 0.5f;

            if (GUI.Button(new Rect(innerX, btnY, halfW, btnH), "여기로 이동", _styleMainBtn))
                OnClickMoveToDestination();

            if (GUI.Button(new Rect(innerX + halfW + pad, btnY, halfW, btnH), "취소", _styleDangerBtn))
                OnClickCancelNavigation();

            float reSearchW = Mathf.Clamp(Screen.width * 0.18f, 78f, 124f);
            float reSearchH = Mathf.Clamp(Screen.height * 0.052f, 38f, 52f);
            if (GUI.Button(new Rect(
                (Screen.width - barW) * 0.5f + barW - reSearchW - pad,
                barY - reSearchH - pad * 0.3f,
                reSearchW, reSearchH), "재검색", _styleSecondaryBtn))
                OpenSearch();
        }

        // ── Arrived 상태 — 도착 알림 ────────────────────────────────────────

        private void DrawArrivedOverlay()
        {
            float boxW = Mathf.Min(Mathf.Clamp(Screen.width * 0.70f, 260f, 560f), Screen.width - 32f);
            float boxH = Mathf.Clamp(Screen.height * 0.18f, 120f, 180f);
            float boxX = (Screen.width  - boxW) * 0.5f;
            float boxY = (Screen.height - boxH) * 0.45f;

            GUI.color = new Color(0, 0, 0, 0.50f);
            GUI.DrawTexture(new Rect(0, 0, Screen.width, Screen.height), Texture2D.whiteTexture);

            DrawShadowedRect(new Rect(boxX, boxY, boxW, boxH), new Color(0.05f, 0.55f, 0.20f, 0.97f));
            GUI.color = Color.white;
            GUI.Label(new Rect(boxX, boxY, boxW, boxH), "도착!", _styleArrivedMsg);

            string name = _navService.CurrentDestination?.name ?? "";
            if (!string.IsNullOrEmpty(name))
            {
                float subH = Screen.height * 0.045f;
                GUI.Label(new Rect(boxX, boxY + boxH * 0.52f, boxW, subH),
                    name + "에 도착했습니다", _styleDistLabel);
            }

            float progress = 1f - (_arrivedTimer / _arrivedDisplayDuration);
            float barW  = boxW * 0.7f;
            float barH2 = Screen.height * 0.008f;
            float barX  = boxX + (boxW - barW) * 0.5f;
            float barY2 = boxY + boxH - barH2 - Screen.height * 0.012f;
            GUI.color = new Color(1, 1, 1, 0.3f);
            GUI.DrawTexture(new Rect(barX, barY2, barW, barH2), Texture2D.whiteTexture);
            GUI.color = Color.white;
            GUI.DrawTexture(new Rect(barX, barY2, barW * progress, barH2), Texture2D.whiteTexture);
        }

        // ── 이벤트 핸들러 ────────────────────────────────────────────────────

        private void HandleRouteCalculated(POIData poi, Vector3[] route)
        {
            _routeRenderer?.ShowRoute(route);
        }

        private void HandleNavigationCleared()
        {
            _routeRenderer?.HideRoute();
            if (_state == NavUIState.Navigating || _state == NavUIState.MapOverview)
                TransitionTo(NavUIState.None);
        }

        private void HandleArrived()
        {
            _routeRenderer?.HideRoute();
            _arrivedTimer = _arrivedDisplayDuration;
            TransitionTo(NavUIState.Arrived);
        }

        // ── UI 동작 ──────────────────────────────────────────────────────────

        private void OpenSearch()
        {
            _searchQuery     = "";
            _searchResults   = new List<POIData>();
            _scrollPos       = Vector2.zero;
            _focusSearchOnce = true;
            _isSearching     = false;
            _searchError     = null;
            _hasPendingSuggestionSearch = false;
            _searchRequestVersion++;
            _showingRecents  = true;
            LoadRecentSearches();
            TransitionTo(NavUIState.SearchOpen);
        }

        private void StartKakaoSearch()
        {
            string query = _searchQuery.Trim();
            if (string.IsNullOrWhiteSpace(query)) return;

            _hasPendingSuggestionSearch = false;
            _showingRecents = false;

            if (_kakaoSearch == null)
            {
                _searchResults = new List<POIData>();
                _scrollPos     = Vector2.zero;
                return;
            }

            _isSearching = true;
            _searchError = null;
            int requestVersion = ++_searchRequestVersion;
            _kakaoSearch.Search(query, MaxSuggestionResults, SuggestionSearchRadiusMeters, (results, error) =>
            {
                if (this == null || !isActiveAndEnabled) return;
                if (requestVersion != _searchRequestVersion) return;

                _isSearching = false;
                if (error != null)
                {
                    _searchError   = error;
                    _searchResults = new List<POIData>();
                    Debug.LogWarning($"[NavUI] Kakao 검색 실패: {error}");
                    return;
                }
                _searchResults = results ?? new List<POIData>();
                _scrollPos     = Vector2.zero;
            });
        }

        private void QueueSuggestionSearch()
        {
            string query = _searchQuery.Trim();

            _searchRequestVersion++;
            _searchError = null;
            _scrollPos = Vector2.zero;
            _isSearching = false;

            if (string.IsNullOrWhiteSpace(query))
            {
                _hasPendingSuggestionSearch = false;
                _isSearching = false;
                _searchResults = new List<POIData>();
                _showingRecents = true;
                return;
            }

            _showingRecents = false;
            _searchResults = new List<POIData>();
            _hasPendingSuggestionSearch = true;
            _lastSearchInputChangeTime = Time.unscaledTime;
        }

        private void UpdateSuggestionSearch()
        {
            if (_state != NavUIState.SearchOpen || !_hasPendingSuggestionSearch)
                return;

            if (Time.unscaledTime - _lastSearchInputChangeTime < SuggestionSearchDelaySeconds)
                return;

            StartKakaoSearch();
        }

        private void CloseSearch()
        {
            TransitionTo(_navService != null && _navService.IsNavigating
                ? NavUIState.Navigating
                : NavUIState.None);
        }

        /// <summary>
        /// 검색 결과에서 목적지를 선택하면 경로를 계산하고 MapOverview 상태로 전환합니다.
        /// MinimapController가 없으면 벡터 다이어그램으로 경로를 표시합니다.
        /// </summary>
        private void SelectDestination(POIData poi)
        {
            if (poi == null)
                return;

            ResolveDependencies();
            if (_navService == null)
            {
                Debug.LogWarning("[NavUI] NavigationService가 없어 목적지를 설정할 수 없습니다.");
                return;
            }

            AddToRecentSearches(poi);
            _navService.SetDestination(poi);
            if (!_navService.IsNavigating)
                return;

            Debug.Log($"[NavUI] 목적지 선택: {poi.name}");

            Vector3 playerPos = _gpsService?.SmoothedUnityPosition ?? Vector3.zero;
            Vector3 destPos   = _navService?.DestinationWorldPos   ?? Vector3.zero;

            if (_minimapController != null)
                _minimapController.EnterOverviewMode(playerPos, destPos);

            ComputeOverviewBounds(playerPos, destPos);
            TransitionTo(NavUIState.MapOverview);
        }

        private void OnClickMoveToDestination()
        {
            _navService?.MoveToDestination();
        }

        private void OnClickCancelNavigation()
        {
            _navService?.ClearNavigation();
        }

        private void TransitionTo(NavUIState next)
        {
            _state = next;
        }

        // ── 오버뷰 헬퍼 ─────────────────────────────────────────────────────

        /// <summary>미니맵 유무에 따라 좌표 변환 방식을 자동 선택합니다.</summary>
        private Vector2 GetMapPos(Vector3 worldPos, Rect mapRect) =>
            _minimapController != null
                ? WorldToMapPos(worldPos, mapRect)
                : WorldToDiagramPos(worldPos, mapRect);

        /// <summary>경로·현재위치·목적지를 포함하는 XZ 바운딩 박스를 계산합니다 (다이어그램 모드용).</summary>
        private void ComputeOverviewBounds(Vector3 playerPos, Vector3 destPos)
        {
            float minX = Mathf.Min(playerPos.x, destPos.x);
            float maxX = Mathf.Max(playerPos.x, destPos.x);
            float minZ = Mathf.Min(playerPos.z, destPos.z);
            float maxZ = Mathf.Max(playerPos.z, destPos.z);

            var route = _navService?.CurrentRoute;
            if (route != null)
            {
                foreach (var pt in route)
                {
                    minX = Mathf.Min(minX, pt.x); maxX = Mathf.Max(maxX, pt.x);
                    minZ = Mathf.Min(minZ, pt.z); maxZ = Mathf.Max(maxZ, pt.z);
                }
            }

            float span = Mathf.Max(maxX - minX, maxZ - minZ, 50f);
            float pad  = span * 0.3f;
            _overviewWorldBounds = new Rect(
                minX - pad, minZ - pad,
                (maxX - minX) + pad * 2f,
                (maxZ - minZ) + pad * 2f);
        }

        /// <summary>월드 좌표 → mapRect 내 GUI 좌표 변환 (_overviewWorldBounds 기준, 미니맵 없을 때 사용)</summary>
        private Vector2 WorldToDiagramPos(Vector3 worldPos, Rect mapRect)
        {
            if (_overviewWorldBounds.width <= 0f) return mapRect.center;
            float u = (worldPos.x - _overviewWorldBounds.x) / _overviewWorldBounds.width;
            float v = 1f - (worldPos.z - _overviewWorldBounds.y) / _overviewWorldBounds.height;
            return new Vector2(
                mapRect.x + mapRect.width  * Mathf.Clamp01(u),
                mapRect.y + mapRect.height * Mathf.Clamp01(v));
        }

        /// <summary>월드 좌표 → mapRect 내 GUI 좌표 변환 (미니맵 카메라 정보 기준)</summary>
        private Vector2 WorldToMapPos(Vector3 worldPos, Rect mapRect)
        {
            Vector3 cam  = _minimapController.CurrentCamPosition;
            float size   = _minimapController.CurrentOrthoSize;

            // camera right = world +X,  camera up = world +Z → GUI Y 반전
            float u = (worldPos.x - cam.x) / (2f * size) + 0.5f;
            float v = 0.5f - (worldPos.z - cam.z) / (2f * size);

            return new Vector2(
                mapRect.x + mapRect.width  * Mathf.Clamp01(u),
                mapRect.y + mapRect.height * Mathf.Clamp01(v));
        }

        /// <summary>OnGUI 에서 임의 방향 직선 그리기 (matrix save/restore 포함)</summary>
        private static void DrawGUILine(Vector2 a, Vector2 b, float width, Color color)
        {
            Vector2 d = b - a;
            if (d.sqrMagnitude < 0.01f) return;
            float angle = Mathf.Atan2(d.y, d.x) * Mathf.Rad2Deg;
            Matrix4x4 saved = GUI.matrix;
            GUI.color = color;
            GUIUtility.RotateAroundPivot(angle, a);
            GUI.DrawTexture(new Rect(a.x, a.y - width * 0.5f, d.magnitude, width), Texture2D.whiteTexture);
            GUI.matrix = saved;
            GUI.color  = Color.white;
        }

        /// <summary>원형 마커를 중심점 기준으로 그립니다 (텍스처 버전)</summary>
        private static void DrawMapMarker(Vector2 center, float size, Texture2D tex)
        {
            GUI.DrawTexture(
                new Rect(center.x - size * 0.5f, center.y - size * 0.5f, size, size),
                tex);
        }

        /// <summary>원형 마커를 단색으로 그립니다 (외곽선용)</summary>
        private static void DrawMapMarker(Vector2 center, float size, Color color)
        {
            GUI.color = color;
            GUI.DrawTexture(
                new Rect(center.x - size * 0.5f, center.y - size * 0.5f, size, size),
                Texture2D.whiteTexture);
            GUI.color = Color.white;
        }

        // ── 최근 검색 ────────────────────────────────────────────────────────

        private void LoadRecentSearches()
        {
            _recentSearches = new List<POIData>();
            int count = PlayerPrefs.GetInt(PrefKeyCount, 0);
            for (int i = 0; i < count; i++)
            {
                string json = PlayerPrefs.GetString(PrefKeyPOI + i, "");
                if (string.IsNullOrEmpty(json)) continue;
                try
                {
                    var poi = JsonUtility.FromJson<POIData>(json);
                    if (poi != null) _recentSearches.Add(poi);
                }
                catch { }
            }
        }

        private void SaveRecentSearches()
        {
            PlayerPrefs.SetInt(PrefKeyCount, _recentSearches.Count);
            for (int i = 0; i < _recentSearches.Count; i++)
                PlayerPrefs.SetString(PrefKeyPOI + i, JsonUtility.ToJson(_recentSearches[i]));
            PlayerPrefs.Save();
        }

        private void AddToRecentSearches(POIData poi)
        {
            _recentSearches.RemoveAll(r => r.name == poi.name
                && System.Math.Abs(r.latitude  - poi.latitude)  < 1e-6
                && System.Math.Abs(r.longitude - poi.longitude) < 1e-6);
            _recentSearches.Insert(0, poi);
            if (_recentSearches.Count > MaxRecentSearches)
                _recentSearches.RemoveAt(_recentSearches.Count - 1);
            SaveRecentSearches();
        }

        // ── 유틸리티 ─────────────────────────────────────────────────────────

        private static string FormatDistance(float meters)
        {
            return meters >= 1000f
                ? $"{meters / 1000f:F1} km"
                : $"{Mathf.RoundToInt(meters)} m";
        }

        private static string CompactCategory(string category)
        {
            if (string.IsNullOrWhiteSpace(category))
                return "장소";

            int separator = category.LastIndexOf('>');
            string compact = separator >= 0 && separator < category.Length - 1
                ? category.Substring(separator + 1).Trim()
                : category.Trim();

            return compact.Length > 8 ? compact.Substring(0, 8) : compact;
        }

        private static void DrawShadowedRect(Rect rect, Color color)
        {
            GUI.color = new Color(0, 0, 0, 0.45f);
            GUI.DrawTexture(new Rect(rect.x + 3, rect.y + 3, rect.width, rect.height), Texture2D.whiteTexture);
            GUI.color = color;
            GUI.DrawTexture(rect, Texture2D.whiteTexture);
            GUI.color = Color.white;
        }

        // ── 스타일 초기화 ────────────────────────────────────────────────────

        private void EnsureStyles()
        {
            // 마커 텍스처는 null 확인으로 지연 생성
            if (_playerMarkerTex == null) _playerMarkerTex = MakeCircleTex(32, new Color(0.15f, 0.55f, 1.00f));
            if (_destMarkerTex   == null) _destMarkerTex   = MakeCircleTex(32, new Color(1.00f, 0.35f, 0.05f));

            if (_stylesReady && _styleScreenWidth == Screen.width && _styleScreenHeight == Screen.height)
                return;

            _stylesReady = true;
            _styleScreenWidth = Screen.width;
            _styleScreenHeight = Screen.height;

            int fs   = Mathf.RoundToInt(Mathf.Clamp(Screen.height * 0.030f, 20f, 34f));
            int fsS  = Mathf.RoundToInt(Mathf.Clamp(Screen.height * 0.022f, 15f, 24f));
            int fsL  = Mathf.RoundToInt(Mathf.Clamp(Screen.height * 0.040f, 26f, 42f));
            int fsXL = Mathf.RoundToInt(Mathf.Clamp(Screen.height * 0.060f, 38f, 64f));

            _styleMainBtn = new GUIStyle(GUI.skin.button)
            {
                fontSize  = fs,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleCenter,
                padding   = new RectOffset(12, 12, 4, 4),
                clipping  = TextClipping.Clip,
                normal    = { textColor = Color.white, background = MakeTex(new Color(0.05f, 0.45f, 0.95f)) },
                hover     = { textColor = Color.white, background = MakeTex(new Color(0.15f, 0.60f, 1.00f)) },
                active    = { textColor = Color.white, background = MakeTex(new Color(0.00f, 0.35f, 0.80f)) },
            };

            _styleSecondaryBtn = new GUIStyle(GUI.skin.button)
            {
                fontSize  = fsS,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleCenter,
                padding   = new RectOffset(10, 10, 3, 3),
                clipping  = TextClipping.Clip,
                normal    = { textColor = Color.white, background = MakeTex(new Color(0.25f, 0.28f, 0.38f)) },
                hover     = { textColor = Color.white, background = MakeTex(new Color(0.35f, 0.40f, 0.55f)) },
                active    = { textColor = Color.white, background = MakeTex(new Color(0.18f, 0.20f, 0.28f)) },
            };

            _styleDangerBtn = new GUIStyle(GUI.skin.button)
            {
                fontSize  = fs,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleCenter,
                padding   = new RectOffset(10, 10, 4, 4),
                clipping  = TextClipping.Clip,
                normal    = { textColor = Color.white, background = MakeTex(new Color(0.75f, 0.15f, 0.15f)) },
                hover     = { textColor = Color.white, background = MakeTex(new Color(0.90f, 0.25f, 0.25f)) },
                active    = { textColor = Color.white, background = MakeTex(new Color(0.60f, 0.10f, 0.10f)) },
            };

            _stylePanelTitle = new GUIStyle(GUI.skin.label)
            {
                fontSize  = fsL,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleLeft,
                clipping  = TextClipping.Clip,
                normal    = { textColor = Color.white },
            };

            _styleInfoLabel = new GUIStyle(GUI.skin.label)
            {
                fontSize  = fs,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleLeft,
                normal    = { textColor = Color.white },
                wordWrap  = false,
                clipping  = TextClipping.Clip,
            };

            _styleDistLabel = new GUIStyle(GUI.skin.label)
            {
                fontSize  = fsS,
                fontStyle = FontStyle.Normal,
                alignment = TextAnchor.MiddleCenter,
                clipping  = TextClipping.Clip,
                normal    = { textColor = new Color(0.75f, 0.87f, 1.0f) },
            };

            _styleResultBtn = new GUIStyle(GUI.skin.button)
            {
                fontSize  = fs,
                alignment = TextAnchor.MiddleLeft,
                padding   = new RectOffset(10, 10, 4, 4),
                clipping  = TextClipping.Clip,
                normal    = { textColor = Color.white, background = MakeTex(new Color(0.14f, 0.17f, 0.26f)) },
                hover     = { textColor = Color.white, background = MakeTex(new Color(0.20f, 0.25f, 0.40f)) },
                active    = { textColor = Color.white, background = MakeTex(new Color(0.05f, 0.45f, 0.95f)) },
            };

            _styleArrivedMsg = new GUIStyle(GUI.skin.label)
            {
                fontSize  = fsXL,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.UpperCenter,
                normal    = { textColor = Color.white },
            };

            _styleSearchField = new GUIStyle(GUI.skin.textField)
            {
                fontSize  = fs,
                alignment = TextAnchor.MiddleLeft,
                padding   = new RectOffset(12, 12, 4, 4),
                clipping  = TextClipping.Clip,
                normal    = {
                    textColor  = Color.white,
                    background = MakeTex(new Color(0.18f, 0.22f, 0.34f))
                },
            };

            _styleErrorLabel = new GUIStyle(GUI.skin.label)
            {
                fontSize  = fsS,
                fontStyle = FontStyle.Normal,
                alignment = TextAnchor.MiddleLeft,
                wordWrap  = true,
                normal    = { textColor = new Color(1.0f, 0.45f, 0.35f) },
            };

            _styleMapTitle = new GUIStyle(GUI.skin.label)
            {
                fontSize  = Mathf.RoundToInt(Screen.height * 0.038f),
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.UpperCenter,
                clipping  = TextClipping.Clip,
                normal    = { textColor = Color.white },
            };

            _styleMapDestName = new GUIStyle(GUI.skin.label)
            {
                fontSize  = Mathf.RoundToInt(Screen.height * 0.024f),
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleCenter,
                wordWrap  = false,
                clipping  = TextClipping.Clip,
                normal    = { textColor = new Color(1.0f, 0.92f, 0.45f) },
            };
        }

        private static Texture2D MakeTex(Color c)
        {
            var t = new Texture2D(1, 1);
            t.SetPixel(0, 0, c);
            t.Apply();
            return t;
        }

        private static Texture2D MakeCircleTex(int size, Color c)
        {
            var tex    = new Texture2D(size, size, TextureFormat.RGBA32, false);
            var pixels = new Color[size * size];
            float r    = size * 0.5f;
            for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                float dx = x - r + 0.5f;
                float dy = y - r + 0.5f;
                pixels[y * size + x] = (dx * dx + dy * dy) <= r * r ? c : Color.clear;
            }
            tex.SetPixels(pixels);
            tex.Apply();
            return tex;
        }
    }
}
