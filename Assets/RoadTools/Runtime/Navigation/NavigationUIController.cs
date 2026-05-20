using System.Collections.Generic;
using UnityEngine;

namespace Rugem.RoadTools
{
    /// <summary>
    /// 네비게이션 전체 UI를 OnGUI로 렌더링하는 컨트롤러입니다.
    /// 상업 지도 앱(카카오맵/구글맵) 스타일의 라이트 테마를 구현합니다.
    ///
    /// UI 상태 머신 (NavUIState):
    ///   None        → 상단 검색바만 표시 (기본 상태)
    ///   SearchOpen  → 전체화면 검색 패널 (최근 검색 / 카카오 검색 결과)
    ///   MapOverview → 경로 전체 오버뷰 지도 + 안내 시작 버튼
    ///   Navigating  → 하단 플로팅 카드 (목적지명·거리·취소) + 방향 안내 카드
    ///   Arrived     → 도착 알림 오버레이 (타이머 후 None 복귀)
    /// </summary>
    public class NavigationUIController : MonoBehaviour
    {
        [Header("의존성")]
        /// <summary>내비게이션 전체 동작의 단일 진입점 퍼사드</summary>
        [SerializeField] private NavigationCoordinator  _coordinator;

        [Header("UI 설정")]
        /// <summary>검색 패널이 화면 높이의 몇 %를 차지할지 비율 (0.4~0.85)</summary>
        [SerializeField, Range(0.4f, 0.85f)] private float _searchPanelHeightRatio = 0.65f;

        /// <summary>도착 알림 오버레이 표시 시간 (초)</summary>
        [SerializeField] private float _arrivedDisplayDuration = 3.5f;

        /// <summary>방향 안내 카드에서 플레이어 앞 몇 미터의 경로를 보고 방향을 계산할지 (미터)</summary>
        [SerializeField] private float _routeGuideLookAheadMeters = 18f;

        // ── 내부 상태 ─────────────────────────────────────────────────────────

        /// <summary>UI 상태 머신의 상태 정의</summary>
        private enum NavUIState { None, SearchOpen, MapOverview, Navigating, Arrived }

        /// <summary>현재 UI 상태</summary>
        private NavUIState _state = NavUIState.None;

        /// <summary>검색 필드에 입력된 현재 쿼리 문자열</summary>
        private string        _searchQuery    = "";

        /// <summary>카카오 API로부터 수신한 검색 결과 목록</summary>
        private List<POIData> _searchResults  = new();

        /// <summary>PlayerPrefs에서 로드한 최근 검색 이력 목록 (최대 MaxRecentSearches개)</summary>
        private List<POIData> _recentSearches = new();

        /// <summary>검색 패널에서 최근 검색 이력을 표시 중인지 여부 (false면 검색 결과 표시)</summary>
        private bool          _showingRecents = true;

        /// <summary>검색 결과 스크롤뷰의 현재 스크롤 위치</summary>
        private Vector2       _scrollPos;

        /// <summary>검색 목록 드래그 스크롤 진행 중 여부</summary>
        private bool          _isDraggingSearchList;

        /// <summary>드래그 스크롤 직전 마우스/터치 Y좌표 (델타 계산용)</summary>
        private float         _lastSearchDragY;

        /// <summary>드래그로 이동한 누적 거리 (클릭과 드래그 구분에 사용, 8픽셀 이하면 클릭으로 처리)</summary>
        private float         _searchDragDistance;

        /// <summary>도착 알림 남은 표시 시간 (초). 0 이하가 되면 None 상태로 복귀.</summary>
        private float         _arrivedTimer;

        /// <summary>검색 패널 열릴 때 텍스트 필드에 포커스를 한 번만 설정하기 위한 플래그</summary>
        private bool          _focusSearchOnce;

        /// <summary>카카오 API 요청이 진행 중인지 여부 (중복 요청 방지)</summary>
        private bool          _isSearching;

        /// <summary>마지막 검색 실패 메시지 (null이면 오류 없음)</summary>
        private string        _searchError;

        /// <summary>입력 변경 후 자동 검색(서제스트)이 대기 중인지 여부</summary>
        private bool          _hasPendingSuggestionSearch;

        /// <summary>마지막 입력 변경 시각 (Time.unscaledTime 기준, 딜레이 계산용)</summary>
        private float         _lastSearchInputChangeTime;

        /// <summary>
        /// 검색 요청 버전 번호. 요청마다 증가하여 오래된 콜백이 결과를 덮어쓰지 않도록 합니다.
        /// </summary>
        private int           _searchRequestVersion;

        /// <summary>
        /// 오버뷰 모드에서 경로 전체를 포함하는 월드 XZ 바운딩 박스.
        /// WorldToDiagramPos()에서 경로 점을 UI 좌표로 변환하는 데 사용합니다.
        /// </summary>
        private Rect          _overviewWorldBounds;

        // ── 상수 ─────────────────────────────────────────────────────────────

        /// <summary>최근 검색 이력 최대 보관 개수</summary>
        private const int    MaxRecentSearches            = 10;

        /// <summary>자동 서제스트 검색 최대 결과 수</summary>
        private const int    MaxSuggestionResults         = 10;

        /// <summary>자동 서제스트 검색 반경 (미터)</summary>
        private const int    SuggestionSearchRadiusMeters = 2000;

        /// <summary>입력 멈춤 후 자동 검색 시작까지 대기 시간 (초). 과도한 API 호출 방지.</summary>
        private const float  SuggestionSearchDelaySeconds = 0.35f;

        /// <summary>PlayerPrefs 저장 키 — 최근 검색 개수</summary>
        private const string PrefKeyCount                 = "NavRecent_Count";

        /// <summary>PlayerPrefs 저장 키 접두사 — 최근 검색 항목 JSON (NavRecent_0, NavRecent_1, ...)</summary>
        private const string PrefKeyPOI                   = "NavRecent_";

        // ── 스타일 ───────────────────────────────────────────────────────────

        /// <summary>파란색 기본 버튼 스타일 (검색, 확인)</summary>
        private GUIStyle _stylePrimaryBtn;

        /// <summary>초록색 '안내 시작' 버튼 스타일</summary>
        private GUIStyle _styleNavStartBtn;

        /// <summary>회색 보조 버튼 스타일 (뒤로, 재검색)</summary>
        private GUIStyle _styleSecondaryBtn;

        /// <summary>빨간색 위험 버튼 스타일 (취소)</summary>
        private GUIStyle _styleDangerBtn;

        /// <summary>패널 제목 레이블 스타일</summary>
        private GUIStyle _stylePanelTitle;

        /// <summary>목적지명 등 굵은 대형 정보 레이블 스타일</summary>
        private GUIStyle _styleInfoLabel;

        /// <summary>거리 표시 파란색 레이블 스타일</summary>
        private GUIStyle _styleDistLabel;

        /// <summary>검색 결과 항목 버튼 스타일 (투명 배경, 호버 시 파란 배경)</summary>
        private GUIStyle _styleResultBtn;

        /// <summary>도착 알림 큰 흰색 텍스트 스타일</summary>
        private GUIStyle _styleArrivedMsg;

        /// <summary>검색 텍스트 필드 스타일 (투명 배경)</summary>
        private GUIStyle _styleSearchField;

        /// <summary>오류 메시지 빨간색 레이블 스타일</summary>
        private GUIStyle _styleErrorLabel;

        /// <summary>오버뷰 지도 제목 흰색 굵은 레이블 스타일</summary>
        private GUIStyle _styleMapTitle;

        /// <summary>오버뷰 지도 목적지명 노란색 레이블 스타일</summary>
        private GUIStyle _styleMapDestName;

        /// <summary>검색바 힌트 텍스트("어디로 가시겠어요?") 회색 스타일</summary>
        private GUIStyle _styleHintLabel;

        /// <summary>검색 결과 장소명 굵은 레이블 스타일</summary>
        private GUIStyle _styleResultName;

        /// <summary>검색 결과 보조 텍스트(도착 메시지 등) 스타일</summary>
        private GUIStyle _styleResultSub;

        /// <summary>카테고리 칩 레이블 스타일 (작은 텍스트, 색상 동적 설정)</summary>
        private GUIStyle _styleChipLabel;

        /// <summary>검색 결과 주소/카테고리 서브 텍스트 스타일</summary>
        private GUIStyle _styleResultAddr;

        /// <summary>방향 안내 카드의 방향 아이콘(↑←→↺) 큰 스타일</summary>
        private GUIStyle _styleGuideIcon;

        /// <summary>방향 안내 카드의 안내 텍스트("왼쪽으로 이동" 등) 굵은 스타일</summary>
        private GUIStyle _styleGuideText;

