# RoadTools

Cesium for Unity 기반 GPS 길찾기 및 도로 자산 배치 도구 모음입니다.  
실제 GPS 좌표로 1인칭 카메라를 제어하고, 카카오 API와 자체 A\* 알고리즘으로 경로를 계산하여 3D 지도 위에 안내합니다.

---

## 주요 기능

| 기능 | 설명 |
|---|---|
| GPS 1인칭 제어 | 실제 GPS 좌표 → Cesium 좌표 변환 → 1인칭 카메라 이동 |
| 나침반/자이로/드래그 회전 | 3가지 회전 모드 전환 (Gyro · Locked · Drag) |
| 다층 경로 계산 | 카카오 API → 도로 메쉬 A\* → NavMesh → 직선 순서로 폴백 |
| 3D 경로 시각화 | LineRenderer로 도로 위에 경로 표시 및 지나간 구간 자동 제거 |
| 장소 검색 | 카카오 로컬 API 키워드 검색 및 최근 검색 저장 |
| 미니맵 | RenderTexture 기반 위성 미니맵, 오버뷰 모드 지원 |
| 건물 충돌 보정 | GPS 오차로 건물 내부 진입 시 인접 도로로 자동 보정 |
| 도로 자산 대량 배치 | CSV 기반 가로수·가로등 등 에디터 일괄 배치 |

---

## 코드 구조

```
Assets/RoadTools/
├── Runtime/
│   ├── GPS/
│   │   ├── GPSLocationService.cs          # GPS 수신 및 Cesium 좌표 변환
│   │   ├── FirstPersonGPSController.cs    # 1인칭 카메라 제어 (위치·회전·보정)
│   │   └── CameraNavAnchor.cs            # 카메라 아래 지면 위치 앵커
│   ├── Navigation/
│   │   ├── NavigationCoordinator.cs       # 외부 진입점 (UI → 내부 서비스 위임)
│   │   ├── NavigationService.cs           # 경로 계산 핵심 엔진
│   │   ├── NavigationUIController.cs      # 길찾기 전체 UI/UX
│   │   ├── RouteRenderer.cs               # 3D 경로 LineRenderer 표시
│   │   ├── RoutePresenter.cs              # RouteRenderer + Minimap 통합
│   │   ├── PositionProvider.cs            # 현재 위치 정보 제공자
│   │   ├── KakaoApiKeyProvider.cs         # 카카오 API 키 관리
│   │   ├── KakaoDirectionsService.cs      # 카카오 모빌리티 경로 API
│   │   ├── KakaoPlaceSearchService.cs     # 카카오 로컬 장소 검색 API
│   │   └── POIData.cs                    # 관심지점 데이터 구조체
│   └── Minimap/
│       └── MinimapController.cs           # 미니맵 카메라 및 오버뷰 모드
├── Editor/
│   ├── RoadAssetPlacer.cs                # 도로 자산 배치 로직
│   ├── RoadAssetPlacerEditor.cs          # 에디터 Inspector UI
│   └── iOSBuildPostProcessor.cs          # iOS 빌드 후처리 (위치 권한)
└── README.md
```

**개별 컴포넌트 상세 문서:**

- `Runtime/GPS/GPSLocationService_README.md`
- `Runtime/GPS/FirstPersonGPSController_README.md`
- `Runtime/GPS/CameraNavAnchor_README.md`
- `Runtime/Minimap/MinimapController_README.md`

---

## 아키텍처

### 레이어 구성

```
┌──────────────────────────────────────────────────────┐
│                     UI 레이어                         │
│  NavigationUIController  (5가지 UI 상태 관리)          │
│  MinimapController       (RenderTexture 미니맵)        │
└───────────────────────┬──────────────────────────────┘
                        │
┌───────────────────────▼──────────────────────────────┐
│                  서비스 레이어                         │
│  NavigationCoordinator  (단일 진입점)                  │
│  NavigationService      (경로 계산 엔진)               │
│  RouteRenderer          (3D 경로 시각화)               │
│  PositionProvider       (위치 정보 집약)               │
└──────────┬──────────────────────┬────────────────────┘
           │                      │
┌──────────▼──────────┐  ┌────────▼───────────────────┐
│    GPS 레이어        │  │      외부 API 레이어         │
│  GPSLocationService │  │  KakaoDirectionsService    │
│  FirstPersonGPSCtrl │  │  KakaoPlaceSearchService   │
│  CameraNavAnchor    │  │  KakaoApiKeyProvider       │
└─────────────────────┘  └────────────────────────────┘
```

### 컴포넌트 간 의존 관계

```
FirstPersonGPSController
  └── GPSLocationService          (OnRawPositionUpdated 이벤트 구독)
  └── CesiumGlobeAnchor           (Transform 적용 대상)
  └── Cesium3DTileset             (지형 높이 샘플링)

NavigationCoordinator
  └── NavigationService           (경로 계산 위임)
  └── RoutePresenter              (시각화 위임)
  └── KakaoPlaceSearchService     (장소 검색 위임)

NavigationService
  └── KakaoDirectionsService      (API 경로)
  └── PositionProvider            (현재 위치)
  └── RouteRenderer               (경로 전달)

PositionProvider
  └── GPSLocationService          (GPS 좌표)
  └── CameraNavAnchor             (NavMesh 기준 위치)
```

---

## 경로 안내 파이프라인

### 1단계: GPS 위치 확보

