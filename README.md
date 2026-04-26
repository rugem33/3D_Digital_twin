# 3D Digital Twin — 모바일 1인칭 3D 지도

**Cesium for Unity 기반 GPS 연동 1인칭 보행자 내비게이션 앱**  
팀 15조 | Unity 2022 LTS | Cesium for Unity | Android / iOS

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

## 주요 기능 (Sprint 1 구현 완료)

| 기능 | 컴포넌트 | 관련 US |
|------|----------|---------|
| GPS → Unity 좌표 변환 (WGS84→ECEF→Unity, Lerp 보간) | `GPSLocationService` | US-04 |
| 1인칭 카메라 GPS 동기화 + 눈높이 보정 (2m) | `FirstPersonGPSController` | US-05 |
| Cesium 지형 높이 비동기 정밀 샘플링 | `FirstPersonGPSController` | US-16 |
| GPS 고도 노이즈 차단 (2m 이상 이동 시만 재감지) | `FirstPersonGPSController` | US-17 |
| 건물 내부 침투 감지 → 인접 도로 XZ 자동 복귀 | `FirstPersonGPSController` | US-18 |
| 회전 모드 3종 전환 (자이로 / 고정 / 드래그) | `FirstPersonGPSController` | US-11, US-12 |
| 백그라운드·포커스 복귀 시 AttitudeSensor 자동 재활성화 | `FirstPersonGPSController` | US-20 |
| Android / iOS 위치 권한 요청 팝업 | `LocationPermissionHandler` | US-03 |
| Cesium Ion 3D 타일 스트리밍 | 씬 설정 | US-01, US-02 |
| 탑뷰 미니맵 (직교 카메라 RenderTexture) | `MinimapController` | US-19 |
| CSV / JSON 좌표 기반 시설물 자동 배치 | `RoadAssetPlacer` | US-06, US-07 |
| 시설물 타입별 가시성 On/Off | `RoadAssetPlacer` | US-08 |
| NavMesh 경로 기반 가로수 자동 배치 | `RoadAssetPlacer` | US-07 |
| 배치 메쉬 → 별도 순수 메쉬 오브젝트 분리 | `RoadAssetPlacer` | US-08 |
| iOS Xcode Info.plist 위치 권한 자동 주입 | `iOSBuildPostProcessor` | US-03 |

---

## 아키텍처

```
┌─────────────────────────────────────────────────────┐
│  Presentation Layer                                 │
│  FirstPersonGPSController  MinimapController        │
│  LocationPermissionHandler  OnGUI 회전 모드 버튼    │
└───────────────────────┬─────────────────────────────┘
                        │
┌───────────────────────▼─────────────────────────────┐
│  Application Layer                                  │
│  GPSLocationService   (WGS84 → ECEF → Unity, Lerp)  │
│  RoadAssetPlacer      (NavMesh + Raycast 배치)       │
└───────────────────────┬─────────────────────────────┘
                        │
┌───────────────────────▼─────────────────────────────┐
│  Infrastructure Layer (Cesium for Unity)            │
│  CesiumGeoreference  Cesium3DTileset                │
│  CesiumGlobeAnchor   NavMeshSurface                 │
└─────────────────────────────────────────────────────┘
```

### 좌표 변환 파이프라인

```
GPS 수신 (WGS84: 위도, 경도, 고도)
  ↓
CesiumWgs84Ellipsoid.LongitudeLatitudeHeightToEarthCenteredEarthFixed()
  ↓
ECEF (지구 중심 고정 좌표계)
  ↓
CesiumGeoreference.TransformEarthCenteredEarthFixedPositionToUnity()
  ↓
Unity 월드 좌표 (Vector3)
  ↓
Vector3.Lerp() 보간 (speed=8) → 카메라 떨림 방지
  ↓
Cesium SampleHeightMostDetailed (비동기) → 지면 Y 캐싱
  ↓
카메라 최종 위치 = (X, 지면Y + 눈높이 2m, Z)
```

---

## 코드 구조

```
Assets/
└── RoadTools/
    ├── Runtime/
    │   ├── GPS/
    │   │   ├── GPSLocationService.cs          # US-04  WGS84→Unity 변환 + Lerp 보간
    │   │   ├── FirstPersonGPSController.cs    # US-05,11,12,16,17,18,20  1인칭 카메라 제어
    │   │   ├── LocationPermissionHandler.cs   # US-03  위치 권한 요청
    │   │   └── MinimapController.cs           # US-19  탑뷰 미니맵
    │   ├── RoadAssetPlacer.cs                 # US-06,07,08  시설물 자동 배치
    │   └── Rugem.RoadTools.Runtime.asmdef
    └── Editor/
        ├── RoadAssetPlacerEditor.cs           # Inspector CSV 배치 UI
        ├── iOSBuildPostProcessor.cs           # iOS 위치 권한 Info.plist 자동 주입
        └── Rugem.RoadTools.Editor.asmdef
```