        /// <summary>방향 안내 카드의 보조 텍스트("N m 앞 경로") 소형 스타일</summary>
        private GUIStyle _styleGuideSub;

        /// <summary>9-slice 라운드 코너 배경 박스 스타일 (카드 공통 사용)</summary>
        private GUIStyle _styleRoundedBase;

        /// <summary>화면 크기가 변경됐을 때 스타일을 재생성하기 위한 플래그</summary>
        private bool     _stylesReady;

        /// <summary>스타일 재생성 기준이 된 화면 너비</summary>
        private int      _styleScreenW;

        /// <summary>스타일 재생성 기준이 된 화면 높이</summary>
        private int      _styleScreenH;

        // ── 텍스처 ───────────────────────────────────────────────────────────

        /// <summary>플레이어 현재 위치 마커용 파란 원형 텍스처</summary>
        private Texture2D             _playerMarkerTex;

        /// <summary>목적지 마커용 빨간 원형 텍스처</summary>
        private Texture2D             _destMarkerTex;

        /// <summary>9-slice 카드 배경용 흰색 라운드 텍스처</summary>
        private Texture2D             _roundedWhiteTex;

        /// <summary>스타일 재생성 시 동적으로 생성된 버튼 배경 텍스처 목록 (OnDestroy 시 해제)</summary>
        private readonly List<Texture2D> _ownedTextures = new();

        // ── 색상 팔레트 (상업 지도 앱 라이트 테마) ───────────────────────────

        /// <summary>카드/패널 배경색 (거의 흰색)</summary>
        private static readonly Color C_Panel      = new Color(0.99f, 0.99f, 0.99f, 0.97f);

        // 기본(N), 호버(H), 클릭(A) 세 상태의 파란색 버튼 색상
        private static readonly Color C_PrimaryN   = new Color(0.13f, 0.59f, 0.95f);
        private static readonly Color C_PrimaryH   = new Color(0.22f, 0.66f, 1.00f);
        private static readonly Color C_PrimaryA   = new Color(0.08f, 0.47f, 0.82f);

        // 초록색 '안내 시작' 버튼 색상
        private static readonly Color C_GreenN     = new Color(0.04f, 0.69f, 0.42f);
        private static readonly Color C_GreenH     = new Color(0.08f, 0.78f, 0.50f);
        private static readonly Color C_GreenA     = new Color(0.02f, 0.56f, 0.34f);

        // 빨간색 '취소' 버튼 색상
        private static readonly Color C_DangerN    = new Color(0.93f, 0.26f, 0.21f);
        private static readonly Color C_DangerH    = new Color(1.00f, 0.36f, 0.30f);
        private static readonly Color C_DangerA    = new Color(0.78f, 0.18f, 0.14f);

        // 회색 보조 버튼 색상
        private static readonly Color C_SecN       = new Color(0.91f, 0.92f, 0.94f);
        private static readonly Color C_SecH       = new Color(0.84f, 0.86f, 0.91f);
        private static readonly Color C_SecA       = new Color(0.76f, 0.79f, 0.86f);

        /// <summary>기본 텍스트 색상 (거의 검정)</summary>
        private static readonly Color C_Text       = new Color(0.12f, 0.12f, 0.12f);

        /// <summary>보조 텍스트 색상 (중간 회색)</summary>
        private static readonly Color C_TextSub    = new Color(0.46f, 0.46f, 0.46f);

        /// <summary>힌트 텍스트 색상 (연한 회색)</summary>
        private static readonly Color C_TextHint   = new Color(0.72f, 0.72f, 0.72f);

        /// <summary>검색 결과 카테고리 칩 배경 파란색</summary>
        private static readonly Color C_ChipBlueBg = new Color(0.90f, 0.95f, 1.00f);

        /// <summary>검색 결과 카테고리 칩 텍스트 파란색</summary>
        private static readonly Color C_ChipBlueTx = new Color(0.13f, 0.50f, 0.90f);

        /// <summary>최근 검색 카테고리 칩 배경 보라색</summary>
        private static readonly Color C_ChipPurBg  = new Color(0.94f, 0.90f, 1.00f);

        /// <summary>최근 검색 카테고리 칩 텍스트 보라색</summary>
        private static readonly Color C_ChipPurTx  = new Color(0.50f, 0.20f, 0.80f);

        /// <summary>결과 항목 호버 배경색</summary>
        private static readonly Color C_ResultHov  = new Color(0.94f, 0.96f, 1.00f);

        /// <summary>항목 구분선 색상</summary>
        private static readonly Color C_Divider    = new Color(0.91f, 0.91f, 0.91f);

        /// <summary>도착 알림 카드 초록색 배경</summary>
        private static readonly Color C_Success    = new Color(0.04f, 0.68f, 0.42f);

        /// <summary>9-slice 라운드 텍스처 크기(픽셀) / 코너 반경(픽셀)</summary>
        private const int RndS = 64, RndR = 14;

        // ── 생명주기 ─────────────────────────────────────────────────────────

        private void Awake() => ResolveDependencies();

        /// <summary>Inspector 미연결 시 씬에서 NavigationCoordinator를 자동 탐색합니다.</summary>
        private void ResolveDependencies()
        {
            if (_coordinator  == null) _coordinator  = FindAnyObjectByType<NavigationCoordinator>();
        }

        private void OnEnable()
        {
            ResolveDependencies();
            if (_coordinator == null) return;
            // 코디네이터 이벤트에 UI 핸들러 등록
            _coordinator.OnRouteCalculated   += HandleRouteCalculated;
            _coordinator.OnNavigationCleared += HandleNavigationCleared;
            _coordinator.OnArrived           += HandleArrived;
        }

        private void OnDisable()
        {
            if (_coordinator == null) return;
            // 이벤트 해제 (메모리 누수 방지)
            _coordinator.OnRouteCalculated   -= HandleRouteCalculated;
            _coordinator.OnNavigationCleared -= HandleNavigationCleared;
            _coordinator.OnArrived           -= HandleArrived;
        }

        private void OnDestroy()
        {
            // 동적 생성 텍스처 명시적 해제
            if (_playerMarkerTex != null) Destroy(_playerMarkerTex);
            if (_destMarkerTex   != null) Destroy(_destMarkerTex);
            if (_roundedWhiteTex != null) Destroy(_roundedWhiteTex);
            foreach (var t in _ownedTextures) if (t != null) Destroy(t);
            _ownedTextures.Clear();
        }

        public void OnClickOpenSearch()
        {
            OpenSearch();
        }

        private void Update()
        {
            // 입력 대기 중인 서제스트 검색을 지연 후 실행
            UpdateSuggestionSearch();

            // 도착 알림 타이머 감소 → 만료 시 기본 상태 복귀
            if (_state == NavUIState.Arrived)
            {
                _arrivedTimer -= Time.deltaTime;
                if (_arrivedTimer <= 0f) TransitionTo(NavUIState.None);
            }

            // 길찾기 중: 플레이어 위치 기준으로 지나친 경로 구간 제거 (.?는 null 조건부 연산자)
            if (_state == NavUIState.Navigating)
                _coordinator?.TrimRoute(_coordinator.NavPosition);
        }

        // ── OnGUI ────────────────────────────────────────────────────────────

        /// <summary>
        /// 현재 UI 상태에 따라 해당 UI를 그립니다.
        /// 스타일은 화면 크기 변경 시 자동으로 재생성됩니다.
        /// </summary>
        private void OnGUI()
        {
            EnsureStyles();
            switch (_state)
            {
                case NavUIState.None:        DrawSearchBar();      break;
                case NavUIState.SearchOpen:  DrawSearchPanel();    break;
                case NavUIState.MapOverview: DrawMapOverview();    break;
                case NavUIState.Navigating:  DrawNavigationBar();  break;
                case NavUIState.Arrived:     DrawArrivedOverlay(); break;
            }
        }

        // ── None — 상단 검색바 (Google/Kakao Maps 스타일) ────────────────────

        /// <summary>
        /// 기본 상태에서 상단 검색바를 그립니다.
        /// 버튼 클릭 시 OpenSearch()를 호출해 SearchOpen 상태로 전환합니다.
        /// </summary>
        private void DrawSearchBar()
        {
            float margin  = Mathf.Clamp(Screen.width * 0.03f, 12f, 24f);
            float mapSize = Screen.height * (_coordinator?.MapSizeRatioConst ?? 0f);
            float mapX    = Screen.width - mapSize - margin;

            float barH = Mathf.Clamp(Screen.height * 0.082f, 64f, 88f);
            float barW = mapX - margin * 2f;
            float barX = margin;
            float barY = margin;

            DrawDropShadow(new Rect(barX, barY, barW, barH));
            DrawCard(new Rect(barX, barY, barW, barH), C_Panel);

            // 검색 아이콘 (파란 원)
            float iconR = barH * 0.22f;
            float iconX = barX + barH * 0.38f;
            float iconY = barY + barH * 0.5f;
            GUI.color = C_PrimaryN;
            GUI.DrawTexture(new Rect(iconX - iconR, iconY - iconR, iconR * 2f, iconR * 2f),
                _playerMarkerTex ?? Texture2D.whiteTexture);
            GUI.color = Color.white;

            float textX = iconX + iconR + margin * 0.5f;
            GUI.Label(new Rect(textX, barY, barX + barW - textX - margin * 0.4f, barH),
                "어디로 가시겠어요?", _styleHintLabel);

            if (GUI.Button(new Rect(barX, barY, barW, barH), GUIContent.none, GUIStyle.none))
                OpenSearch();
        }

