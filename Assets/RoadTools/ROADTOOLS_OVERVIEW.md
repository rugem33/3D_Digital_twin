# RoadTools 프로젝트 개요서

> Cesium for Unity 기반 모바일 1인칭 3D 지도 애플리케이션용 도로 도구 패키지

---

## 목차

1. [프로젝트 개요](#1-프로젝트-개요)
2. [폴더 구조](#2-폴더-구조)
3. [어셈블리 구성](#3-어셈블리-구성)
4. [모듈별 기능 설명](#4-모듈별-기능-설명)
   - 4.1 [GPS 모듈](#41-gps-모듈)
   - 4.2 [Navigation 모듈](#42-navigation-모듈)
   - 4.3 [Minimap 모듈](#43-minimap-모듈)
   - 4.4 [RoadAssetPlacer 모듈](#44-roadassetplacer-모듈)
   - 4.5 [ShpToTile 모듈](#45-shptotile-모듈)
   - 4.6 [TerrainSource 모듈](#46-terrainsource-모듈)
   - 4.7 [VWorldOverlay 모듈](#47-vworldoverlay-모듈)
   - 4.8 [Editor 도구](#48-editor-도구)
5. [컴포넌트 의존 관계](#5-컴포넌트-의존-관계)
6. [핵심 데이터 흐름](#6-핵심-데이터-흐름)
7. [외부 의존성 및 API](#7-외부-의존성-및-api)
8. [API 키 관리](#8-api-키-관리)

---

## 1. 프로젝트 개요

**RoadTools**는 Unity + Cesium for Unity 환경에서 동작하는 모바일 1인칭 3D 지도 앱을 위한 통합 도로 도구 패키지입니다.

| 항목 | 내용 |
|------|------|
| 대상 플랫폼 | iOS / Android (에디터 도구는 Unity Editor 전용) |
| 핵심 의존성 | Cesium for Unity, Unity AI (NavMesh), Unity UI |
| 주요 언어 | C# (Unity 스크립트) |
| 어셈블리 수 | 2개 (Runtime, Editor) |

### 핵심 기능 요약

- **GPS 기반 1인칭 카메라** — 실시간 GPS 수신 및 Cesium 좌표 변환으로 기기 위치 동기화
- **실내외 길찾기** — 카카오 Directions API / NavMesh / 도로 메쉬 A* 다단계 경로 계산
- **장소 검색** — 카카오 로컬 API 기반 키워드 검색 및 POI 관리
- **3D 경로 시각화** — 도로 표면에 투영된 LineRenderer 경로 표시
- **위성/지도 오버레이** — V-World WMTS 기반 위성·지도·하이브리드 레이어 전환
- **지형 소스 전환** — Cesium Ion / 자체 quantized-mesh 서버 / Ellipsoid 간 전환
- **도로 자산 대량 배치** — CSV 기반 가로수·가로등 자동 배치 (에디터 도구)
- **SHP → 3D Tiles 변환** — mago-3D-tiler 서버 연동 자동 변환 및 배치

---

## 2. 폴더 구조

```
Assets/RoadTools/
├── ROADTOOLS_OVERVIEW.md           # 이 문서
├── README.md                       # 빠른 시작 가이드
│
├── Runtime/                        # 런타임 컴포넌트 (모바일 빌드 포함)
│   ├── Rugem.RoadTools.Runtime.asmdef
│   │
│   ├── GPS/                        # GPS 수신 및 1인칭 카메라 제어
│   │   ├── GPSLocationService.cs
│   │   ├── FirstPersonGPSController.cs
│   │   └── CameraNavAnchor.cs
│   │
│   ├── Navigation/                 # 경로 계산 및 길찾기 UI
│   │   ├── NavigationCoordinator.cs
│   │   ├── NavigationService.cs
│   │   ├── NavigationUIController.cs
│   │   ├── PositionProvider.cs
│   │   ├── RoutePresenter.cs
│   │   ├── RouteRenderer.cs
│   │   ├── POIData.cs
│   │   ├── KakaoApi/
│   │   │   ├── KakaoApiKeyProvider.cs
│   │   │   ├── KakaoDirectionsService.cs
│   │   │   └── KakaoPlaceSearchService.cs
│   │   └── Prefabs/
│   │       └── Navigation UI.prefab
│   │
│   ├── Minimap/                    # 미니맵 및 오버뷰 모드
│   │   └── MinimapController.cs
│   │
│   ├── RoadAssetPlacer/            # CSV 기반 도로 자산 배치
│   │   └── RoadAssetPlacer.cs
│   │
│   ├── ShpToTile/                  # SHP → 3D Tiles 변환 연동
│   │   └── ShpToTileConverter.cs
│   │
│   ├── TerrainSource/              # 지형 소스 전환
│   │   └── TerrainSourceSwitcher.cs
│   │
│   └── VWorldOverlay/              # V-World WMTS 오버레이
│       ├── VWorldApiKeyProvider.cs
│       └── VWorldOverlayController.cs
│
└── Editor/                         # 에디터 전용 도구 (빌드 미포함)
    ├── Rugem.RoadTools.Editor.asmdef
    ├── RoadAssetPlacerEditor.cs
    ├── ShpToTileConverterEditor.cs
    ├── TerrainSourceSwitcherEditor.cs
    ├── VWorldOverlayEditor.cs
    └── iOSBuildPostProcessor.cs
```

---

## 3. 어셈블리 구성

### Rugem.RoadTools.Runtime

| 항목 | 내용 |
|------|------|
| 포함 플랫폼 | 모든 플랫폼 (제한 없음) |
| 외부 참조 | Cesium for Unity, Unity UI, Unity AI (NavMesh) |
| Unsafe 코드 | 비허용 |

### Rugem.RoadTools.Editor

| 항목 | 내용 |
|------|------|
| 포함 플랫폼 | Unity Editor 전용 |
| 외부 참조 | Rugem.RoadTools.Runtime |
| 용도 | Inspector GUI, 빌드 후처리, 에디터 전용 변환 도구 |

---

## 4. 모듈별 기능 설명

### 4.1 GPS 모듈

#### GPSLocationService
GPS 신호 수신 및 WGS84 → Unity 월드 좌표 변환을 담당하는 핵심 엔진입니다.

| 주요 프로퍼티 | 설명 |
|---|---|
| `SmoothedUnityPosition` | Lerp 보간이 적용된 현재 Unity 위치 |
| `TargetUnityPosition` | GPS 원시 데이터 변환 좌표 (보간 전) |
| `CurrentLatitude/Longitude/Altitude` | 마지막 수신 GPS 좌표 |
| `IsRunning` | GPS 서비스 실행 중 여부 |
| `OnRawPositionUpdated` | 새 GPS 수신 시 이벤트 콜백 |

**좌표 변환 파이프라인**
```
기기 GPS (위도/경도/고도)
  → CesiumWgs84Ellipsoid.LongitudeLatitudeHeightToEarthCenteredEarthFixed()
  → CesiumGeoreference.TransformEarthCenteredEarthFixedPositionToUnity()
  → SmoothedUnityPosition (Lerp 보간)
```

#### FirstPersonGPSController
1인칭 카메라의 위치 및 회전을 GPS 데이터와 동기화합니다.

**회전 모드 3가지**

| 모드 | 설명 |
|---|---|
| Gyro | 기기 자이로센서 기반 자유 회전 |
| Locked | 현재 방위각 고정 |
| Drag | 터치 드래그로 수평 회전 |

**주요 기능**
- Cesium3DTileset 비동기 지면 높이 샘플링 (Raycast 폴백)
- GPS 점프 필터 (이전 위치 대비 20m 이상 급변 거부)
- 나침반 원형 평균 스무싱 (sin/cos 링 버퍼)
- 건물 내부 감지 및 인근 도로로 자동 보정 (반경 30m 방사형 탐색)
- iOS/Android 위치 권한 자동 요청

#### CameraNavAnchor
메인 카메라 정사영 지점에 `mainCameraNav` 자식 오브젝트를 유지합니다. NavigationService가 경로 시작점으로 이 위치를 사용합니다.

---

### 4.2 Navigation 모듈

길찾기 전체 기능을 담당하는 핵심 모듈입니다. 레이어드 아키텍처로 UI, 서비스, 시각화가 분리되어 있습니다.

#### 구성 컴포넌트

| 클래스 | 역할 |
|---|---|
| `NavigationUIController` | 길찾기 UI/UX 전체 렌더링 (OnGUI 기반, 상태 머신) |
| `NavigationCoordinator` | 외부 진입점 퍼사드 (UI ↔ 내부 서비스 위임) |
| `NavigationService` | 경로 계산 핵심 엔진 |
| `PositionProvider` | GPS 위치 + NavAnchor 통합 제공자 |
| `RoutePresenter` | RouteRenderer + MinimapController 통합 퍼사드 |
| `RouteRenderer` | 경로를 3D LineRenderer로 시각화 |
| `POIData` | 관심지점 데이터 모델 (이름/카테고리/좌표) |

#### NavigationUIController — UI 상태 머신

```
None (기본: 상단 검색바만 표시)
  │
  ├─[검색바 탭]──→ SearchOpen (전체화면 검색 패널)
  │                   │
  │                   └─[결과 선택]──→ MapOverview (경로 전체 오버뷰)
  │                                       │
  │                                       └─[안내 시작]──→ Navigating (하단 카드 + 방향 안내)
  │                                                           │
  │                                                           ├─[도착]──→ Arrived (타이머 후 None 복귀)
  │                                                           └─[취소]──→ None
  └─────────────────────────────────────────────────────────────────────────────────
```

**UI 주요 기능**
- 검색 입력 딜레이 서제스트 (입력 멈춤 후 0.35초)
- 최근 검색 이력 저장/복구 (PlayerPrefs)
- 검색 결과 드래그 스크롤뷰
- 방향 안내 화살표 (경로 18m 앞 지점 기준 방위각 계산)

#### NavigationService — 경로 계산 우선순위

```
SetDestination(POI) 호출
  │
  ├─[1순위] 카카오 Directions API (도로 네트워크 기반)
  │           → vertexes 배열 파싱 (경도/위도 쌍) → Unity 좌표 변환
  │
  ├─[2순위] 도로 메쉬 A* (API 실패 시)
  │           → Road 레이어 콜라이더로 그리드 생성 → 8방향 A* 탐색
  │
  ├─[3순위] Unity NavMesh (A* 실패 시)
  │           → NavMesh.CalculatePath()
  │
  └─[4순위] 직선 폴백 (NavMesh 실패 시)
              → 출발-도착 2점 직선
```

**주요 이벤트**

| 이벤트 | 발생 시점 |
|---|---|
| `OnRouteCalculated(POIData, Vector3[])` | 경로 계산 완료 |
| `OnDestinationSet(POIData)` | 목적지 설정 완료 |
| `OnNavigationCleared` | 길찾기 취소 |
| `OnArrived` | 도착 감지 (반경 15m 이내) |

#### RouteRenderer — 3D 경로 시각화

**경로 높이(Y) 결정 우선순위**
1. Road 레이어 Raycast (도로 콜라이더)
2. 일반 지형 Raycast
3. 카메라 위치 기반 추정 (폴백)

목적지 마커: 경로 끝에 빨간 구(Sphere) 형태로 자동 배치

#### PositionProvider — 위치 통합 제공자

**NavPosition 결정 우선순위**
1. CameraNavAnchor.NavTransform (지면 투영)
2. Camera.main 위치
3. GPSLocationService.SmoothedUnityPosition

---

### 4.3 Minimap 모듈

#### MinimapController
독립 카메라 + RenderTexture 기반 미니맵을 관리합니다.

| 기능 | 설명 |
|---|---|
| RenderTexture 바인딩 | Canvas RawImage에 실시간 렌더링 |
| 플레이어 화살표 | 나침반 기반 방향 표시 UI |
| 일반 모드 | 플레이어 위치 팔로우 |
| 오버뷰 모드 | 경로 전체가 보이도록 카메라 이동 및 orthoSize 자동 확대 |

---

### 4.4 RoadAssetPlacer 모듈

#### RoadAssetPlacer
CSV 파일 기반으로 도로 자산(가로수, 가로등 등)을 씬에 대량 배치합니다.

**배치 모드**

| 모드 | 방식 |
|---|---|
| NavMesh Edge | 도로 경계선에 스냅 (Right / Left / Both / Nearest) |
| OSM Polyline | 중심선에서 측면 오프셋 (Center / Right / Left / Both) |

**자동 처리 항목**
- 타입별 그룹 오브젝트 자동 생성
- CesiumGlobeAnchor 자동 추가
- Static Batching 적용 (선택)
- 그림자 옵션 일괄 적용
- MeshRenderer 별도 객체 분리 (DetachMeshes)
- NavMeshSurface 빌드 (BuildNavMesh)

**CSV 지원 형식**
- 선 데이터: 시작/종료 위도·경도 + 자산 개수 + 타입명
- 점 데이터: 위도/경도 (열 인덱스 자유 지정)

---

### 4.5 ShpToTile 모듈

#### ShpToTileConverter
SHP 파일을 mago-3D-tiler 서버로 변환 요청하고, 결과 3D Tiles를 씬에 자동 배치합니다.

**서버 API**

| 엔드포인트 | 용도 |
|---|---|
| `POST /api/convert` | 변환 요청 (SHP + 사이드카 파일 업로드) |
| `GET /api/jobs/{jobId}` | 변환 상태 폴링 |

**주요 설정 프로퍼티**

| 프로퍼티 | 설명 |
|---|---|
| `ServerUrl` | mago-3D-tiler 서버 주소 |
| `ShpFilePath` | 변환할 SHP 파일 경로 |
| `OutputType` | 출력 타일 포맷 |
| `CoordinateSystem` | 입력 좌표계 (EPSG 코드 등) |
| `HeightColumn` | 높이값으로 사용할 속성 열 이름 |
| `ScaleHeight` | 높이 스케일 배수 |
| `CurvatureCorrection` | 지구 곡률 보정 여부 |

완료 대기 최대 시간: **10분**

---

### 4.6 TerrainSource 모듈

#### TerrainSourceSwitcher
지형 소스를 세 가지 모드 간에 전환합니다.

| 모드 | 설명 |
|---|---|
| CesiumIon | Cesium Ion Asset ID 기반 스트리밍 |
| CustomUrl | 자체 quantized-mesh 서버 URL |
| Ellipsoid | 외부 서비스 없음, 평탄 지구면 |

CustomUrl 모드에서 `ValidateUrlCoroutine`이 layer.json 및 타일 파일 접근 가능 여부를 검증합니다.

---

### 4.7 VWorldOverlay 모듈

#### VWorldOverlayController
V-World WMTS 기반 위성/지도/하이브리드 오버레이를 Cesium3DTileset에 적용합니다.

**지원 레이어**

| 레이어 | 설명 |
|---|---|
| Satellite | 위성 사진 |
| Base | 기본 벡터 지도 |
| Hybrid | 위성 + 지도 혼합 |

**WMTS URL 템플릿**
```
https://api.vworld.kr/req/wmts/1.0.0/{key}/{layer}/{z}/{reverseY}/{x}.{format}
```

레이어 전환 시 Unity 크래시 방지를 위해 코루틴 기반 순차 전환을 사용합니다.

---

### 4.8 Editor 도구

| 파일 | 기능 |
|---|---|
| `RoadAssetPlacerEditor.cs` | CSV 로드/배치 진행률, 타입별 가시성 토글, 메쉬 분리 실행 |
| `ShpToTileConverterEditor.cs` | SHP 파일 선택, 사이드카 존재 확인(✓/✗), 변환 요청 버튼 |
| `TerrainSourceSwitcherEditor.cs` | URL 접근 테스트 (Play 불필요), 런타임 전환 버튼 |
| `VWorldOverlayEditor.cs` | 레이어 전환, ON/OFF, 제거/재적용 버튼 |
| `iOSBuildPostProcessor.cs` | iOS 빌드 후 `NSLocationWhenInUseUsageDescription` 자동 추가 |

---

## 5. 컴포넌트 의존 관계

```
NavigationUIController (UI 최상위)
    │
    └─→ NavigationCoordinator (외부 퍼사드)
           ├─→ NavigationService (경로 계산 엔진)
           │    ├─→ PositionProvider
           │    │    ├─→ GPSLocationService
           │    │    └─→ CameraNavAnchor
           │    ├─→ KakaoDirectionsService
           │    │    └─→ KakaoApiKeyProvider
           │    └─→ RouteRenderer
           │
           ├─→ RoutePresenter (시각화 퍼사드)
           │    ├─→ RouteRenderer
           │    └─→ MinimapController
           │
           ├─→ KakaoPlaceSearchService
           │    └─→ KakaoApiKeyProvider
           │
           └─→ PositionProvider

FirstPersonGPSController (1인칭 카메라, 독립)
    ├─→ GPSLocationService
    ├─→ Cesium3DTileset (지면 높이 샘플링)
    └─→ CesiumGlobeAnchor

RoadAssetPlacer (에디터/런타임 도구, 독립)
    └─→ CesiumGeoreference

ShpToTileConverter (독립)
    ├─→ CesiumGeoreference
    └─→ Cesium3DTileset

TerrainSourceSwitcher (독립)
    └─→ Cesium3DTileset

VWorldOverlayController (독립)
    ├─→ Cesium3DTileset
    ├─→ VWorldApiKeyProvider
    └─→ CesiumUrlTemplateRasterOverlay
```

---

## 6. 핵심 데이터 흐름

### 6.1 GPS → 카메라 위치 동기화

```
기기 GPS 신호 수신
  → GPSLocationService.GPSUpdateLoop()
  → WGS84 (위도/경도/고도)
  → ECEF 변환 (CesiumWgs84Ellipsoid)
  → Unity 월드 좌표 (CesiumGeoreference)
  → Lerp 보간 (SmoothedUnityPosition)
  → FirstPersonGPSController 이벤트 수신
  → CesiumGlobeAnchor.position 갱신
```

### 6.2 장소 검색 → 경로 계산 → 시각화

```
사용자: 검색어 입력
  → KakaoPlaceSearchService.Search()
  → POIData 목록 반환
  → 사용자: POI 선택
  → NavigationService.SetDestination(POI)
  → CalculateRoute() [다단계 폴백]
  → RouteRenderer.ShowRoute(Vector3[])
     ├─ 각 점을 도로 표면 Y에 투영
     └─ LineRenderer 업데이트
  → MinimapController 오버뷰 모드 진입
```

### 6.3 실시간 경로 안내

```
매 프레임
  → PositionProvider.NavPosition 갱신
  → NavigationService.UpdateDistance()
  → RouteRenderer.TrimFromPlayerPosition()  [지나간 구간 제거]
  → NavigationUIController: 방향 화살표 갱신
     └─ 경로 18m 앞 지점 기준 방위각 계산
  → NavigationService.CheckArrival()
     └─ 반경 15m 이내 → OnArrived 이벤트
```

---

## 7. 외부 의존성 및 API

| 서비스 | 용도 | 인증 |
|---|---|---|
| **Cesium for Unity** | 지구 좌표계 변환, 3D Tiles 스트리밍, 지형 메쉬 | Cesium Ion 토큰 |
| **카카오 Directions API** | 도로 네트워크 기반 경로 계산 | REST API 키 |
| **카카오 로컬 API** | 키워드 장소 검색 | REST API 키 (동일) |
| **V-World WMTS** | 위성/지도 타일 오버레이 | V-World API 키 |
| **mago-3D-tiler** | SHP → 3D Tiles 변환 서버 | 서버 URL (자체 호스팅) |
| **Unity NavMesh** | 경로 계산 폴백 | 없음 (Unity 내장) |

---

## 8. API 키 관리

### 카카오 API 키

- **저장 위치**: `Assets/Resources/kakao_api_key.txt`
- **형식**: `KakaoAK {REST_API_KEY}` 또는 키 값만 입력
- **처리**: `KakaoAK ` 접두사 자동 제거, `#` 시작 줄은 주석으로 무시
- **우선순위**: Inspector 필드 값 > 파일 로드

### V-World API 키

- **저장 위치**: `Assets/Resources/vworld_api_key.txt`
- **처리**: `VWorldApiKeyProvider.GetApiKey()` 로드 및 캐싱
- **우선순위**: Inspector 필드 값 > 파일 로드

### 공통 주의사항

`Resources/` 폴더에 저장된 키 파일은 빌드에 포함됩니다. 공개 저장소에 커밋하지 않도록 `.gitignore`에 추가를 권장합니다.

---

*최종 작성일: 2026-05-17*
