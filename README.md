# 3D Digital Twin — 모바일 1인칭 3D 지도

**Cesium for Unity 기반 GPS 연동 1인칭 보행자 내비게이션 앱**  
팀 15조 | Unity 6 LTS (6000.4.1f1) | Cesium for Unity | Android / iOS

---

## 개요

기존 2D 탑뷰 지도(네이버·카카오·구글맵)는 사용자가 **지금 어느 방향을 보는지**를 직관적으로 알기 어렵고, 가로등·벤치·자전거 전용도로 등 **보행자용 세부 시설물 정보**가 없어 현장을 미리 체감하기 어렵습니다.

본 프로젝트는 Cesium for Unity를 통해 실제 3D 지형 위에서 GPS 기반 1인칭 시점으로 이동하고, 공공 데이터로 시설물을 3D 오버레이하는 **실감형 보행자 내비게이션 앱**을 구현합니다.

### 근거 통계

| 수치 | 내용 | 출처 |
|------|------|------|
| **71%** | 사용자가 2D 지도에서 정북 방향을 정확히 판단하지 못함 | ScienceDirect (보행 지하통로 길찾기 연구) |
| **33%** | 길찾기 중 최소 1회 이상 잘못된 방향으로 이동 | Journal of Road Safety (2021) |
| **인지 부하 30~50% 감소** | AR/3D 내비 사용 시 응시 시간·동공 크기 감소 | Cartography and GIS, Vol.48 No.3 (2021) |
| **78.9%** | 노인 스마트기기 미사용 이유 1위: "사용 방법이 어려워서" | 서울시 고령자 설문 / NIA (2023) |
| **CAGR 30.87%** | AR 실내 내비게이션 시장 연평균 성장률 2025~2035 | Market Research Future |

---

## 주요 기능

| 기능 | 컴포넌트 | 관련 US |
|------|----------|---------|
| GPS 수신 및 WGS84 → ECEF → Unity 좌표 변환 | `GPSLocationService` | US-04 |
| GPS 위치 Lerp 보간 및 현재 좌표 상태 제공 | `GPSLocationService`, `PositionProvider` | US-04 |
| 1인칭 카메라 GPS 동기화 + 지면 기준 눈높이 보정 | `FirstPersonGPSController` | US-05 |
| 자이로 / 고정 / 드래그 3가지 회전 모드 전환 | `FirstPersonGPSController` | US-11, US-12 |
| 나침반 평균화, GPS 점프 필터, 앱 복귀 시 센서 재활성화 | `FirstPersonGPSController` | US-17, US-20 |
| Cesium 지형 높이 비동기 샘플링 및 Raycast 폴백 | `FirstPersonGPSController` | US-16 |
| 건물 내부 진입 감지 후 인접 도로 위치로 보정 | `FirstPersonGPSController` | US-18 |
| Android / iOS 위치 권한 요청 및 GPS 시작 제어 | `FirstPersonGPSController`, `iOSBuildPostProcessor` | US-03 |
| 카카오 장소 키워드 검색, 현재 위치 반경 검색, 최근 검색 저장 | `KakaoPlaceSearchService`, `NavigationUIController` | US-14, US-24 |
| 목적지 설정, 도착 감지, 주기적 경로 재계산 | `NavigationService` | US-14 |
| 카카오 실도로 경로 → 도로 메쉬 A* → NavMesh → 직선 폴백 | `NavigationService`, `KakaoDirectionsService` | US-14 |
| UI, 경로 시각화, 미니맵을 묶는 내비게이션 진입점 | `NavigationCoordinator`, `RoutePresenter` | US-14, US-24 |
| LineRenderer 기반 3D 경로 표시, 도로/지형 높이 스냅, 지나간 구간 제거 | `RouteRenderer` | US-14 |
| RenderTexture 기반 탑뷰 미니맵 및 목적지 오버뷰 모드 | `MinimapController` | US-19, US-22 |
| CSV 기반 점/선 시설물 배치, 타입별 가시성 제어, 메쉬 분리 | `RoadAssetPlacer`, `RoadAssetPlacerEditor` | US-06, US-07, US-08, US-21 |
| Cesium 크레딧 UI 커스터마이징 | `MyCesiumCreditSystemUI.uxml` | US-01, US-02 |

---

## 아키텍처