        // ── SearchOpen — 검색 패널 (라이트 테마) ────────────────────────────

        /// <summary>
        /// 검색 패널을 그립니다.
        /// 상단: 뒤로 버튼 + 텍스트 필드 + 검색 버튼
        /// 하단 카드: 최근 검색 이력 또는 카카오 검색 결과 스크롤 목록
        /// 입력 변경 시 SuggestionSearchDelaySeconds 후 자동 검색이 실행됩니다.
        /// </summary>
        private void DrawSearchPanel()
        {
            // 흰색 배경 오버레이
            GUI.color = new Color(0.96f, 0.96f, 0.96f, 0.92f);
            GUI.DrawTexture(new Rect(0, 0, Screen.width, Screen.height), Texture2D.whiteTexture);
            GUI.color = Color.white;

            float margin = Mathf.Clamp(Screen.width * 0.03f, 12f, 24f);
            float barH   = Mathf.Clamp(Screen.height * 0.085f, 66f, 90f);
            float barY   = margin;

            // 뒤로 버튼
            float backW = barH;
            DrawDropShadow(new Rect(margin, barY, backW, barH));
            DrawCard(new Rect(margin, barY, backW, barH), C_Panel);
            if (GUI.Button(new Rect(margin, barY, backW, barH), "←", _styleSecondaryBtn))
            { CloseSearch(); return; }

            // 검색 필드
            float searchBtnW = Mathf.Clamp(Screen.width * 0.20f, 72f, 104f);
            float fieldX = margin + backW + margin * 0.4f;
            float fieldW = Screen.width - fieldX - searchBtnW - margin * 1.4f;

            DrawDropShadow(new Rect(fieldX, barY, fieldW, barH));
            DrawCard(new Rect(fieldX, barY, fieldW, barH), C_Panel);

            if (_focusSearchOnce && Event.current.type == EventType.Layout)
            { GUI.FocusControl("SearchField"); _focusSearchOnce = false; }

            GUI.SetNextControlName("SearchField");
            string newQ = GUI.TextField(
                new Rect(fieldX + margin * 0.55f, barY, fieldW - margin * 0.6f, barH),
                _searchQuery, _styleSearchField);
            if (newQ != _searchQuery) { _searchQuery = newQ; QueueSuggestionSearch(); }

            if (Event.current.type == EventType.KeyDown && Event.current.keyCode == KeyCode.Return
                && !_isSearching && !string.IsNullOrWhiteSpace(_searchQuery))
            { StartKakaoSearch(); Event.current.Use(); }

            // 검색 버튼
            float searchBtnX = fieldX + fieldW + margin * 0.4f;
            DrawDropShadow(new Rect(searchBtnX, barY, searchBtnW, barH));
            if (GUI.Button(new Rect(searchBtnX, barY, searchBtnW, barH),
                _isSearching ? "…" : "검색", _stylePrimaryBtn) && !_isSearching)
                StartKakaoSearch();

            // 결과 카드
            float panelY = barY + barH + margin * 0.5f;
            float panelH = Screen.height - panelY - margin;
            float panelW = Screen.width - margin * 2f;
            DrawDropShadow(new Rect(margin, panelY, panelW, panelH));
            DrawCard(new Rect(margin, panelY, panelW, panelH), C_Panel);

            float pad    = Mathf.Clamp(margin * 0.7f, 10f, 18f);
            float innerX = margin + pad;
            float innerW = panelW - pad * 2f;
            float statusH = Mathf.Clamp(Screen.height * 0.040f, 28f, 40f);
            float statusY = panelY + pad * 0.5f;

            if (_isSearching)
                GUI.Label(new Rect(innerX, statusY, innerW, statusH), "검색 중…", _styleDistLabel);
            else if (_searchError != null)
                GUI.Label(new Rect(innerX, statusY, innerW, statusH), _searchError, _styleErrorLabel);
            else if (_showingRecents && _recentSearches.Count > 0)
                GUI.Label(new Rect(innerX, statusY, innerW, statusH), "최근 검색", _stylePanelTitle);
            else if (!_showingRecents)
                GUI.Label(new Rect(innerX, statusY, innerW, statusH),
                    _searchResults.Count > 0 ? $"검색 결과 {_searchResults.Count}개" : "결과 없음",
                    _stylePanelTitle);

            // 구분선
            float divY = statusY + statusH + pad * 0.25f;
            GUI.color = C_Divider;
            GUI.DrawTexture(new Rect(innerX, divY, innerW, 1f), Texture2D.whiteTexture);
            GUI.color = Color.white;

            var  displayList  = _showingRecents ? _recentSearches : _searchResults;
            bool isRecentList = _showingRecents;
            float listY  = divY + 3f;
            float listH  = Mathf.Max(40f, panelY + panelH - listY - pad);
            float itemH  = Mathf.Clamp(Screen.height * 0.082f, 62f, 82f);
            float totalH = displayList.Count * itemH;

            Rect viewRect    = new Rect(innerX, listY, innerW, listH);
            Rect contentRect = new Rect(0, 0, innerW - 20f, Mathf.Max(totalH, listH + 1f));

            HandleSearchListScroll(viewRect, contentRect);
            _scrollPos = GUI.BeginScrollView(viewRect, _scrollPos, contentRect, false, false);
            {
                float iy = 0f;
                for (int i = 0; i < displayList.Count; i++)
                {
                    if (i > 0)
                    {
                        GUI.color = C_Divider;
                        GUI.DrawTexture(new Rect(0, iy - 0.5f, contentRect.width, 1f), Texture2D.whiteTexture);
                        GUI.color = Color.white;
                    }
                    DrawResultItem(displayList[i], new Rect(0, iy, contentRect.width, itemH), isRecentList);
                    iy += itemH;
                }
            }
            GUI.EndScrollView();
        }

        /// <summary>
        /// 검색 목록 스크롤 입력을 처리합니다.
        /// 마우스 휠, 터치 드래그 모두 지원합니다.
        /// _searchDragDistance를 통해 클릭(≤8px)과 드래그(>8px)를 구분합니다.
        /// </summary>
        private void HandleSearchListScroll(Rect viewRect, Rect contentRect)
        {
            float maxScrollY = Mathf.Max(0f, contentRect.height - viewRect.height);
            if (maxScrollY <= 0f) { _scrollPos = Vector2.zero; _isDraggingSearchList = false; _searchDragDistance = 0f; return; }
            Event e = Event.current; Vector2 mouse = e.mousePosition;
            if (e.type == EventType.ScrollWheel && viewRect.Contains(mouse))
            { _scrollPos.y = Mathf.Clamp(_scrollPos.y + e.delta.y * 18f, 0f, maxScrollY); e.Use(); return; }
            if (e.type == EventType.MouseDown && e.button == 0 && viewRect.Contains(mouse))
            { _isDraggingSearchList = true; _lastSearchDragY = mouse.y; _searchDragDistance = 0f; }
            else if (e.type == EventType.MouseDrag && _isDraggingSearchList)
            {
                float dy = _lastSearchDragY - mouse.y; _lastSearchDragY = mouse.y;
                _searchDragDistance += Mathf.Abs(dy);
                _scrollPos.y = Mathf.Clamp(_scrollPos.y + dy, 0f, maxScrollY); e.Use();
            }
            else if (e.type == EventType.MouseUp && e.button == 0) _isDraggingSearchList = false;
        }

