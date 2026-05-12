# 3D Digital Twin — 포스터용 아키텍처 다이어그램

## Core Flow

```
┌─────────────────────────────────────────────────────────────────────┐
│                      3D Digital Twin                                │
│              GPS 기반 모바일 1인칭 도시 탐색 시스템                 │
└─────────────────────────────────────────────────────────────────────┘

  [모바일 디바이스]
  ┌──────────────┐   ┌──────────────┐   ┌──────────────┐
  │ GPS 센서     │   │ 자이로스코프  │   │ 터치스크린   │
  └──────┬───────┘   └──────┬───────┘   └──────┬───────┘
         │                  │                  │
         ▼                  ▼                  ▼
  ┌──────────────────────────────────────────────────────┐
  │              권한 & 입력 레이어                       │
  │  LocationPermissionHandler · MobileInputSetup        │
  └──────────────────────────┬───────────────────────────┘
                             │
                             ▼
  ┌──────────────────────────────────────────────────────┐
  │              위치 처리 레이어                         │
  │                                                      │
  │  GPS 좌표 (WGS84)                                    │
  │       │                                              │
  │       ▼  GPSLocationService                          │
  │  WGS84 → ECEF → Unity World Coords                  │
  │       │  (CesiumGeoreference)                        │
  │       ▼                                              │
  │  SmoothedUnityPosition (Lerp 보간)                   │
  └──────────────────────────┬───────────────────────────┘
                             │
                             ▼
  ┌──────────────────────────────────────────────────────┐
  │              카메라 제어 레이어                       │
  │                                                      │
  │  FirstPersonGPSController                            │
  │  ├─ GPS 위치 → 카메라 XZ 이동                        │
  │  ├─ Cesium 지형 높이 샘플링 → 카메라 Y               │
  │  ├─ 자이로/드래그/고정 회전                           │
  │  └─ 건물 내부 침투 방지 (6방향 Raycast)               │
  │                                                      │
  │  CameraNavAnchor                                     │
  │  └─ 카메라 직하 지형 표면 → mainCameraNav            │
  └────────────┬──────────────────────┬──────────────────┘
               │                      │
               ▼                      ▼
  ┌────────────────────┐  ┌──────────────────────────────┐
  │  시각화 레이어      │  │  내비게이션 레이어            │
  │                    │  │                              │
  │  MinimapController │  │  KakaoPlaceSearchService     │
  │  (탑뷰 RenderTex)  │  │  ↓ POI 키워드 검색           │
  │                    │  │                              │
  │  BuildingLabel     │  │  NavigationService           │
  │  Manager           │  │  ├─ Kakao Directions API     │
  │  (건물명 OnGUI)    │  │  ├─ NavMesh 경로             │
  │                    │  │  ├─ Road Mesh A*             │
  │  CesiumCredit      │  │  └─ 직선 fallback            │
  │  Reducer           │  │  ↓ OnRouteCalculated         │
  │  (크레딧 UI 제어)  │  │                              │
  │                    │  │  RouteRenderer               │
  │                    │  │  (LineRenderer + 지형 투영)  │
  └────────────────────┘  └──────────────────────────────┘
               │                      │
               └──────────┬───────────┘
                          ▼
  ┌──────────────────────────────────────────────────────┐
  │              UI 레이어 (OnGUI)                        │
  │                                                      │
  │  NavigationUIController  ─── 5-상태 머신              │
  │  ┌────────────┬──────────────┬────────────────────┐  │
  │  │ None       │ SearchOpen   │ MapOverview        │  │
  │  │ (검색 버튼)│ (POI 검색)   │ (경로 오버뷰 지도) │  │
  │  ├────────────┴──────────────┴────────────────────┤  │
  │  │ Navigating (경로 안내 HUD)  │ Arrived (도착 알림)│  │
  │  └─────────────────────────────────────────────────┘  │
  └──────────────────────────────────────────────────────┘
```

---

## 시스템 아키텍처 다이어그램