```
          Device Layer (Android / iOS)
      GPS · Compass · AttitudeSensor · Touchscreen
                         │
┌────────────────────────▼────────────────────────┐
│ Presentation Layer                              │
│ NavigationUIController  MinimapController       │
│ FirstPersonGPSController                        │
└───────────────┬──────────────────────┬─────────┘
                │                      │
┌───────────────▼──────────────────────▼─────────┐
│ Application Layer                              │
│ NavigationCoordinator  NavigationService       │
│ RoutePresenter         RouteRenderer           │
│ PositionProvider       GPSLocationService      │
└───────────────┬──────────────────────┬─────────┘
                │                      │
┌───────────────▼────────────┐ ┌───────▼──────────────┐
│ Cesium / Unity Infra       │ │ External API          │
│ CesiumGeoreference         │ │ KakaoDirectionsService│
│ CesiumGlobeAnchor          │ │ KakaoPlaceSearchService│
│ Cesium3DTileset, NavMesh   │ │ KakaoApiKeyProvider   │
└────────────────────────────┘ └──────────────────────┘

Editor Tools
RoadAssetPlacerEditor → RoadAssetPlacer → CSV 시설물 배치 / 타입별 표시 제어
```

### GPS 좌표 변환 파이프라인

```
GPS 수신 (WGS84: 위도, 경도, 고도)
  ↓
GPSLocationService.GPSUpdateLoop()
  ↓
CesiumWgs84Ellipsoid.LongitudeLatitudeHeightToEarthCenteredEarthFixed()
  ↓
ECEF (지구 중심 고정 좌표계)
  ↓
CesiumGeoreference.TransformEarthCenteredEarthFixedPositionToUnity()
  ↓
Unity 월드 좌표 (TargetUnityPosition)
  ↓
GPSLocationService.Update()에서 SmoothedUnityPosition Lerp 보간
  ↓
OnRawPositionUpdated(rawUnityPosition) 이벤트
  ↓
FirstPersonGPSController.OnGPSPositionUpdated()
  ↓
Cesium3DTileset.SampleHeightMostDetailed()로 지형 높이 샘플링
  ↓ 실패 시
Physics.Raycast()로 지면 높이 폴백
  ↓
카메라 목표 위치 = (GPS X/Z, 지면Y + eyeHeight)
  ↓
CesiumGlobeAnchor Transform에 Lerp 적용
```

### 경로 안내 파이프라인

```
사용자 검색어 입력
  ↓
NavigationUIController → NavigationCoordinator.Search()
  ↓
KakaoPlaceSearchService.Search()
  ↓
POIData 목록 표시 및 최근 검색 저장
  ↓
사용자 목적지 선택
  ↓
NavigationService.SetDestination(POIData)
  ↓
NavigationService.CalculateRoute()
  ├─ 1순위: KakaoDirectionsService 실도로 vertexes 취득
  ├─ 2순위: Road 레이어 콜라이더 기반 도로 메쉬 A* 탐색
  ├─ 3순위: NavMesh.CalculatePath()
  └─ 4순위: 출발지-목적지 직선 경로
  ↓
OnRouteCalculated(POIData, Vector3[])
  ↓
RoutePresenter.ShowRoute()
  ↓
RouteRenderer.ShowRoute()
  ├─ 구간별 NavMesh corner 보정
  ├─ Road/terrain Raycast로 Y 좌표 스냅
  └─ 목적지 마커 생성
  ↓
LineRenderer 3D 경로 표시
  ↓
NavigationService.Update()
  ├─ 10초 주기 경로 재계산
  ├─ 수평 거리 15m 이내 도착 판정
  └─ OnArrived 이벤트 → UI Arrived 상태
```

---

## 코드 구조

```
Assets/
└── RoadTools/
    ├── Runtime/
    │   ├── CesiumCredit/
    │   │   └── MyCesiumCreditSystemUI.uxml  # Cesium Credit UI 커스터마이징
    │   ├── GPS/
    │   │   ├── GPSLocationService.cs          # US-04  WGS84→ECEF→Unity 변환 + Lerp 보간
    │   │   ├── FirstPersonGPSController.cs    # US-05,11,12,16,17,18,20  1인칭 카메라·터치 시점 조작
    │   │   └── CameraNavAnchor.cs             # 카메라 수직 하방 지면 앵커 (경로 계산 기준점)
    │   ├── Navigation/
    │   │   ├── NavigationCoordinator.cs       # UI에서 검색·경로·오버뷰 기능을 호출하는 단일 진입점
    │   │   ├── NavigationService.cs           # US-14  목적지 설정·경로 계산·도착 감지
    │   │   ├── NavigationUIController.cs      # US-14,24  검색·오버뷰·안내·도착 UI 상태 관리
    │   │   ├── PositionProvider.cs            # GPS/카메라 앵커 기반 현재 위치 제공
    │   │   ├── RoutePresenter.cs              # RouteRenderer와 MinimapController 연결
    │   │   ├── RouteRenderer.cs               # US-14  3D 경로 LineRenderer 및 도로/지형 스냅
    │   │   ├── POIData.cs                     # POI 데이터 구조체
    │   │   └── KakaoApi/
    │   │       ├── KakaoApiKeyProvider.cs     # Resources/kakao_api_key.txt 런타임 로드
    │   │       ├── KakaoDirectionsService.cs  # Kakao Mobility API 실도로 경로
    │   │       └── KakaoPlaceSearchService.cs # Kakao Local API 키워드 POI 검색
    │   ├── Minimap/
    │   │   └── MinimapController.cs           # US-19,22  탑뷰 미니맵 (RenderTexture + 전체 지도)
    └── Editor/
        ├── RoadAssetPlacer/
        │   ├── RoadAssetPlacer.cs             # US-06,07,08,21  CSV 기반 시설물 자동 배치·타입별 표시 제어
        │   └── Rugem.RoadTools.Runtime.asmdef # RoadAssetPlacer 전용 asmdef
        ├── RoadAssetPlacerEditor.cs           # Inspector CSV 배치·메쉬 분리 UI
        ├── iOSBuildPostProcessor.cs           # US-23  iOS Info.plist 위치 권한 자동 주입
        └── Rugem.RoadTools.Editor.asmdef
```