        /// <summary>
        /// 검색 결과 또는 최근 검색 항목 하나를 그립니다.
        /// isRecent=true이면 보라색 칩, false이면 파란색 칩으로 구분됩니다.
        /// 드래그 거리가 8픽셀 이하일 때만 클릭으로 인식합니다.
        /// </summary>
        private void DrawResultItem(POIData poi, Rect rect, bool isRecent = false)
        {
            bool clicked = GUI.Button(rect, "", _styleResultBtn);
            if (clicked && _searchDragDistance <= 8f) SelectDestination(poi);
            if (clicked) _searchDragDistance = 0f;

            float sidePad = Mathf.Clamp(rect.height * 0.16f, 10f, 16f);

            // 아이콘 원형 뱃지
            float iconSz = rect.height * 0.46f;
            float iconX  = rect.x + sidePad;
            float iconCY = rect.y + rect.height * 0.5f;
            Color badgeBg   = isRecent ? C_ChipPurBg : C_ChipBlueBg;
            Color badgeText = isRecent ? C_ChipPurTx : C_ChipBlueTx;
            DrawCard(new Rect(iconX, iconCY - iconSz * 0.5f, iconSz, iconSz), badgeBg);
            // 뱃지 글자 색 임시 세팅
            Color saved = _styleChipLabel.normal.textColor;
            _styleChipLabel.normal.textColor = badgeText;
            GUI.Label(new Rect(iconX, iconCY - iconSz * 0.5f, iconSz, iconSz),
                isRecent ? "↺" : "•", _styleChipLabel);
            _styleChipLabel.normal.textColor = saved;

            // 장소명 + 카테고리
            float textX = iconX + iconSz + sidePad;
            float textW = rect.width * 0.60f;
            GUI.Label(new Rect(textX, rect.y + rect.height * 0.10f, textW, rect.height * 0.48f),
                poi.name, _styleResultName);
            string sub = CompactCategory(poi.category);
            if (!string.IsNullOrEmpty(sub))
                GUI.Label(new Rect(textX, rect.y + rect.height * 0.56f, textW, rect.height * 0.34f),
                    sub, _styleResultAddr);

            // 카테고리 칩 (우측)
            float chipW = Mathf.Clamp(rect.width * 0.22f, 52f, 92f);
            float chipH = rect.height * 0.38f;
            float chipX = rect.x + rect.width - chipW - sidePad;
            float chipY = rect.y + (rect.height - chipH) * 0.5f;
            DrawCard(new Rect(chipX, chipY, chipW, chipH), isRecent ? C_ChipPurBg : C_ChipBlueBg);
            Color chipTx = isRecent ? C_ChipPurTx : C_ChipBlueTx;
            Color savd2 = _styleChipLabel.normal.textColor;
            _styleChipLabel.normal.textColor = chipTx;
            GUI.Label(new Rect(chipX, chipY, chipW, chipH), isRecent ? "최근" : sub, _styleChipLabel);
            _styleChipLabel.normal.textColor = savd2;
        }

        // ── MapOverview — 경로 확인 ──────────────────────────────────────────

        /// <summary>
        /// 경로 오버뷰 화면을 그립니다.
        /// 미니맵 RenderTexture 위에 경로 선·플레이어·목적지 마커를 그리고,
        /// 하단 카드에 목적지명과 취소/안내 시작 버튼을 표시합니다.
        /// </summary>
        private void DrawMapOverview()
        {
            GUI.color = new Color(0f, 0f, 0f, 0.60f);
            GUI.DrawTexture(new Rect(0, 0, Screen.width, Screen.height), Texture2D.whiteTexture);
            GUI.color = Color.white;

            float margin = Mathf.Clamp(Screen.width * 0.04f, 14f, 30f);

            // 타이틀 바
            float titleH = Mathf.Clamp(Screen.height * 0.068f, 44f, 66f);
            DrawCard(new Rect(margin, margin * 0.5f, Screen.width - margin * 2f, titleH),
                new Color(0.08f, 0.08f, 0.10f, 0.95f));
            GUI.Label(new Rect(0, margin * 0.5f, Screen.width, titleH), "경로 확인", _styleMapTitle);

            // 하단 카드 크기 먼저 계산
            float bottomH = Mathf.Clamp(Screen.height * 0.21f, 138f, 175f);
            float mapTop  = margin * 0.5f + titleH + margin * 0.35f;
            float mapBot  = Screen.height - bottomH - margin * 0.7f;
            float mapSize = Mathf.Min(Screen.width * 0.92f, Mathf.Max(180f, mapBot - mapTop));
            float mapX    = (Screen.width - mapSize) * 0.5f;
            float mapY    = mapTop;
            var   mapRect = new Rect(mapX, mapY, mapSize, mapSize);

            // 지도 테두리 (파란색)
            GUI.color = C_PrimaryN;
            GUI.DrawTexture(new Rect(mapX - 3, mapY - 3, mapSize + 6, mapSize + 6), Texture2D.whiteTexture);
            GUI.color = Color.white;

            var rt = _coordinator?.OverviewTexture;
            if (rt != null)
            {
                GUI.DrawTexture(mapRect, rt, ScaleMode.ScaleToFit, false);
            }
            else
            {
                GUI.color = new Color(0.12f, 0.16f, 0.24f);
                GUI.DrawTexture(mapRect, Texture2D.whiteTexture);
                GUI.color = new Color(1f, 1f, 1f, 0.06f);
                for (int gi = 1; gi < 6; gi++)
                {
                    float t = gi / 6f;
                    GUI.DrawTexture(new Rect(mapX + mapSize * t, mapY, 1f, mapSize), Texture2D.whiteTexture);
                    GUI.DrawTexture(new Rect(mapX, mapY + mapSize * t, mapSize, 1f), Texture2D.whiteTexture);
                }
                GUI.color = Color.white;
            }

            // 경로 선
            var route = _coordinator?.CurrentRoute;
            if (route != null && route.Length >= 2)
            {
                float lw = Mathf.Max(mapSize * 0.010f, 3f);
                for (int i = 0; i < route.Length - 1; i++)
                {
                    Vector2 a = GetMapPos(route[i], mapRect), b = GetMapPos(route[i + 1], mapRect);
                    DrawGUILine(a, b, lw + 2f, new Color(0f, 0f, 0f, 0.35f));
                    DrawGUILine(a, b, lw, C_PrimaryN);
                }
            }

            // 마커
            if (_coordinator != null && _playerMarkerTex != null)
            {
                Vector2 pm = GetMapPos(_coordinator.PlayerPosition, mapRect);
                float sz = mapSize * 0.055f;
                DrawMapMarker(pm, sz + 4f, Color.black); DrawMapMarker(pm, sz, _playerMarkerTex);
            }
            if (_coordinator?.CurrentDestination != null && _destMarkerTex != null)
            {
                Vector2 dm = GetMapPos(_coordinator.DestinationWorldPos, mapRect);
                float sz = mapSize * 0.065f;
                DrawMapMarker(dm, sz + 4f, Color.black); DrawMapMarker(dm, sz, _destMarkerTex);
                float lblW = mapSize * 0.55f, lblH = Screen.height * 0.030f;
                GUI.Label(new Rect(dm.x - lblW * 0.5f, dm.y - sz * 0.5f - lblH - 2f, lblW, lblH),
                    _coordinator.CurrentDestination.name, _styleMapDestName);
            }

            GUI.color = Color.white;
            GUI.Label(new Rect(mapX + 6f, mapY + 4f, 30f, 30f), "N", _styleMapTitle);

            // 하단 흰색 카드
            float cardY = Screen.height - bottomH - margin * 0.3f;
            float cardW = Screen.width - margin * 2f;
            DrawDropShadow(new Rect(margin, cardY, cardW, bottomH));
            DrawCard(new Rect(margin, cardY, cardW, bottomH), C_Panel);

            float pad  = Mathf.Clamp(margin * 0.65f, 10f, 18f);
            float btnH = Mathf.Clamp(Screen.height * 0.072f, 52f, 70f);
            float btnW = (cardW - pad * 3f) * 0.5f;
            float btnY = cardY + bottomH - btnH - pad;
            string destName = _coordinator?.CurrentDestination?.name ?? "";
            GUI.Label(new Rect(margin + pad, cardY + pad * 0.6f,
                cardW - pad * 2f, bottomH - btnH - pad * 2f),
                string.IsNullOrEmpty(destName) ? "목적지" : destName, _styleInfoLabel);

            if (GUI.Button(new Rect(margin + pad, btnY, btnW, btnH), "취소", _styleDangerBtn))
            {
                _coordinator?.ExitOverviewMode();
                _coordinator?.ClearNavigation();
                TransitionTo(NavUIState.None);
            }
            if (GUI.Button(new Rect(margin + pad * 2f + btnW, btnY, btnW, btnH), "안내 시작", _styleNavStartBtn))
            {
                _coordinator?.ExitOverviewMode();
                TransitionTo(NavUIState.Navigating);
            }
        }

        // ── Navigating — 하단 플로팅 카드 ───────────────────────────────────