```
┌─────────────────────────────────────────────────────────────────────┐
│                         외부 서비스                                  │
│                                                                     │
│   ┌──────────────────┐        ┌────────────────────────────────┐   │
│   │  Cesium ion       │        │  카카오 REST API                │   │
│   │  ─────────────── │        │  ─────────────────────────────  │   │
│   │  World Terrain    │        │  /v2/local/search/keyword      │   │
│   │  3D 건물 타일     │        │  /v1/directions (모빌리티)      │   │
│   │  도로 타일(dorohe)│        │  /v2/local/geo/coord2address   │   │
│   └────────┬─────────┘        └──────────────┬─────────────────┘   │
└────────────│──────────────────────────────────│─────────────────────┘
             │                                  │
             ▼                                  ▼
┌────────────────────┐              ┌───────────────────────┐
│  Cesium for Unity  │              │  Kakao 서비스 레이어   │
│  ────────────────  │              │  ─────────────────────│
│  CesiumGeoreference│              │  KakaoPlaceSearch     │
│  Cesium3DTileset   │              │  Service              │
│  CesiumGlobeAnchor │              │  KakaoDirections      │
│  CesiumCameraManager│             │  Service              │
└────────┬───────────┘              │  BuildingLabel        │
         │                         │  Manager              │
         │                         └──────────┬────────────┘
         │                                    │
         ▼                                    ▼
┌────────────────────────────────────────────────────────────┐
│                    RoadTools Runtime                        │
│                    (Rugem.RoadTools)                        │
│                                                            │
│  [GPS / 위치]                   [내비게이션]               │
│  LocationPermissionHandler  ─── KakaoPlaceSearchService    │
│  GPSLocationService         ─── KakaoDirectionsService     │
│  FirstPersonGPSController   ─── NavigationService          │
│  CameraNavAnchor            ─── RouteRenderer              │
│                                 NavigationUIController     │
│  [시각화 보조]                                              │
│  MinimapController                                         │
│  BuildingLabelManager                                      │
│  CesiumCreditReducer                                       │
│  MobileInputSetup                                          │
└────────────────────────────────────────────────────────────┘
         │
         ▼
┌────────────────────────────────────────────────────────────┐
│                    Unity 엔진 레이어                        │
│                                                            │
│  Physics (Raycast)    NavMesh AI        Input System       │
│  Camera               LineRenderer      OnGUI              │
│  RenderTexture        LayerMask         UIToolkit          │
└────────────────────────────────────────────────────────────┘
```

---

## 데이터 플로우 다이어그램

```
                    ┌─────────────────────────────────────┐
                    │         사용자 인터랙션              │
                    │   터치 / 검색어 입력 / POI 선택      │
                    └───────────────┬─────────────────────┘
                                    │
              ┌─────────────────────┼─────────────────────┐
              │                     │                     │
              ▼                     ▼                     ▼
    ┌─────────────────┐   ┌─────────────────┐   ┌──────────────────┐
    │  GPS 위치 데이터│   │  자이로 센서 값  │   │  POI 검색 요청   │
    │  (lat, lon, alt)│   │  (quaternion)    │   │  (키워드)        │
    └────────┬────────┘   └────────┬────────┘   └────────┬─────────┘
             │                     │                     │
             ▼                     ▼                     ▼
    ┌─────────────────┐   ┌─────────────────┐   ┌──────────────────┐
    │ WGS84→Unity 변환│   │  AttitudeSensor  │   │ Kakao Keyword    │
    │ (ECEF 경유)     │   │  quaternion 처리 │   │ API 응답         │
    └────────┬────────┘   └────────┬────────┘   └────────┬─────────┘
             │                     │                     │
             ▼                     ▼                     ▼
    ┌─────────────────┐   ┌─────────────────┐   ┌──────────────────┐
    │SmoothedUnity    │   │ Camera.rotation  │   │  List<POIData>   │
    │Position (Lerp)  │   │ (Lerp 보간)      │   │  → 목적지 선택   │
    └────────┬────────┘   └────────┬────────┘   └────────┬─────────┘
             │                     │                     │
             └─────────────────────┼─────────────────────┘
                                   │
                                   ▼
                        ┌─────────────────────┐
                        │  NavigationService   │
                        │  경로 계산           │
                        │  ① Kakao Directions  │
                        │  ② NavMesh Path     │
                        │  ③ Road Mesh A*     │
                        │  ④ 직선 fallback    │
                        └──────────┬──────────┘
                                   │ Vector3[] waypoints
                                   ▼
                        ┌─────────────────────┐
                        │  RouteRenderer       │
                        │  5m 간격 보간        │
                        │  Camera 기준 하방    │
                        │  Raycast → 지형 Y   │
                        │  LineRenderer 설정   │
                        └──────────┬──────────┘
                                   │
                                   ▼
                        ┌─────────────────────┐
                        │  화면 렌더링 결과    │
                        │  경로 선 + 마커      │
                        │  미니맵 탑뷰         │
                        │  건물명 레이블       │
                        └─────────────────────┘
```

---

## 컴포넌트 배치도 (level2.unity 씬 기준)