---

## 기술 스택

| 분류 | 내용 |
|------|------|
| **엔진** | Unity 6 LTS (6000.4.1f1) |
| **GIS 프레임워크** | Cesium for Unity v1.23.1+ |
| **좌표계** | WGS84 (GPS) → ECEF → Unity World |
| **입력** | New Input System — Touchscreen, AttitudeSensor, Compass |
| **내비게이션** | Unity AI Navigation (NavMesh) + Kakao Mobility API |
| **지오코딩** | Kakao Local API (POI 검색, 역지오코딩) |
| **빌드 타겟** | Android, iOS |
| **데이터 소스** | Cesium Ion, 공공 CSV/GeoJSON, Kakao 지도 API |

---

## 달성 지표

| 지표 | 목표 |
|------|------|
| GPS ↔ Unity 좌표 오차 | 실외 기준 **3m 이내** |
| 위치 업데이트 지연 | **500ms 이내** |
| 카메라 Jittering 발생률 | GPS ↔ 터치 전환 시 **1% 미만** |
| 최저 프레임 | **30 FPS 이상** |
| 시설물 가시 거리 | **50m 이상** |

---

## User Story 진행 현황

### Sprint 1 완료 (66 SP)

| US | 제목 | SP | 담당 | 상태 |
|----|------|----|------|------|
| US-01 | Cesium Ion 서버 연동 | 8 | 유성 | ✅ |
| US-02 | 대상 지역 데이터 최적화 | 5 | 유성 | ✅ |
| US-03 | 모바일 위치 권한 획득 | 3 | 혜린 | ✅ |
| US-04 | 지리좌표계 변환 엔진 | 3 | 현서 | ✅ |
| US-05 | 1인칭 카메라 위치 동기화 | 5 | 현서 | ✅ |
| US-06 | 외부 좌표 데이터 파싱 | 5 | 유성 | ✅ |
| US-07 | 데이터 기반 객체 자동 배치 | 3 | 유성 | ✅ |
| US-08 | 시설물 가시성 필터링 | 3 | 유성 | ✅ |
| US-11 | 터치 기반 시점 조작 | 5 | 혜린 | ✅ |
| US-12 | 조작 모드 심리스 전환 | 3 | 혜린 | ✅ |
| US-16 | Cesium 지형 표면 높이 정밀 측정 | 8 | 현서 | ✅ |
| US-17 | GPS 고도 노이즈 차단 및 지면 높이 캐싱 | 3 | 현서 | ✅ |
| US-18 | 건물 내부 침투 감지 및 인접 도로 위치 고정 | 5 | 현서 | ✅ |
| US-19 | 탑뷰 미니맵 (Cesium 타일 연동) | 5 | 혜린 | ✅ |
| US-20 | 앱 백그라운드·포커스 복귀 시 센서 자동 재활성화 | 2 | 혜린 | ✅ |

### Sprint 2 진행 현황 (41 SP)

| US | 제목 | SP | 담당 | 상태 |
|----|------|----|------|------|
| US-09 | 자전거 도로 폴리라인 생성 | 5 | 유성 | 🔄 |
| US-10 | 자전거 도로 특화 셰이더 | 5 | 유성 | 🔄 |
| US-14 | 실시간 경로 가이드 시각화 | 8 | 유성 | ✅ |
| US-15 | 거리 기반 LOD 및 컬링 | 5 | 유성 | 🔄 |
| US-21 | 데이터 기반 객체 지형 메쉬 컬링 보정 | 5 | 혜린 | 🔄 |
| US-22 | 미니맵을 통한 전체 지도 표시 | 5 | 현서 | 🔄 |
| US-23 | iOS 애플리케이션 빌드 | 5 | 현서 | ✅ |
| US-24 | 내비게이션 UI 개선 | 3 | 현서 | ✅ |