        /// <summary>
        /// 길찾기 중 하단 플로팅 카드를 그립니다.
        /// 목적지명·남은 거리·취소 버튼을 표시하고, 재검색 버튼도 카드 위에 배치합니다.
        /// </summary>
        private void DrawNavigationBar()
        {
            float margin = Mathf.Clamp(Screen.width * 0.035f, 12f, 26f);
            DrawRouteDirectionGuide(margin);
            float cardH  = Mathf.Clamp(Screen.height * 0.22f, 150f, 198f);
            float cardY  = Screen.height - cardH - margin;
            float cardW  = Screen.width - margin * 2f;

            DrawDropShadow(new Rect(margin, cardY, cardW, cardH));
            DrawCard(new Rect(margin, cardY, cardW, cardH), C_Panel);

            float pad    = Mathf.Clamp(margin * 0.7f, 10f, 18f);
            float innerX = margin + pad;
            float innerW = cardW - pad * 2f;

            // 목적지명
            string name  = _coordinator.CurrentDestination?.name ?? "목적지";
            float  nameH = Mathf.Clamp(Screen.height * 0.052f, 36f, 50f);
            GUI.Label(new Rect(innerX, cardY + pad * 0.7f, innerW, nameH), name, _styleInfoLabel);

            // 거리
            float dist    = _coordinator.DistanceToDestination;
            string distStr = dist >= 0f ? FormatDistance(dist) : "계산 중…";
            float distH   = Mathf.Clamp(Screen.height * 0.040f, 28f, 40f);
            GUI.Label(new Rect(innerX, cardY + nameH + pad * 0.8f, innerW * 0.55f, distH),
                distStr, _styleDistLabel);

            // 버튼
            float btnH  = Mathf.Clamp(Screen.height * 0.070f, 50f, 66f);
            float btnY  = cardY + cardH - btnH - pad;
            if (GUI.Button(new Rect(innerX, btnY, innerW, btnH), "취소", _styleDangerBtn))
                OnClickCancelNavigation();

            // 재검색 버튼 (카드 위)
            float rW = Mathf.Clamp(Screen.width * 0.22f, 76f, 114f);
            float rH = Mathf.Clamp(Screen.height * 0.047f, 34f, 48f);
            float rX = margin + cardW - rW;
            float rY = cardY - rH - pad * 0.5f;
            DrawDropShadow(new Rect(rX, rY, rW, rH));
            DrawCard(new Rect(rX, rY, rW, rH), C_Panel);
            if (GUI.Button(new Rect(rX, rY, rW, rH), "재검색", _styleSecondaryBtn))
                OpenSearch();
        }

        // ── 방향 안내 카드 / 도착 알림 ──────────────────────────────────────────

        /// <summary>
        /// 경로 앞 _routeGuideLookAheadMeters 미터 기준 방향 안내 카드를 그립니다.
        /// 플레이어 전방과 목표 방향의 SignedAngle로 직진/좌/우/유턴을 결정합니다.
        /// </summary>
        private void DrawRouteDirectionGuide(float margin)
        {
            if (!TryGetRouteDirectionGuide(out string icon, out string guide, out string sub))
                return;

            float guideW = Mathf.Min(Screen.width - margin * 2f, Mathf.Clamp(Screen.width * 0.54f, 260f, 440f));
            float guideH = Mathf.Clamp(Screen.height * 0.105f, 74f, 104f);
            float guideX = (Screen.width - guideW) * 0.5f;
            float guideY = margin;
            var rect = new Rect(guideX, guideY, guideW, guideH);

            DrawDropShadow(rect);
            DrawCard(rect, new Color(0.08f, 0.12f, 0.18f, 0.94f));

            float iconW = guideH * 0.82f;
            GUI.Label(new Rect(guideX + margin * 0.35f, guideY, iconW, guideH), icon, _styleGuideIcon);

            float textX = guideX + iconW + margin * 0.65f;
            float textW = guideW - iconW - margin;
            GUI.Label(new Rect(textX, guideY + guideH * 0.14f, textW, guideH * 0.46f), guide, _styleGuideText);
            GUI.Label(new Rect(textX, guideY + guideH * 0.55f, textW, guideH * 0.32f), sub, _styleGuideSub);
        }

        /// <summary>
        /// 현재 경로 기준 방향 안내 데이터를 계산합니다.
        /// lookAhead 거리 앞의 경로 목표 지점을 구하고 카메라 forward와의 각도로 방향을 결정합니다.
        /// 경로가 없거나 유효하지 않으면 false를 반환합니다.
        /// </summary>
        private bool TryGetRouteDirectionGuide(out string icon, out string guide, out string sub)
        {
            icon = "↑";
            guide = "경로를 따라 직진";
            sub = "";

            Vector3[] route = _coordinator?.CurrentRoute;
            if (route == null || route.Length < 2)
                return false;

            Vector3 player = GetNavigationPosition();
            if (!TryGetLookAheadRouteTarget(route, player, Mathf.Max(3f, _routeGuideLookAheadMeters), out Vector3 target, out float remainingToTarget))
                return false;

            Vector3 toTarget = target - player;
            toTarget.y = 0f;
            if (toTarget.sqrMagnitude < 0.01f)
                return false;

            Vector3 forward = Camera.main != null ? Camera.main.transform.forward : transform.forward;
            forward.y = 0f;
            if (forward.sqrMagnitude < 0.01f)
                forward = Vector3.forward;

            float angle = Vector3.SignedAngle(forward.normalized, toTarget.normalized, Vector3.up);
            float absAngle = Mathf.Abs(angle);

            if (absAngle < 20f)
            {
                icon = "↑";
                guide = "경로를 따라 직진";
            }
            else if (absAngle < 65f)
            {
                icon = angle < 0f ? "↰" : "↱";
                guide = angle < 0f ? "왼쪽 방향으로 이동" : "오른쪽 방향으로 이동";
            }
            else if (absAngle < 140f)
            {
                icon = angle < 0f ? "←" : "→";
                guide = angle < 0f ? "왼쪽으로 크게 이동" : "오른쪽으로 크게 이동";
            }
            else
            {
                icon = "↺";
                guide = "뒤쪽 방향으로 이동";
            }

            sub = $"{FormatDistance(remainingToTarget)} 앞 경로";
            return true;
        }

        private Vector3 GetNavigationPosition() =>
            _coordinator?.NavPosition ?? Vector3.zero;

        /// <summary>
        /// 경로에서 플레이어에 가장 가까운 구간을 찾은 뒤,
        /// 그 지점에서 lookAhead 미터 앞의 경로 위 좌표(target)와 실제 거리를 반환합니다.
        /// XZ 평면 투영으로 Y(높이) 오차를 제거합니다.
        /// </summary>
        private static bool TryGetLookAheadRouteTarget(Vector3[] route, Vector3 player, float lookAhead, out Vector3 target, out float distanceToTarget)
        {
            target = default;
            distanceToTarget = 0f;
            if (route == null || route.Length < 2)
                return false;

            Vector3 playerFlat = new Vector3(player.x, 0f, player.z);
            int nearestSegment = 0;
            float nearestT = 0f;
            float nearestDistSq = float.MaxValue;

            for (int i = 0; i < route.Length - 1; i++)
            {
                Vector3 a = new Vector3(route[i].x, 0f, route[i].z);
                Vector3 b = new Vector3(route[i + 1].x, 0f, route[i + 1].z);
                Vector3 ab = b - a;
                float abLenSq = ab.sqrMagnitude;
                if (abLenSq < 0.001f)
                    continue;

                float t = Mathf.Clamp01(Vector3.Dot(playerFlat - a, ab) / abLenSq);
                Vector3 p = a + ab * t;
                float dSq = (playerFlat - p).sqrMagnitude;
                if (dSq < nearestDistSq)
                {
                    nearestDistSq = dSq;
                    nearestSegment = i;
                    nearestT = t;
                }
            }

            Vector3 segStart = route[nearestSegment];
            Vector3 segEnd = route[nearestSegment + 1];
            Vector3 closest = Vector3.Lerp(segStart, segEnd, nearestT);
            closest.y = player.y;

            float remaining = lookAhead;
            Vector3 cursor = closest;
            for (int i = nearestSegment; i < route.Length - 1; i++)
            {
                Vector3 next = route[i + 1];
                Vector3 delta = new Vector3(next.x - cursor.x, 0f, next.z - cursor.z);
                float len = delta.magnitude;
                if (len < 0.001f)
                {
                    cursor = next;
                    continue;
                }

                if (remaining <= len)
                {
                    target = Vector3.Lerp(cursor, next, remaining / len);
                    distanceToTarget = lookAhead;
                    return true;
                }

                remaining -= len;
                cursor = next;
            }

            target = route[^1];
            distanceToTarget = Mathf.Max(0f, lookAhead - remaining);
            return true;
        }