```
씬 계층 구조
│
├── CesiumGeoreference                    ← 지리 좌표계 원점
│   └── CesiumCameraManager
│
├── Cesium World Terrain                  ← 지형 3D 타일 (ionAssetID: 1)
│   └── CesiumIonRasterOverlay            ← 위성 이미지 (ionAssetID: 3830184)
│
├── dorohe                                ← 도로 3D 타일 (ionAssetID: 4609071)
│   ├── Cesium3DTileset (_createPhysicsMeshes: 1)
│   └── NavMeshSurface                    ← NavMesh 데이터
│
├── output_folder                         ← 건물 3D 타일 (ionAssetID: 4545115)
│
├── Main Camera                           ← 1인칭 카메라 (플레이어)
│   ├── Camera
│   ├── FirstPersonGPSController          ← GPS 이동/자이로 회전
│   ├── CesiumGlobeAnchor                 ← Cesium 좌표 고정
│   ├── CesiumOriginShift                 ← 부동소수점 정밀도 보정
│   ├── CameraNavAnchor                   ← mainCameraNav (지형 앵커)
│   └── CesiumCreditReducer               ← 크레딧 UI 제어
│
├── gpsm minimap                          ← GPS + 미니맵 오브젝트
│   ├── LocationPermissionHandler         ← 위치 권한
│   ├── GPSLocationService                ← GPS 수신/변환
│   ├── MinimapController                 ← 탑뷰 미니맵
│   └── RoadAssetPlacer                   ← 시설물 배치
│
├── navUI                                 ← 내비게이션 UI/로직 오브젝트
│   ├── NavigationUIController            ← 검색/안내 UI (OnGUI)
│   ├── NavigationService                 ← 경로 계산 오케스트레이터
│   ├── RouteRenderer + LineRenderer      ← 경로 시각화
│   ├── KakaoPlaceSearchService           ← 장소 검색 API
│   └── RoadAssetPlacer                   ← 시설물 배치
│
└── building nameTag Manager              ← 건물 이름 레이블
    └── BuildingLabelManager              ← Kakao coord2address + OnGUI
```

---

## 핵심 기술 스택

```
┌─────────────────────────────────────────────────────────────────────┐
│                         기술 스택                                    │
├────────────────┬────────────────┬────────────────┬──────────────────┤
│  런타임 엔진   │  3D 지도       │  위치 서비스   │  API             │
│  ─────────── │  ───────────── │  ─────────────│  ──────────────  │
│  Unity 6      │  Cesium for    │  Device GPS    │  Kakao Local     │
│  6000.4.1f1   │  Unity 1.23.1  │  (WGS84)      │  (장소 검색)     │
│               │  Cesium ion    │  AttitudeSensor│  Kakao Mobility  │
│               │  3D Tiles      │  (자이로)      │  (도로 경로)     │
├────────────────┼────────────────┼────────────────┼──────────────────┤
│  렌더링        │  내비게이션    │  입력          │  좌표 변환       │
│  ─────────── │  ───────────── │  ─────────────│  ──────────────  │
│  LineRenderer  │  NavMesh AI   │  New Input     │  WGS84           │
│  OnGUI         │  2.0.12       │  System        │  ↓ ECEF          │
│  RenderTexture │  A* Road Grid │  Pointer.current│  ↓ Unity World   │
│  URP Shader    │               │  EnhancedTouch │                  │
└────────────────┴────────────────┴────────────────┴──────────────────┘
```

---

## 주요 처리 알고리즘 요약

```
[GPS → 카메라 위치]                    [경로 계산 우선순위]
WGS84(lat, lon, alt)                    SetDestination()
  → ECEF (Cesium)                         │
  → Unity World (Georeference)            ├─1. Kakao Directions API
  → Lerp 보간                             │     도로 기반 실제 경로
  → 지형 높이 Raycast                    ├─2. NavMesh.CalculatePath()
  → Camera Y = 지형Y + 2m               │     Unity NavMesh 경로
                                          ├─3. Road Layer A* 그리드
[지형 표면 투영]                          │     Raycast + BFS/A*
Camera.y + 100m 기준점                  └─4. 직선 폴백 (2점)
  → 하방 Physics.Raycast
  → hit.y + 0.3m (Z-fighting 방지)     [건물명 캐시]
  → Fallback: Camera.y - 2m            GPS 양자화 (1e-4 = ~11m)
                                          → 딕셔너리 캐시 키
[경로 트리밍]                             → 최초 1회만 Kakao API 호출
매 프레임 Navigating 상태                → 이후 캐시 반환
  XZ 기준 가장 가까운 경로점
  → 지나간 구간 제거
  → DrawLine(trimmed[])
```
