# 지형 & 오버레이 기능 설계 세션 요약

> 작성일: 2026-05-14  
> 브랜치: app_beta6-3

---

## 목차

1. [구현 가능 여부 검토](#1-구현-가능-여부-검토)
2. [기능 2 테스트에 필요한 파일](#2-기능-2-테스트에-필요한-파일)
3. [거리별 라벨 표시 LOD 시스템](#3-거리별-라벨-표시-lod-시스템)
4. [V-World 오버레이 구현](#4-v-world-오버레이-구현)
5. [World Terrain 제거 가능 여부](#5-world-terrain-제거-가능-여부)
6. [3D 지형 메쉬 생성 방식 비교](#6-3d-지형-메쉬-생성-방식-비교)
7. [기업 도입 관점 비교](#7-기업-도입-관점-비교)
8. [방식 A vs 방식 C 차이](#8-방식-a-vs-방식-c-차이)
9. [방식 C 테스트 구현](#9-방식-c-테스트-구현)
10. [quantized-mesh 디버그 및 서버 수정](#10-quantized-mesh-디버그-및-서버-수정)
11. [생성된 파일 목록](#11-생성된-파일-목록)

---

## 1. 구현 가능 여부 검토

### 기능 1: SHP 파일 → 건물 메쉬 자동 배치

- **가능 여부**: 가능 (복잡도: 중~고)
- SHP 파일 파싱 → Polygon FootPrint → Triangulation → Unity Mesh
- WGS84 좌표를 `CesiumGeoreference` + `CesiumGlobeAnchor`로 Unity 좌표 변환
- 높이 보정: `Cesium3DTileset.SampleHeightMostDetailed()` 활용

| 역할 | 방법 |
|------|------|
| SHP 파싱 | NetTopologySuite DLL 또는 순수 C# 파서 |
| Polygon → Mesh | Ear Clipping Triangulation |
| 좌표 변환 | `CesiumGeoreference.TransformECEFToUnity()` |
| 높이 보정 | `Cesium3DTileset.SampleHeightMostDetailed()` |

### 기능 2: 커스텀 VirtualGlobe (World Terrain) 생성

- **가능 여부**: 가능 (복잡도: 낮~중)
- Cesium for Unity 1.23.1 기준 지원 오버레이 클래스:

| 클래스 | 용도 |
|--------|------|
| `CesiumUrlTemplateRasterOverlay` | XYZ/슬리피맵 타일 (V-World 등) |
| `CesiumTileMapServiceRasterOverlay` | TMS 형식 |
| `CesiumWebMapServiceRasterOverlay` | WMS (GeoServer 등) |
| `CesiumWebMapTileServiceRasterOverlay` | WMTS (V-World, 네이버 등) |

---

## 2. 기능 2 테스트에 필요한 파일

### 방법 A: Cesium Ion (추가 파일 불필요)

- `ion.cesium.com.asset`에 토큰 이미 설정됨
- `Cesium3DTileset` > `ionAssetID = 1` → Cesium World Terrain

### 방법 B: V-World (API 키만 필요)

- **V-World API 키**: `https://map.vworld.kr` 에서 무료 신청
- 키 저장 위치: `Assets/Resources/vworld_api_key.txt`
- Unity 설정: `CesiumUrlTemplateRasterOverlay` 컴포넌트 추가

```
URL 템플릿:
https://api.vworld.kr/req/wmts/1.0.0/{KEY}/Satellite/{z}/{reverseY}/{x}.jpeg
```

> `{reverseY}` = y=0이 북쪽인 XYZ 방식 → V-World TileRow에 대응

### 방법 C: 국토지리정보원 DEM (커스텀 지형)

1. 국토지리정보원 수치표고모델 `.tif` 다운로드
2. Cesium Ion 업로드 (무료 플랜 5GB)
3. Ion이 자동으로 quantized-mesh 변환
4. Unity: `ionAssetID = 업로드된 ID`

**권장 순서**: 방법 A → 방법 B (V-World 오버레이 추가) → 방법 C (커스텀 DEM)

---

## 3. 거리별 라벨 표시 LOD 시스템

- **구현 가능 여부**: 가능 (Cesium 내장 기능 아님, 직접 구현 필요)

### 동작 원리

```
카메라 고도 (CesiumGlobeAnchor에서 추출)
  ├─ > 10,000m  →  시/도 레벨 라벨 (광역시, 도)
  ├─ 1,000~10,000m  →  구/군/읍면동 레벨 라벨
  └─ < 1,000m   →  도로명/건물명 라벨
```

### 데이터 소스

기존 `KakaoPlaceSearchService.cs` 활용 → Kakao 역지오코딩 API 추가:

```
GET https://dapi.kakao.com/v2/local/geo/coord2regioncode.json?x={lng}&y={lat}
→ region_1depth_name (시/도)
→ region_2depth_name (구/군)
→ region_3depth_name (동/읍)
```

### 구현 구조 (스크립트 1개)

```csharp
public class LODLabelManager : MonoBehaviour
{
    void Update()
    {
        double alt = cameraAnchor.height;
        if      (alt > 10000f) ActivateLayer(LabelLayer.Region);
        else if (alt > 1000f)  ActivateLayer(LabelLayer.District);
        else                   ActivateLayer(LabelLayer.Street);
    }
}
```

라벨 오브젝트: `CesiumGlobeAnchor` + `Canvas(World Space)` + `TextMeshPro`

---

## 4. V-World 오버레이 구현

### 생성된 파일

```
Assets/RoadTools/Runtime/VWorldOverlay/
├── VWorldApiKeyProvider.cs       ← API 키 로드/캐싱 (KakaoApiKeyProvider 패턴)
└── VWorldOverlayController.cs    ← 오버레이 관리 메인

Assets/RoadTools/Editor/
└── VWorldOverlayEditor.cs        ← Inspector 테스트 버튼

Assets/Resources/
└── vworld_api_key.txt            ← API 키 입력 파일
```

### VWorldLayerType

```csharp
public enum VWorldLayerType
{
    Satellite,  // 위성 영상 (.jpeg)
    Base,       // 일반 지도 (.png)
    Hybrid      // 위성 + 도로/지명 (.png)
}
```

### 사용법

1. 씬의 빈 GameObject에 `VWorldOverlayController` 컴포넌트 추가
2. **Target Tileset** → `Cesium3DTileset` GameObject 연결
3. `Assets/Resources/vworld_api_key.txt`에 키 입력
4. Play → Inspector 버튼으로 실시간 레이어 전환

### 주의: CesiumRasterOverlay 제약

`CesiumRasterOverlay`는 반드시 `Cesium3DTileset`과 **같은 GameObject**에 있어야 합니다.  
`VWorldOverlayController`가 런타임에 `_targetTileset.gameObject.AddComponent<>()`로 자동 처리합니다.

---

## 5. World Terrain 제거 가능 여부

### 결론: 제거하면 안 됨

```
Cesium3DTileset (World Terrain)
  ├── 역할 1: 3D 지형 메쉬 (산, 골짜기 높낮이)  ← V-World가 대체 불가
  └── 역할 2: 기본 위성이미지 (Bing 등)         ← 이것만 V-World가 대체

CesiumUrlTemplateRasterOverlay (V-World)
  └── 역할: 이미지(텍스처)만 교체
```

### 추가 영향: GPS 카메라 높이 샘플링

`FirstPersonGPSController.cs:754`:

```csharp
if (_worldTerrain != null && _worldTerrain.isActiveAndEnabled)
{
    CesiumSampleHeightResult result =
        await _worldTerrain.SampleHeightMostDetailed(new double3(lon, lat, 0.0));
}
```

World Terrain 제거 시 `SampleHeightMostDetailed()` 불가 → GPS 카메라 높이 정밀도 저하

### 올바른 사용 구조

```
[World Terrain] Cesium3DTileset (유지)
  ├── CesiumIonRasterOverlay (기존 이미지) ← 비활성화하면 V-World만 표시
  └── CesiumUrlTemplateRasterOverlay (V-World) ← 새로 추가
```

---

## 6. 3D 지형 메쉬 생성 방식 비교

### 방식 A: Cesium Ion 업로드

```
국토지리정보원 수치표고모델(.tif)
  → Cesium Ion 업로드 → quantized-mesh 자동 변환
  → Cesium3DTileset.ionAssetID = 업로드된 ID
```

| 항목 | 내용 |
|------|------|
| 코드 작업 | `ionAssetID` 숫자 하나 교체 |
| SampleHeightMostDetailed() | 완전 호환 |
| 제약 | Ion 무료 플랜 5GB 한도 |

### 방식 B: DEM → Unity Mesh 직접 생성

```
DEM 파일 (GeoTIFF / raw heightmap)
  → C# 파서로 고도 배열 추출 → Mesh.vertices[] 그리드 생성
  → CesiumGlobeAnchor로 WGS84 위치 고정
```

| 항목 | 내용 |
|------|------|
| SampleHeightMostDetailed() | **불가** (별도 구현 필요) |
| 장점 | 완전 오프라인, 외부 서비스 없음 |

### 방식 C: 자체 서버 URL (quantized-mesh)

```csharp
tileset.tilesetSource = CesiumDataSource.FromUrl;
tileset.url = "https://내부서버/terrain/layer.json";
```

| 항목 | 내용 |
|------|------|
| 타일 포맷 | quantized-mesh (방식 A와 동일) |
| Ion 의존 | 없음 |
| 서버 도구 | CTB (Cesium Terrain Builder) + nginx |

---

## 7. 기업 도입 관점 비교

### 기업 핵심 요구사항

| 기업 요구사항 | 방식 A (Ion) | 방식 C (자체 서버) |
|-------------|-------------|-----------------|
| 사내망(인트라넷) 동작 | ✗ | ✓ |
| 데이터 외부 유출 없음 | ✗ (Ion 클라우드 경유) | ✓ |
| 고객사 자체 DEM 사용 | 번거로움 | ✓ |
| Cesium Ion 비용 | 유료 (상업용) | 없음 |
| 오프라인 환경 | ✗ | ✓ |

### 카카오/네이버 데이터 활용 가능성

- 카카오·네이버는 **DEM/지형 메쉬 API를 공개하지 않음**
- 제공 데이터: 지도 이미지 타일, 길찾기, 장소 검색만 가능
- 3D 지형 메쉬 소스: **국토지리정보원(V-World)** 만 가능

### 확장성 있는 아키텍처

```
레이어 3: 서비스 데이터    카카오 경로/장소 (이미 구현)
레이어 2: 이미지 오버레이  V-World / 카카오맵 타일 / 네이버맵 타일
          ↕ CesiumUrlTemplateRasterOverlay (교체 가능)
레이어 1: 지형 메쉬        Cesium Ion → 자체 서버로 교체 가능
          ↕ Cesium3DTileset.ionAssetID / .url (한 줄 교체)
레이어 0: 좌표계           CesiumGeoreference (WGS84 고정)
```

### 결론

| | 개인/소규모 | 기업 납품 |
|--|------------|---------|
| 방식 A (Ion) | 최적 | 비용·의존성 문제 |
| 방식 B (Unity Mesh) | 과도한 작업 | 품질 한계 |
| **방식 C (자체 서버)** | 구축 비용 있음 | **최적** |

**현실적 전략**: 방식 A로 빠르게 개발 → `ionAssetID` → `url` 교체 가능하도록 설계 유지

---

## 8. 방식 A vs 방식 C 차이

```
방식 A                          방식 C
─────────────────────────────────────────────────
데이터 위치   Cesium 클라우드       고객사 서버
네트워크      외부 인터넷 필수      사내망만으로 가능
비용          Ion 사용료 발생       서버 운영비만
데이터 주권   Cesium 서버 경유      완전 내부 보관
코드 차이     ionAssetID = 숫자    url = "주소"
타일 포맷     quantized-mesh      quantized-mesh (동일)
Unity 코드    ← 이 부분은 완전히 동일 →
```

타일을 어디서 가져오느냐만 다르고, Unity 코드와 Cesium 렌더링 품질은 동일합니다.

---

## 9. 방식 C 테스트 구현

### 생성된 파일

```
Assets/RoadTools/Runtime/TerrainSource/
└── TerrainSourceSwitcher.cs       ← 지형 소스 전환 컨트롤러

Assets/RoadTools/Editor/
└── TerrainSourceSwitcherEditor.cs ← Inspector 버튼 (현재 모드 색상 표시)

Tools/
└── terrain_test_server.py         ← 로컬 quantized-mesh 타일 서버 (Python)
```

### TerrainSourceMode

```csharp
public enum TerrainSourceMode
{
    CesiumIon,   // 방식 A: Cesium Ion 클라우드
    CustomUrl,   // 방식 C: 자체 서버 URL
    Ellipsoid    // 외부 서비스 없이 평탄 타원체
}
```

### 테스트 순서

```bash
pip install flask
python Tools/terrain_test_server.py
```

Unity:
1. 빈 GameObject → `Add Component` → `RoadTools > Terrain Source Switcher`
2. **Target Tileset** → `Cesium3DTileset` GameObject 연결
3. **Mode** → `CustomUrl`
4. **Terrain Url** → `http://localhost:5001/layer.json`
5. Play → Inspector 버튼으로 Ion ↔ CustomUrl ↔ Ellipsoid 전환

---

## 10. quantized-mesh 디버그 및 서버 수정

### 발견된 버그

**버그 1: 좌표계 공식 오류 (핵심)**

| | 이전 코드 | 수정 후 |
|--|----------|---------|
| 투영 방식 | Web Mercator (`atan(sinh(...))`) | Geographic 등장방형 (선형 경위도) |
| x 타일 수 | `2^z` | `2^(z+1)` (경도 360° = 위도 180° × 2) |

Cesium 지형 타일은 Web Mercator가 아닌 **Geographic(Plate Carrée) 투영**을 사용합니다.

```python
# 수정 전 (잘못됨 — Web Mercator 공식)
lat = math.degrees(math.atan(math.sinh(math.pi * (1 - 2 * y / n))))

# 수정 후 (올바름 — Geographic 등장방형)
x_count = 2 ** (z + 1)
y_count = 2 ** z
lon_min = (x       / x_count) * 360.0 - 180.0
lat_min = (y_tms   / y_count) * 180.0 - 90.0
```

**버그 2: 평탄 지형 가시성**

고도 0m 평탄 지형은 텍스처 없이 타원체와 시각적으로 동일합니다.  
→ `VWorldOverlayController`로 V-World 위성 이미지를 함께 적용해야 확인 가능합니다.

### 서버 로그로 확인

```
[META] layer.json 요청
[TILE] z=0 x=0 y=0  lon[-180.0~0.0] lat[-90.0~90.0]
[TILE] z=0 x=1 y=0  lon[0.0~180.0]  lat[-90.0~90.0]
```

로그가 찍히면 타일 로드 성공 → V-World 오버레이 적용으로 시각 확인

### 공개 quantized-mesh 서버 여부

**인증 없이 자유롭게 쓸 수 있는 공개 quantized-mesh 서버는 현재 없습니다.**  
Cesium Ion 외 STK terrain(AGI/Ansys)이 있으나 등록 필요.

---

## 11. 생성된 파일 목록

| 파일 | 역할 |
|------|------|
| `Assets/RoadTools/Runtime/VWorldOverlay/VWorldApiKeyProvider.cs` | V-World API 키 로드/캐싱 |
| `Assets/RoadTools/Runtime/VWorldOverlay/VWorldOverlayController.cs` | V-World WMTS 오버레이 관리 |
| `Assets/RoadTools/Editor/VWorldOverlayEditor.cs` | V-World 오버레이 Inspector UI |
| `Assets/Resources/vworld_api_key.txt` | V-World API 키 입력 파일 |
| `Assets/RoadTools/Runtime/TerrainSource/TerrainSourceSwitcher.cs` | 지형 소스 전환 컨트롤러 |
| `Assets/RoadTools/Editor/TerrainSourceSwitcherEditor.cs` | 지형 소스 전환 Inspector UI |
| `Tools/terrain_test_server.py` | 로컬 quantized-mesh 지형 타일 서버 |