        /// <summary>
        /// 도착 알림 오버레이를 그립니다.
        /// 초록색 카드 중앙에 "✓ 도착!" 메시지를 표시하고,
        /// _arrivedDisplayDuration 동안 진행 바가 줄어들다 사라집니다.
        /// </summary>
        private void DrawArrivedOverlay()
        {
            GUI.color = new Color(0f, 0f, 0f, 0.42f);
            GUI.DrawTexture(new Rect(0, 0, Screen.width, Screen.height), Texture2D.whiteTexture);
            GUI.color = Color.white;

            float boxW = Mathf.Min(Mathf.Clamp(Screen.width * 0.72f, 260f, 510f), Screen.width - 32f);
            float boxH = Mathf.Clamp(Screen.height * 0.22f, 140f, 196f);
            float boxX = (Screen.width  - boxW) * 0.5f;
            float boxY = (Screen.height - boxH) * 0.42f;

            DrawDropShadow(new Rect(boxX, boxY, boxW, boxH));
            DrawCard(new Rect(boxX, boxY, boxW, boxH), C_Success);

            GUI.Label(new Rect(boxX, boxY + boxH * 0.08f, boxW, boxH * 0.44f), "✓  도착!", _styleArrivedMsg);

            string name = _coordinator.CurrentDestination?.name ?? "";
            if (!string.IsNullOrEmpty(name))
                GUI.Label(new Rect(boxX, boxY + boxH * 0.52f, boxW, boxH * 0.28f),
                    name + "에 도착했습니다", _styleResultSub);

            float progress = 1f - (_arrivedTimer / _arrivedDisplayDuration);
            float barW2 = boxW * 0.65f, barH2 = Screen.height * 0.007f;
            float barX  = boxX + (boxW - barW2) * 0.5f;
            float barY2 = boxY + boxH - barH2 - Screen.height * 0.014f;
            GUI.color = new Color(1f, 1f, 1f, 0.25f);
            GUI.DrawTexture(new Rect(barX, barY2, barW2, barH2), Texture2D.whiteTexture);
            GUI.color = Color.white;
            GUI.DrawTexture(new Rect(barX, barY2, barW2 * progress, barH2), Texture2D.whiteTexture);
        }

        // ── 이벤트 핸들러 ─────────────────────────────────────────────────────

        private void HandleRouteCalculated(POIData poi, Vector3[] route) =>
            _coordinator?.ShowRoute(route);

        private void HandleNavigationCleared()
        {
            _coordinator?.HideRoute();
            if (_state == NavUIState.Navigating || _state == NavUIState.MapOverview)
                TransitionTo(NavUIState.None);
        }

        private void HandleArrived()
        {
            _coordinator?.HideRoute();
            _arrivedTimer = _arrivedDisplayDuration;
            TransitionTo(NavUIState.Arrived);
        }

        // ── UI 동작 ───────────────────────────────────────────────────────────

        /// <summary>
        /// 검색 패널을 초기화하고 SearchOpen 상태로 전환합니다.
        /// 텍스트 필드에 포커스를 한 번 설정하고 최근 검색 이력을 로드합니다.
        /// </summary>
        private void OpenSearch()
        {
            _searchQuery = ""; _searchResults = new List<POIData>(); _scrollPos = Vector2.zero;
            _focusSearchOnce = true; _isSearching = false; _searchError = null;
            _hasPendingSuggestionSearch = false; _searchRequestVersion++;
            _showingRecents = true; LoadRecentSearches();
            TransitionTo(NavUIState.SearchOpen);
        }

        /// <summary>
        /// 현재 검색 쿼리로 카카오 장소 검색 API를 호출합니다.
        /// _searchRequestVersion으로 이전 요청의 콜백이 덮어쓰지 않도록 보호합니다.
        /// </summary>
        private void StartKakaoSearch()
        {
            string query = _searchQuery.Trim();
            if (string.IsNullOrWhiteSpace(query)) return;
            _hasPendingSuggestionSearch = false; _showingRecents = false;
            if (_coordinator == null) { _searchResults = new List<POIData>(); _scrollPos = Vector2.zero; return; }
            _isSearching = true; _searchError = null;
            int ver = ++_searchRequestVersion;
            _coordinator.Search(query, MaxSuggestionResults, SuggestionSearchRadiusMeters, (results, error) =>
            {
                if (this == null || !isActiveAndEnabled || ver != _searchRequestVersion) return;
                _isSearching = false;
                if (error != null) { _searchError = error; _searchResults = new List<POIData>(); return; }
                _searchResults = results ?? new List<POIData>(); _scrollPos = Vector2.zero;
            });
        }

        /// <summary>
        /// 입력 변경 시 서제스트 자동 검색을 예약합니다.
        /// SuggestionSearchDelaySeconds 후 UpdateSuggestionSearch()에서 실제 요청이 발생합니다.
        /// 쿼리가 비어 있으면 최근 검색 이력 표시로 복귀합니다.
        /// </summary>
        private void QueueSuggestionSearch()
        {
            string query = _searchQuery.Trim();
            _searchRequestVersion++; _searchError = null; _scrollPos = Vector2.zero;
            _isSearching = false;
            if (string.IsNullOrWhiteSpace(query))
            {
                _hasPendingSuggestionSearch = false; _searchResults = new List<POIData>();
                _showingRecents = true; return;
            }
            _showingRecents = false; _searchResults = new List<POIData>();
            _hasPendingSuggestionSearch = true; _lastSearchInputChangeTime = Time.unscaledTime;
        }

        /// <summary>
        /// Update()에서 매 프레임 호출됩니다.
        /// 대기 중인 서제스트 검색이 있고 지연 시간이 지났으면 실제 검색을 실행합니다.
        /// </summary>
        private void UpdateSuggestionSearch()
        {
            if (_state != NavUIState.SearchOpen || !_hasPendingSuggestionSearch) return;
            if (Time.unscaledTime - _lastSearchInputChangeTime < SuggestionSearchDelaySeconds) return;
            StartKakaoSearch();
        }

        private void CloseSearch() =>
            TransitionTo(_coordinator != null && _coordinator.IsNavigating
                ? NavUIState.Navigating : NavUIState.None);

        /// <summary>
        /// 검색 결과 항목 클릭 시 호출됩니다.
        /// 최근 검색에 추가하고, 목적지를 설정한 뒤 오버뷰 모드로 전환합니다.
        /// </summary>
        private void SelectDestination(POIData poi)
        {
            if (poi == null) return;
            ResolveDependencies();
            if (_coordinator == null) return;
            AddToRecentSearches(poi);
            _coordinator.SetDestination(poi);
            if (!_coordinator.IsNavigating) return;
            Vector3 playerPos = _coordinator?.NavPosition           ?? Vector3.zero;
            Vector3 destPos   = _coordinator?.DestinationWorldPos   ?? Vector3.zero;
            _coordinator?.EnterOverviewMode(playerPos, destPos);
            ComputeOverviewBounds(playerPos, destPos);
            TransitionTo(NavUIState.MapOverview);
        }

        private void OnClickCancelNavigation()   => _coordinator?.ClearNavigation();
        private void TransitionTo(NavUIState s)  => _state = s;

        // ── 오버뷰 헬퍼 ──────────────────────────────────────────────────────

        /// <summary>
        /// 월드 좌표를 오버뷰 지도 UI 좌표로 변환합니다.
        /// 미니맵이 있으면 카메라 직교 투영 기준 변환, 없으면 경로 바운딩박스 기준 변환을 사용합니다.
        /// </summary>
        private Vector2 GetMapPos(Vector3 worldPos, Rect mapRect) =>
            _coordinator != null && _coordinator.HasMinimap
                ? WorldToMapPos(worldPos, mapRect)
                : WorldToDiagramPos(worldPos, mapRect);

        /// <summary>
        /// 플레이어·목적지·경로 전체를 포함하는 월드 XZ 바운딩박스를 계산합니다.
        /// 30% 여백을 추가해 경로가 오버뷰 지도 안에 여유 있게 들어오도록 합니다.
        /// </summary>
        private void ComputeOverviewBounds(Vector3 playerPos, Vector3 destPos)
        {
            float minX = Mathf.Min(playerPos.x, destPos.x), maxX = Mathf.Max(playerPos.x, destPos.x);
            float minZ = Mathf.Min(playerPos.z, destPos.z), maxZ = Mathf.Max(playerPos.z, destPos.z);
            var route = _coordinator?.CurrentRoute;
            if (route != null) foreach (var pt in route)
            {
                minX = Mathf.Min(minX, pt.x); maxX = Mathf.Max(maxX, pt.x);
                minZ = Mathf.Min(minZ, pt.z); maxZ = Mathf.Max(maxZ, pt.z);
            }
            float span = Mathf.Max(maxX - minX, maxZ - minZ, 50f), pad = span * 0.3f;
            _overviewWorldBounds = new Rect(minX - pad, minZ - pad,
                (maxX - minX) + pad * 2f, (maxZ - minZ) + pad * 2f);
        }