---

## 팀 구성

| 역할 | 이름 | 학번 | 담당 |
|------|------|------|------|
| Product Owner | 유성 | 202102669 | Cesium 인프라, 시설물 배치 (RoadAssetPlacer), 내비게이션, LOD 컬링, 자전거 도로 |
| Scrum Master | 여현서 | 202102664 | GPS·카메라 시스템 (GPSLocationService, FirstPersonGPSController), iOS 빌드, 미니맵 전체 지도, UI/UX |
| Developer | 이혜린 | 202300385 | 위치 권한 (LocationPermissionHandler), UI 최적화, 데이터 기반 객체 배치 |
| 지도교수 | 김형기 교수님 | — | — |

---

## 빠른 시작

### 필수 패키지

- Cesium for Unity (`com.cesium.unity`) v1.23.1 이상
- Unity AI Navigation (`com.unity.ai.navigation`)
- Input System (`com.unity.inputsystem`)

### Kakao API 키 설정

`Assets/Resources/kakao_api_key.txt` 파일을 생성하고 Kakao REST API 키를 한 줄로 저장합니다.  
이 파일은 `.gitignore`에 등록되어 있으므로 커밋되지 않습니다.

### 씬 설정

1. `CesiumGeoreference` 오브젝트 생성 후 대상 지역 위경도 설정
2. `Cesium3DTileset`에 Cesium Ion Asset ID 입력
3. 카메라 GameObject에 `GPSLocationService` + `FirstPersonGPSController` 추가
4. `FirstPersonGPSController`의 `World Terrain` 필드에 지형 Tileset 연결
5. `NavigationService` 오브젝트 생성 후 `GPSLocationService`, `KakaoDirectionsService` 연결
6. `RouteRenderer`를 씬에 추가하고 `NavigationService`에 연결
7. `RoadAssetPlacer` 오브젝트 생성 후 `assetPrefab` 및 `roadLayerMask` 설정
8. Inspector의 CSV 배치 버튼으로 시설물 데이터 로드

### CSV 형식

**선(Line) 데이터** — 가로수·가로등 노선
```
id, 시작위도, 시작경도, 종료위도, 종료경도, (기타), 갯수
```

**점(Point) 데이터** — 버스정류장·표지판 등
```
..., 위도, 경도, ...   (열 인덱스는 Inspector에서 지정)
```

---

## level2 씬 오브젝트 Hierarchy

기준 씬: `Assets/level2.unity`

현재 씬 오브젝트 수:

- 전체 GameObject: `9,427`
- 최상위 루트 GameObject: `3`
- 주요 런타임 루트: `CesiumGeoreference`

씬에는 도로 시설물 clone 오브젝트가 많이 포함되어 있습니다. README 가독성을 위해 반복 생성된 clone 오브젝트는 실제 부모 계층은 유지하되 개수로 요약했습니다.

```text
level2
├── CesiumGeoreference (9,425 objects)
│   ├── Cesium World Terrain
│   ├── Directional Light
│   ├── Main Camera (12 objects)
│   │   ├── Minimap Service (8 objects)
│   │   │   └── UI(Canvas) (7 objects)
│   │   │       └── Minimap (6 objects)
│   │   │           ├── CircleMask (2 objects)
│   │   │           │   └── RawImage
│   │   │           ├── FrameImage
│   │   │           ├── North
│   │   │           └── playerArrow
│   │   └── Navigation Service (3 objects)
│   │       └── Canvas (2 objects)
│   │           └── UI(Canvas)
│   ├── Road
│   ├── Road Placer (9,407 objects)
│   │   ├── [Type] 가로수 (550 objects)
│   │   │   ├── 41 Line_* groups
│   │   │   └── 509 Palm_Tree(Clone) instances
│   │   └── [Type] 시설물 (8,856 objects)
│   │       ├── 가로등: 8,576 Light Streetlight(Clone) instances
│   │       └── 버스정류장: 277 Bus Stop(Clone) instances
│   └── yuseonggu buildings (2 objects)
│       └── building nameTag Manager
├── EventSystem
└── Mesh Assets
```

### 미니맵 Canvas 계층

미니맵 UI는 `OnGUI`가 아니라 씬에 배치된 Canvas 오브젝트로 구현되어 있습니다.

```text
Main Camera
└── Minimap Service
    └── UI(Canvas)
        └── Minimap
            ├── CircleMask
            │   └── RawImage
            ├── FrameImage
            ├── North
            └── playerArrow
```

`MinimapController`는 런타임 미니맵 카메라와 `RenderTexture`를 생성한 뒤, 이 계층의 `RawImage` 오브젝트에 해당 텍스처를 연결합니다.