```
기기 GPS 신호
  → GPSLocationService.GPSUpdateLoop()
  → Input.location.lastData 수신
  → WGS84 위도/경도/고도
  → CesiumWgs84Ellipsoid.LongitudeLatitudeHeightToEarthCenteredEarthFixed()
  → CesiumGeoreference.TransformEarthCenteredEarthFixedPositionToUnity()
  → Unity 월드 좌표 (Vector3)
  → OnRawPositionUpdated 이벤트 발행
```

### 2단계: 카메라 이동 및 높이 보정

```
OnRawPositionUpdated(rawUnityPos)
  → FirstPersonGPSController.OnGPSPositionUpdated()
  → X/Z: GPS 변환 위치 사용
  → Y: SampleAndUpdateGroundHeight() 비동기 호출
       ├── Cesium3DTileset.SampleHeightMostDetailed()  [우선]
       └── Physics.Raycast()                           [폴백]
  → _targetPosition = (GPS X/Z, 지면Y + eyeHeight)
  → Update()에서 Lerp 보간 이동
```

### 3단계: 목적지 설정 및 경로 계산

```
사용자: 장소 검색 입력
  → KakaoPlaceSearchService.SearchAsync(keyword, lat, lon)
  → 카카오 로컬 API 응답 → POIData 목록 반환
  → UI에서 목적지 선택
  → NavigationService.SetDestination(POI)
  → CalculateRoute() 호출
```

### 4단계: 다층 경로 계산 (우선순위 순)

```
CalculateRoute()
  │
  ├── [1순위] KakaoDirectionsService
  │     → https://apis-navi.kakaomobility.com/v1/directions
  │     → vertexes 배열 파싱 (경도/위도 쌍)
  │     → Unity 좌표 변환 → 경로 waypoints
  │
  ├── [2순위] 도로 메쉬 A*
  │     → Road 레이어 콜라이더로 그리드 생성
  │     → 그리드 해상도 자동 조정 (최대 셀 수 제한)
  │     → 8방향 A* 탐색
  │     → 불연속 구간 1셀 팽창 보완
  │
  ├── [3순위] NavMesh
  │     → NavMesh.CalculatePath(start, dest, mask, path)
  │
  └── [4순위] 직선 폴백
        → [출발지, 목적지] 2점 경로
```

### 5단계: 경로 시각화

```
OnRouteCalculated(waypoints)
  → RouteRenderer.ShowRoute(waypoints)
  → 각 waypoint에 높이 계산:
       ├── Road 레이어 Raycast  [우선]
       ├── 지형 Raycast         [차선]
       └── 카메라 위치 기반     [폴백]
  → NavMesh.CalculatePath()로 구간별 도로 굴곡 반영
  → LineRenderer 좌표 갱신
  → 목적지 마커 배치
```

### 6단계: 실시간 경로 갱신 및 도착 처리

```
Update() (NavigationService)
  → PositionProvider.NavPosition으로 현재 위치 갱신
  → RouteRenderer.TrimFromPlayerPosition()
       → 지나간 waypoint 제거
  → 목적지까지 거리 계산
  → 도착 반경(5m) 이내 진입 시
       → OnArrived 이벤트 발행
       → UI 상태: Navigating → Arrived
```

### UI 상태 전환

```
None (기본)
  → [검색 입력] → SearchOpen
       → [결과 선택] → MapOverview (경로 확인 지도)
            → [안내 시작] → Navigating (하단 카드 + 방향 안내)
                 → [도착] → Arrived (완료 오버레이)
                 → [취소] → None
```

---

## 회전 모드

`FirstPersonGPSController`에서 버튼 탭으로 순환 전환됩니다.

| 모드 | 입력 소스 | 설명 |
|---|---|---|
| **Gyro** | 나침반 (우선) / AttitudeSensor | 기기 방향을 자동으로 반영 |
| **Locked** | 없음 | 전환 시점 방향 고정 |
| **Drag** | 터치 / 마우스 드래그 | 사용자가 수동으로 시야 조작 |

---

## 에디터 도구: RoadAssetPlacer

CSV 파일로 도로 자산(가로수, 가로등 등)을 대량 배치하는 에디터 전용 도구입니다.

**선(Line) 데이터 CSV 포맷:**
```
id, startLat, startLon, endLat, endLon, ..., count
```

**점(Point) 데이터 CSV 포맷:**
```
열 인덱스 자유 지정, 헤더 선택 가능
```

**배치 모드:**

| 모드 | 방식 |
|---|---|
| NavMesh 기반 | 도로 경계선에 스냅 (Right / Left / Both / Nearest) |
| OSM 폴리라인 기반 | 중심선에서 측면 오프셋 (Center / Right / Left / Both) |

**자동 처리:**
- 타입별 그룹 오브젝트 생성
- `CesiumGlobeAnchor` 자동 추가
- Static Batching 및 그림자 옵션 일괄 적용

---

## 외부 의존성

| 의존성 | 용도 |
|---|---|
| Cesium for Unity | 지형 렌더링, 좌표 변환, GlobeAnchor |
| Kakao Mobility API | 도로망 기반 경로 계산 |
| Kakao Local API | 키워드 장소 검색 |
| Unity NavMesh | AI 경로 폴백 |
| Unity Input System | 터치·자이로·나침반 센서 |

카카오 API 키는 `Resources/kakao_api_key.txt`에 저장하거나 Inspector에서 `KakaoApiKeyProvider`에 직접 입력합니다.

---

## iOS 빌드 설정

`iOSBuildPostProcessor.cs`가 빌드 후 자동으로 `Info.plist`에 위치 권한 설명을 추가합니다.

```
NSLocationWhenInUseUsageDescription
```

별도 설정 없이 iOS 빌드 시 자동 처리됩니다.