        /// <summary>
        /// 미니맵 없을 때: _overviewWorldBounds 기준 정규화(0~1)하여 mapRect 픽셀 좌표로 변환합니다.
        /// V축은 Z가 증가하면 화면 아래로 가도록 반전합니다.
        /// </summary>
        private Vector2 WorldToDiagramPos(Vector3 worldPos, Rect mapRect)
        {
            if (_overviewWorldBounds.width <= 0f) return mapRect.center;
            float u = (worldPos.x - _overviewWorldBounds.x) / _overviewWorldBounds.width;
            float v = 1f - (worldPos.z - _overviewWorldBounds.y) / _overviewWorldBounds.height;
            return new Vector2(mapRect.x + mapRect.width * Mathf.Clamp01(u),
                               mapRect.y + mapRect.height * Mathf.Clamp01(v));
        }

        /// <summary>
        /// 미니맵 있을 때: 미니맵 카메라 위치와 orthographicSize를 기준으로
        /// 월드 좌표를 직교 투영 픽셀 좌표로 변환합니다.
        /// </summary>
        private Vector2 WorldToMapPos(Vector3 worldPos, Rect mapRect)
        {
            Vector3 cam = _coordinator.MinimapCamPosition;
            float   sz  = _coordinator.MinimapOrthoSize;
            float u = (worldPos.x - cam.x) / (2f * sz) + 0.5f;
            float v = 0.5f - (worldPos.z - cam.z) / (2f * sz);
            return new Vector2(mapRect.x + mapRect.width * Mathf.Clamp01(u),
                               mapRect.y + mapRect.height * Mathf.Clamp01(v));
        }

        private static void DrawGUILine(Vector2 a, Vector2 b, float width, Color color)
        {
            Vector2 d = b - a;
            if (d.sqrMagnitude < 0.01f) return;
            float angle    = Mathf.Atan2(d.y, d.x) * Mathf.Rad2Deg;
            Matrix4x4 saved = GUI.matrix;
            GUI.color = color;
            GUIUtility.RotateAroundPivot(angle, a);
            GUI.DrawTexture(new Rect(a.x, a.y - width * 0.5f, d.magnitude, width), Texture2D.whiteTexture);
            GUI.matrix = saved; GUI.color = Color.white;
        }

        private static void DrawMapMarker(Vector2 center, float size, Texture2D tex) =>
            GUI.DrawTexture(new Rect(center.x - size * 0.5f, center.y - size * 0.5f, size, size), tex);

        private static void DrawMapMarker(Vector2 center, float size, Color color)
        {
            GUI.color = color;
            GUI.DrawTexture(new Rect(center.x - size * 0.5f, center.y - size * 0.5f, size, size),
                Texture2D.whiteTexture);
            GUI.color = Color.white;
        }

        // ── 그리기 유틸리티 ───────────────────────────────────────────────────

        /// <summary>
        /// 주어진 Rect 아래에 두 겹의 반투명 그림자를 그려 카드 깊이감을 표현합니다.
        /// </summary>
        private static void DrawDropShadow(Rect r)
        {
            GUI.color = new Color(0f, 0f, 0f, 0.10f);
            GUI.DrawTexture(new Rect(r.x, r.y + 4f, r.width, r.height + 4f), Texture2D.whiteTexture);
            GUI.color = new Color(0f, 0f, 0f, 0.06f);
            GUI.DrawTexture(new Rect(r.x - 2f, r.y + 8f, r.width + 4f, r.height + 6f), Texture2D.whiteTexture);
            GUI.color = Color.white;
        }

        private void DrawCard(Rect rect, Color color)
        {
            GUI.color = color;
            if (_styleRoundedBase != null)
                GUI.Box(rect, GUIContent.none, _styleRoundedBase);
            else
                GUI.DrawTexture(rect, Texture2D.whiteTexture);
            GUI.color = Color.white;
        }

        // ── 최근 검색 ─────────────────────────────────────────────────────────

        /// <summary>
        /// PlayerPrefs에서 최근 검색 이력을 로드합니다.
        /// JSON 직렬화 실패한 항목은 무시하고 건너뜁니다.
        /// </summary>
        private void LoadRecentSearches()
        {
            _recentSearches = new List<POIData>();
            int count = PlayerPrefs.GetInt(PrefKeyCount, 0);
            for (int i = 0; i < count; i++)
            {
                string json = PlayerPrefs.GetString(PrefKeyPOI + i, "");
                if (string.IsNullOrEmpty(json)) continue;
                try { var p = JsonUtility.FromJson<POIData>(json); if (p != null) _recentSearches.Add(p); } catch { }
            }
        }

        private void SaveRecentSearches()
        {
            PlayerPrefs.SetInt(PrefKeyCount, _recentSearches.Count);
            for (int i = 0; i < _recentSearches.Count; i++)
                PlayerPrefs.SetString(PrefKeyPOI + i, JsonUtility.ToJson(_recentSearches[i]));
            PlayerPrefs.Save();
        }

        /// <summary>
        /// 선택한 POI를 최근 검색 목록 맨 앞에 추가합니다.
        /// 동일한 이름+위경도가 이미 있으면 제거 후 재삽입합니다.
        /// MaxRecentSearches 초과 시 가장 오래된 항목을 삭제합니다.
        /// </summary>
        private void AddToRecentSearches(POIData poi)
        {
            _recentSearches.RemoveAll(r => r.name == poi.name
                && System.Math.Abs(r.latitude  - poi.latitude)  < 1e-6
                && System.Math.Abs(r.longitude - poi.longitude) < 1e-6);
            _recentSearches.Insert(0, poi);
            if (_recentSearches.Count > MaxRecentSearches) _recentSearches.RemoveAt(_recentSearches.Count - 1);
            SaveRecentSearches();
        }

        // ── 유틸리티 ─────────────────────────────────────────────────────────

        /// <summary>미터를 사람이 읽기 좋은 거리 문자열로 변환합니다. 1km 이상이면 km 단위로 표시.</summary>
        private static string FormatDistance(float meters) =>
            meters >= 1000f ? $"{meters / 1000f:F1} km" : $"{Mathf.RoundToInt(meters)} m";

        /// <summary>
        /// 카테고리 문자열에서 마지막 '>' 이후의 가장 구체적인 분류를 추출합니다.
        /// 8자 초과이면 앞 8자만 사용합니다.
        /// </summary>
        private static string CompactCategory(string category)
        {
            if (string.IsNullOrWhiteSpace(category)) return "장소";
            int sep = category.LastIndexOf('>');
            string s = sep >= 0 && sep < category.Length - 1
                ? category.Substring(sep + 1).Trim() : category.Trim();
            return s.Length > 8 ? s.Substring(0, 8) : s;
        }

        // ── 스타일 초기화 ─────────────────────────────────────────────────────