---

## 기술 스택

| 분류 | 내용 |
|------|------|
| **엔진** | Unity 2022 LTS |
| **GIS 프레임워크** | Cesium for Unity v1.23.1+ |
| **좌표계** | WGS84 (GPS) → ECEF → Unity World |
| **입력** | New Input System — Touchscreen, AttitudeSensor |
| **내비게이션** | Unity AI Navigation (NavMesh) |
| **빌드 타겟** | Android, iOS |
| **데이터 소스** | Cesium Ion, 공공 CSV/GeoJSON |

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

### Sprint 1 완료 (54 / 98 SP)

| US | 제목 | SP | 담당 | 상태 |
|----|------|----|------|------|
| US-01 | Cesium Ion 서버 연동 | 3 | 유성 | ✅ |
| US-02 | 대상 지역 데이터 최적화 | 5 | 유성 | ✅ |
| US-03 | 모바일 위치 권한 획득 | 2 | 혜린 | ✅ |
| US-04 | 지리좌표계 변환 엔진 | 8 | 현서 | ✅ |
| US-05 | 1인칭 카메라 위치 동기화 | 5 | 현서 | ✅ |
| US-06 | 외부 좌표 데이터 파싱 | 3 | 유성 | ✅ |
| US-07 | 데이터 기반 객체 자동 배치 | 5 | 유성 | ✅ |
| US-08 | 시설물 가시성 필터링 | 3 | 유성 | ✅ |
| US-11 | 터치 기반 시점 조작 | 5 | 혜린 | ✅ |
| US-12 | 조작 모드 심리스 전환 | 3 | 혜린 | ✅ |
| US-16 | Cesium 지형 높이 정밀 측정 | 8 | 현서 | ✅ |
| US-17 | GPS 고도 노이즈 차단 | 3 | 현서 | ✅ |
| US-18 | 건물 내부 침투 감지 | 5 | 현서 | ✅ |
| US-19 | 탑뷰 미니맵 | 5 | 혜린 | ✅ |
| US-20 | 백그라운드 센서 재활성화 | 2 | 혜린 | ✅ |

### Sprint 2 예정 (25 SP)

| US | 제목 | SP | Priority |
|----|------|----|----------|
| US-09 | 자전거 도로 폴리라인 생성 | 5 | Must |
| US-10 | 자전거 도로 특화 셰이더 | 5 | Should |
| US-13 | '내 위치' 복귀 버튼 | 2 | Must |
| US-14 | 실시간 경로 가이드 시각화 | 8 | Should |
| US-15 | 거리 기반 LOD 및 컬링 | 5 | Must |

---

## 팀 구성

| 역할 | 이름 | 학번 | 담당 |
|------|------|------|------|
| Product Owner | 유성 | 202102669 | Cesium 인프라, 시설물 배치 (RoadAssetPlacer) |
| Scrum Master | 여현서 | 202102664 | GPS·카메라 시스템 (GPSLocationService, FirstPersonGPSController) |
| Developer | 이혜린 | 202300385 | UI/UX, 권한 처리, 미니맵 |
| 지도교수 | 김형기 교수님 | — | — |

---

## 빠른 시작

### 필수 패키지

- Cesium for Unity (`com.cesium.unity`) v1.23.1 이상
- Unity AI Navigation (`com.unity.ai.navigation`)
- Input System (`com.unity.inputsystem`)

### 씬 설정

1. `CesiumGeoreference` 오브젝트 생성 후 대상 지역 위경도 설정
2. `Cesium3DTileset`에 Cesium Ion Asset ID 입력
3. 카메라 GameObject에 `GPSLocationService` + `FirstPersonGPSController` 추가
4. `FirstPersonGPSController`의 `World Terrain` 필드에 지형 Tileset 연결
5. `RoadAssetPlacer` 오브젝트 생성 후 `assetPrefab` 및 `roadLayerMask` 설정
6. Inspector의 CSV 배치 버튼으로 시설물 데이터 로드

### CSV 형식

**선(Line) 데이터** — 가로수·가로등 노선
```
id, 시작위도, 시작경도, 종료위도, 종료경도, (기타), 갯수
```

**점(Point) 데이터** — 버스정류장·표지판 등
```
..., 위도, 경도, ...   (열 인덱스는 Inspector에서 지정)
```