        /// <summary>
        /// GUI 스타일과 마커 텍스처가 없거나 화면 크기가 바뀌었으면 재생성합니다.
        /// 화면 해상도에 비례한 fontSize를 사용해 다양한 기기 크기에 대응합니다.
        /// </summary>
        private void EnsureStyles()
        {
            if (_playerMarkerTex == null) _playerMarkerTex = MakeCircleTex(32, C_PrimaryN);
            if (_destMarkerTex   == null) _destMarkerTex   = MakeCircleTex(32, C_DangerN);

            if (_stylesReady && _styleScreenW == Screen.width && _styleScreenH == Screen.height) return;
            _stylesReady  = true;
            _styleScreenW = Screen.width;
            _styleScreenH = Screen.height;

            // 기존 소유 텍스처 파기
            foreach (var t in _ownedTextures) if (t != null) Destroy(t);
            _ownedTextures.Clear();

            // 9-slice 라운드 베이스 텍스처
            if (_roundedWhiteTex == null) _roundedWhiteTex = MakeRoundedTex(RndS, RndS, RndR, Color.white);

            _styleRoundedBase = new GUIStyle(GUI.skin.box)
            {
                border  = new RectOffset(RndR, RndR, RndR, RndR),
                padding = new RectOffset(0, 0, 0, 0),
                margin  = new RectOffset(0, 0, 0, 0),
                overflow = new RectOffset(0, 0, 0, 0),
                normal  = { background = _roundedWhiteTex, textColor = Color.clear },
            };

            int fs   = Mathf.RoundToInt(Mathf.Clamp(Screen.height * 0.030f, 20f, 34f));
            int fsS  = Mathf.RoundToInt(Mathf.Clamp(Screen.height * 0.022f, 14f, 24f));
            int fsL  = Mathf.RoundToInt(Mathf.Clamp(Screen.height * 0.038f, 24f, 40f));
            int fsXL = Mathf.RoundToInt(Mathf.Clamp(Screen.height * 0.056f, 34f, 58f));

            _stylePrimaryBtn = new GUIStyle(GUI.skin.button)
            {
                fontSize = fs, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter,
                border   = new RectOffset(RndR, RndR, RndR, RndR),
                padding  = new RectOffset(12, 12, 6, 6),
                normal   = { textColor = Color.white, background = NewTex(C_PrimaryN) },
                hover    = { textColor = Color.white, background = NewTex(C_PrimaryH) },
                active   = { textColor = Color.white, background = NewTex(C_PrimaryA) },
            };

            _styleNavStartBtn = new GUIStyle(GUI.skin.button)
            {
                fontSize = fs, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter,
                border   = new RectOffset(RndR, RndR, RndR, RndR),
                padding  = new RectOffset(12, 12, 6, 6),
                normal   = { textColor = Color.white, background = NewTex(C_GreenN) },
                hover    = { textColor = Color.white, background = NewTex(C_GreenH) },
                active   = { textColor = Color.white, background = NewTex(C_GreenA) },
            };

            _styleDangerBtn = new GUIStyle(GUI.skin.button)
            {
                fontSize = fs, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter,
                border   = new RectOffset(RndR, RndR, RndR, RndR),
                padding  = new RectOffset(12, 12, 6, 6),
                normal   = { textColor = Color.white, background = NewTex(C_DangerN) },
                hover    = { textColor = Color.white, background = NewTex(C_DangerH) },
                active   = { textColor = Color.white, background = NewTex(C_DangerA) },
            };

            _styleSecondaryBtn = new GUIStyle(GUI.skin.button)
            {
                fontSize = fsS, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter,
                border   = new RectOffset(RndR, RndR, RndR, RndR),
                padding  = new RectOffset(10, 10, 5, 5),
                normal   = { textColor = C_Text, background = NewTex(C_SecN) },
                hover    = { textColor = C_Text, background = NewTex(C_SecH) },
                active   = { textColor = C_Text, background = NewTex(C_SecA) },
            };

            _stylePanelTitle = new GUIStyle(GUI.skin.label)
            {
                fontSize = fsS, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleLeft,
                normal = { textColor = C_TextSub },
            };

            _styleInfoLabel = new GUIStyle(GUI.skin.label)
            {
                fontSize = fsL, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleLeft,
                wordWrap = false, clipping = TextClipping.Clip,
                normal = { textColor = C_Text },
            };

            _styleDistLabel = new GUIStyle(GUI.skin.label)
            {
                fontSize = fs, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleLeft,
                normal = { textColor = C_PrimaryN },
            };

            _styleResultBtn = new GUIStyle(GUI.skin.button)
            {
                fontSize = fs, alignment = TextAnchor.MiddleLeft,
                padding  = new RectOffset(6, 6, 4, 4),
                normal   = { textColor = C_Text, background = NewTex(Color.clear) },
                hover    = { textColor = C_Text, background = NewTex(C_ResultHov) },
                active   = { textColor = C_Text, background = NewTex(C_ChipBlueBg) },
            };

            _styleArrivedMsg = new GUIStyle(GUI.skin.label)
            {
                fontSize = fsXL, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter,
                normal = { textColor = Color.white },
            };

            _styleSearchField = new GUIStyle(GUI.skin.textField)
            {
                fontSize = fs, alignment = TextAnchor.MiddleLeft,
                padding  = new RectOffset(10, 10, 4, 4),
                normal   = { textColor = C_Text, background = NewTex(Color.clear) },
                focused  = { textColor = C_Text, background = NewTex(Color.clear) },
            };

            _styleErrorLabel = new GUIStyle(GUI.skin.label)
            {
                fontSize = fsS, wordWrap = true,
                normal = { textColor = C_DangerN },
            };

            _styleMapTitle = new GUIStyle(GUI.skin.label)
            {
                fontSize = Mathf.RoundToInt(Screen.height * 0.035f),
                fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter,
                normal = { textColor = Color.white },
            };

            _styleMapDestName = new GUIStyle(GUI.skin.label)
            {
                fontSize = Mathf.RoundToInt(Screen.height * 0.024f),
                fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter,
                wordWrap = false,
                normal = { textColor = new Color(1f, 0.92f, 0.45f) },
            };

            _styleHintLabel = new GUIStyle(GUI.skin.label)
            {
                fontSize = fs, alignment = TextAnchor.MiddleLeft,
                normal = { textColor = C_TextHint },
            };

            _styleResultName = new GUIStyle(GUI.skin.label)
            {
                fontSize = fs, fontStyle = FontStyle.Bold, alignment = TextAnchor.LowerLeft,
                wordWrap = false, clipping = TextClipping.Clip,
                normal = { textColor = C_Text },
            };

            _styleResultSub = new GUIStyle(GUI.skin.label)
            {
                fontSize = fsS, alignment = TextAnchor.MiddleCenter,
                wordWrap = false, clipping = TextClipping.Clip,
                normal = { textColor = Color.white },
            };

            _styleChipLabel = new GUIStyle(GUI.skin.label)
            {
                fontSize = fsS, alignment = TextAnchor.MiddleCenter,
                clipping = TextClipping.Clip,
                normal = { textColor = C_ChipBlueTx },
            };

            _styleResultAddr = new GUIStyle(GUI.skin.label)
            {
                fontSize = fsS, alignment = TextAnchor.UpperLeft,
                wordWrap = false, clipping = TextClipping.Clip,
                normal = { textColor = C_TextSub },
            };

            _styleGuideIcon = new GUIStyle(GUI.skin.label)
            {
                fontSize = Mathf.RoundToInt(Mathf.Clamp(Screen.height * 0.064f, 42f, 62f)),
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleCenter,
                normal = { textColor = Color.white },
            };

            _styleGuideText = new GUIStyle(GUI.skin.label)
            {
                fontSize = Mathf.RoundToInt(Mathf.Clamp(Screen.height * 0.026f, 18f, 28f)),
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.LowerLeft,
                wordWrap = false,
                clipping = TextClipping.Clip,
                normal = { textColor = Color.white },
            };

            _styleGuideSub = new GUIStyle(GUI.skin.label)
            {
                fontSize = Mathf.RoundToInt(Mathf.Clamp(Screen.height * 0.018f, 12f, 18f)),
                alignment = TextAnchor.UpperLeft,
                wordWrap = false,
                clipping = TextClipping.Clip,
                normal = { textColor = new Color(0.74f, 0.86f, 1f, 1f) },
            };
        }

        /// <summary>라운드 코너 텍스처를 생성하고 _ownedTextures에 등록해 OnDestroy 시 자동 해제합니다.</summary>
        private Texture2D NewTex(Color c)
        {
            var t = MakeRoundedTex(RndS, RndS, RndR, c);
            _ownedTextures.Add(t);
            return t;
        }

        /// <summary>
        /// 지정 크기의 라운드 코너 사각형 텍스처를 생성합니다.
        /// 9-slice 스케일링에 사용되며, 코너 r픽셀이 부드럽게 처리됩니다.
        /// </summary>
        private static Texture2D MakeRoundedTex(int w, int h, int r, Color c)
        {
            var tex = new Texture2D(w, h, TextureFormat.RGBA32, false);
            var pixels = new Color[w * h];
            for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                pixels[y * w + x] = InRoundedRect(x, y, w, h, r) ? c : Color.clear;
            tex.filterMode = FilterMode.Bilinear;
            tex.SetPixels(pixels);
            tex.Apply();
            return tex;
        }

        private static bool InRoundedRect(int x, int y, int w, int h, int r)
        {
            int x1 = r, x2 = w - r - 1, y1 = r, y2 = h - r - 1;
            if (x >= x1 && x <= x2) return true;
            if (y >= y1 && y <= y2) return true;
            float cx = x < x1 ? x1 : x2, cy = y < y1 ? y1 : y2;
            return (x - cx) * (x - cx) + (y - cy) * (y - cy) <= (float)r * r;
        }

        /// <summary>
        /// 플레이어·목적지 마커용 원형 텍스처를 생성합니다.
        /// 원 바깥 픽셀은 투명(Color.clear)으로 설정합니다.
        /// </summary>
        private static Texture2D MakeCircleTex(int size, Color c)
        {
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
            var pixels = new Color[size * size];
            float r = size * 0.5f;
            for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                float dx = x - r + 0.5f, dy = y - r + 0.5f;
                pixels[y * size + x] = dx * dx + dy * dy <= r * r ? c : Color.clear;
            }
            tex.SetPixels(pixels);
            tex.Apply();
            return tex;
        }
    }
}
