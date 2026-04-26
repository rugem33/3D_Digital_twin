"""
양식 5 - 시스템 구조 설계 및 개발 환경 (v2)
템플릿 구조(0~5섹션) 그대로 유지, 내용만 프로젝트 내용으로 채움
"""
import shutil
from docx import Document
from docx.shared import Pt, RGBColor, Inches, Cm
from docx.enum.text import WD_ALIGN_PARAGRAPH
from docx.enum.table import WD_TABLE_ALIGNMENT
from docx.oxml.ns import qn
from docx.oxml import OxmlElement

TEMPLATE = "[양식 5] 시스템 구조 설계 및 개발 환경 양식.docx"
OUTPUT   = "[양식 5] 시스템 구조 설계 및 개발 환경 완성_v2.docx"

shutil.copy(TEMPLATE, OUTPUT)
doc = Document(OUTPUT)

# 기존 내용 삭제
for p in list(doc.paragraphs):
    p._element.getparent().remove(p._element)
for t in list(doc.tables):
    t._element.getparent().remove(t._element)

# ── 헬퍼 ─────────────────────────────────────────────────────────────────────

def p_add(text="", bold=False, size=None, indent=None, align=None, color=None):
    para = doc.add_paragraph()
    if align:
        para.alignment = align
    if indent:
        para.paragraph_format.left_indent = Cm(indent)
    if text:
        run = para.add_run(text)
        if bold:
            run.bold = True
        if size:
            run.font.size = Pt(size)
        if color:
            run.font.color.rgb = RGBColor(*color)
    return para

def heading(text, level=1):
    try:
        h = doc.add_heading(text, level=level)
    except Exception:
        h = doc.add_paragraph()
        r = h.add_run(text)
        r.bold = True
        r.font.size = Pt(14 - level * 2)
    return h

def shade_cell(cell, hex_color="D9E1F2"):
    shd = OxmlElement('w:shd')
    shd.set(qn('w:val'), 'clear')
    shd.set(qn('w:color'), 'auto')
    shd.set(qn('w:fill'), hex_color)
    cell._tc.get_or_add_tcPr().append(shd)

def table(headers, rows, col_widths=None, header_color="D9E1F2"):
    tbl = doc.add_table(rows=1 + len(rows), cols=len(headers))
    tbl.style = 'Table Grid'
    tbl.alignment = WD_TABLE_ALIGNMENT.CENTER
    # 헤더
    for i, h in enumerate(headers):
        c = tbl.rows[0].cells[i]
        c.text = h
        c.paragraphs[0].runs[0].bold = True
        c.paragraphs[0].alignment = WD_ALIGN_PARAGRAPH.CENTER
        shade_cell(c, header_color)
    # 데이터
    for ri, row in enumerate(rows):
        for ci, val in enumerate(row):
            c = tbl.rows[ri+1].cells[ci]
            c.text = val
    # 열 너비
    if col_widths:
        for r in tbl.rows:
            for i, c in enumerate(r.cells):
                c.width = Cm(col_widths[i])
    return tbl

# ════════════════════════════════════════════════════════════════════════════
# 표지
# ════════════════════════════════════════════════════════════════════════════
cover = doc.add_paragraph()
cover.alignment = WD_ALIGN_PARAGRAPH.CENTER
r = cover.add_run("Project Document\nSystem Architecture Design\n& Development Environments")
r.bold = True; r.font.size = Pt(20)

doc.add_paragraph()
p_add("Project Name", bold=True, size=14, align=WD_ALIGN_PARAGRAPH.CENTER)
p_add("Cesium for Unity 기반 모바일 1인칭 3D 지도", bold=True, size=13, align=WD_ALIGN_PARAGRAPH.CENTER)
doc.add_paragraph()
for line in ["15 조", "202002581  황용하", "202300385  이혜린",
             "202102664  여현서", "202102669  유성", "", "지도교수: 김형기 교수님"]:
    p_add(line, align=WD_ALIGN_PARAGRAPH.CENTER)

doc.add_page_break()

# ════════════════════════════════════════════════════════════════════════════
# Document Revision History
# ════════════════════════════════════════════════════════════════════════════
heading("Document Revision History", 1)
table(
    ["Rev#", "Date", "Affected Section", "Author"],
    [["1", "2026/04/25", "시스템 구조 설계 및 개발 환경 최초 작성", "공통"]],
    col_widths=[2, 3, 8, 3],
)
doc.add_paragraph()

# ════════════════════════════════════════════════════════════════════════════
# 제목
# ════════════════════════════════════════════════════════════════════════════
heading("System Design Document – Cesium for Unity 기반 모바일 1인칭 3D 지도", 1)

# ════════════════════════════════════════════════════════════════════════════
# 0. Meta Information
# ════════════════════════════════════════════════════════════════════════════
heading("0. Meta Information", 2)
table(
    ["항목", "내용"],
    [
        ["Project",   "Cesium for Unity 기반 모바일 1인칭 3D 지도"],
        ["Team",      "15조 (황용하, 이혜린, 여현서, 유성)"],
        ["Version",   "v0.1.0"],
        ["Framework", "Unity 2022.3 LTS · Cesium for Unity · URP"],
        ["Scope",     "US-01 ~ US-15"],
    ],
    col_widths=[4, 12],
)
doc.add_paragraph()

# ════════════════════════════════════════════════════════════════════════════
# 1. Project Overview
# ════════════════════════════════════════════════════════════════════════════
heading("1. Project Overview", 1)

heading("Vision", 2)
p_add(
    "도보 및 자전거 이용자는 기존 2D 지도나 드론 뷰 기반 3D 지도로는 지면 밀착형 공간 정보를 "
    "직관적으로 파악하기 어렵다는 문제가 있다. "
    "본 프로젝트는 Cesium for Unity를 활용해 실제 지형 위에 1인칭 보행자 시점을 구현하고, "
    "GPS 실시간 동기화·커스텀 시설물(가로등·벤치) 자동 배치·자전거 전용 도로 시각화를 통해 "
    "단순 위치 확인을 넘어 상세 노면 상태와 주변 환경을 체감할 수 있는 실감형 길찾기 경험을 "
    "모바일 사용자에게 제공한다."
)
doc.add_paragraph()

heading("Scope", 2)
table(
    ["구분", "내용"],
    [
        ["In Scope",
         "3D 지형 스트리밍(Cesium Ion) / 실시간 GPS 동기화 / 1인칭 카메라 제어 / "
         "커스텀 시설물 자동 배치(가로등·벤치) / 자전거 도로 폴리라인 시각화 / "
         "터치 기반 시점 조작 / 경로 가이드 시각화 / 모바일 성능 최적화"],
        ["Out of Scope",
         "결제 시스템 / 서버 사이드 사용자 인증 / 실시간 교통 정보 연동 / "
         "다중 사용자 협업 기능 / PC·콘솔 빌드"],
    ],
    col_widths=[3, 13],
)
doc.add_paragraph()

heading("Success Metrics", 2)
metrics = [
    "GPS-엔진 좌표 정밀도: WGS84 → Unity 3D 좌표 매핑 오차 실외 기준 3 m 이내",
    "실시간 위치 업데이트 지연: 카메라 위치 갱신 지연 500 ms 이내 (Lerp 보간 적용)",
    "하이브리드 조작 안정성: GPS↔터치 모드 전환 시 카메라 Jitter 발생률 1 % 미만",
    "데이터 렌더링 부하: GPS 이동 및 3D Tiles 스트리밍 중 최저 30 FPS 유지",
    "객체 가시 거리: 자전거 도로·가로등 등 주요 시설물 50 m 이상 가시 거리 확보",
]
for m in metrics:
    p_add(m, indent=0.5)
doc.add_paragraph()

# ════════════════════════════════════════════════════════════════════════════
# 2. Architecture Design
# ════════════════════════════════════════════════════════════════════════════
heading("2. Architecture Design", 1)

heading("Layered Architecture", 2)
table(
    ["레이어", "구성 요소", "책임"],
    [
        ["Presentation\n(View / UI)",
         "MinimapController\nFirstPersonGPSController (OnGUI)\nLocationPermissionHandler (Panel)",
         "화면 렌더링, 사용자 터치 입력 수신, 권한 안내 UI 상태 표시"],
        ["Application\n(Service / Controller)",
         "FirstPersonGPSController\nGPSLocationService\nRoadAssetPlacer\n"
         "BikeRouteRenderer (예정)\nNavigationGuide (예정)",
         "GPS 수신 및 좌표 변환, 1인칭 카메라 동기화,\n"
         "시설물 자동 배치, 자전거 도로 메쉬 생성, 경로 안내 로직"],
        ["Infrastructure\n(외부 연동)",
         "CesiumGeoreference · Cesium3DTileset\nInput.location (GPS HW)\n"
         "NavMeshSurface · NavMesh API\nPhysics.Raycast\nAttitudeSensor (자이로)",
         "Cesium Ion 3D Tiles 스트리밍, 모바일 GPS 하드웨어 접근,\n"
         "런타임 NavMesh 빌드, 물리 지면 감지, 방향 센서"],
        ["Domain\n(Model / Data)",
         "WGS84 좌표 (double lat/lon/alt)\nCSV / JSON 파싱 DTO\n"
         "NavMeshPath.corners[]\nRenderTexture (미니맵)",
         "핵심 데이터 구조 보관, 좌표 변환 결과, 경로 좌표 배열"],
    ],
    col_widths=[3.5, 6, 7.5],
)
doc.add_paragraph()

heading("Core Flow 1 – GPS 실시간 동기화 & 1인칭 카메라 추적 (US-03·04·05)", 2)
flow1 = [
    "① App 시작 → LocationPermissionHandler: Android Permission.RequestUserPermission(FineLocation)",
    "   iOS: Input.location.Start() 상태 폴링 → 권한 허용 시 OnPermissionGranted 이벤트",
    "② FirstPersonGPSController → GPSLocationService.StartGPS() 코루틴 시작",
    "③ [Loop 1초] Input.location.lastData 수신",
    "   → WGS84(lat, lon, alt) → ECEF: CesiumWgs84Ellipsoid.LLH→ECEF()",
    "   → ECEF → Unity World: CesiumGeoreference.TransformECEFToUnity()",
    "   → TargetUnityPosition 갱신 / OnRawPositionUpdated 이벤트 발생",
    "④ GPSLocationService.Update(): SmoothedUnityPosition = Lerp(current, target, dt × lerpSpeed)",
    "⑤ FirstPersonGPSController.OnGPSPositionUpdated()",
    "   → Cesium3DTileset.SampleHeightMostDetailed() 비동기 → 실제 지형 고도 획득",
    "   → 실패 시 Physics.Raycast 폴백 → _cachedGroundY 갱신",
    "   → _targetPosition = (x, groundY + eyeHeight, z)",
    "⑥ FirstPersonGPSController.Update()",
    "   → transform.position = Lerp(current, _targetPosition, dt × posLerpSpeed)",
    "   → 건물 충돌 감지(0.2초 간격): 수평 4방향 Raycast → 건물 내부 시 인접 도로 XZ 고정",
    "⑦ MinimapController.LateUpdate(): 미니맵 카메라가 플레이어 위 400 m 추적",
]
for line in flow1:
    p_add(line, indent=0.5)
doc.add_paragraph()

heading("Core Flow 2 – 커스텀 시설물 자동 배치 (US-06·07)", 2)
flow2 = [
    "① CSV 파일 파싱 → 시설물 좌표 쌍 (startLat, startLon, endLat, endLon) 추출",
    "② RoadAssetPlacer.BuildNavMesh()",
    "   → Cesium 타일 로드 완료 후 NavMeshSurface.BuildNavMesh() (런타임 동적 빌드)",
    "③ RoadAssetPlacer.PlaceTreeLine(lat1, lon1, lat2, lon2)",
    "   → WGS84 → ECEF → Unity World 1차 변환",
    "   → NavMesh.SamplePosition(): 공중 좌표를 가장 가까운 노면으로 Snap",
    "   → NavMesh.CalculatePath(): 직선 좌표 사이의 실제 도로 곡선(Corners) 역추적",
    "④ 각 Corner 구간을 treeInterval(10 m) 간격으로 보간",
    "   → Physics.Raycast(down): 정확한 지면 높이·법선 획득",
    "   → Prefab Instantiate → CesiumGlobeAnchor 부착(detectTransformChanges = false)",
    "⑤ StaticBatchingUtility.Combine(): 드로우 콜 최적화",
    "",
    "   ※ PlacePointAsset(lat, lon): 단일 좌표 시설물(버스정류장 등) 배치",
    "      WGS84 → ECEF → Unity World → Raycast → 지면 안착",
]
for line in flow2:
    p_add(line, indent=0.5)
doc.add_paragraph()

heading("Core Flow 3 – 터치 조작 & 모드 전환 (US-11·12·13)", 2)
flow3 = [
    "① FirstPersonGPSController.Update() → RotationMode 분기",
    "   Gyro:   AttitudeSensor.attitude → GyroToWorldRotation() → Slerp 적용",
    "   Locked: 현재 Quaternion 고정",
    "   Drag:   Touchscreen.primaryTouch.delta → dragYaw/Pitch 누적 → Euler 회전",
    "② GUI 버튼 클릭: CycleRotationMode() → Gyro → Locked → Drag 순환",
    "③ [US-12 예정] 터치 입력 감지 시 _isManualMode = true → GPS 위치 적용 스킵",
    "   일정 시간(3초) 터치 없으면 _isManualMode = false 자동 복귀",
    "④ [US-13 예정] '내 위치' UI 버튼 클릭",
    "   → _targetPosition = GPSLocationService.SmoothedUnityPosition",
    "   → _isManualMode = false → GPS 동기화 즉시 재개",
]
for line in flow3:
    p_add(line, indent=0.5)
doc.add_paragraph()

# ════════════════════════════════════════════════════════════════════════════
# 3. 데이터 설계 (Class Diagram) and/or API 설계
# ════════════════════════════════════════════════════════════════════════════
heading("3. 데이터 설계 (Class Diagram) and/or API 설계", 1)

p_add("※ 본 프로젝트는 Unity MonoBehaviour 기반이므로 ERD 대신 컴포넌트 Class Diagram을 사용합니다.")
doc.add_paragraph()

heading("Class Diagram (주요 컴포넌트 관계)", 2)
table(
    ["Class", "주요 필드 / 메서드", "의존 대상"],
    [
        ["GPSLocationService",
         "SmoothedUnityPosition : Vector3\nTargetUnityPosition : Vector3\nCurrentLatitude / Longitude : double\n"
         "StartGPS() / StopGPS()\nConvertToUnityPosition(lat, lon, alt) : Vector3\nOnRawPositionUpdated : Action<Vector3>",
         "CesiumGeoreference\nCesiumWgs84Ellipsoid\nInput.location"],
        ["FirstPersonGPSController",
         "_targetPosition : Vector3\n_rotationMode : RotationMode {Gyro, Locked, Drag}\n_cachedGroundY : float\n"
         "_insideBuilding : bool\nCycleRotationMode()\nOnGPSPositionUpdated(Vector3)\n"
         "SampleAndUpdateGroundHeight() [async]\nHandleBuildingCollision()",
         "GPSLocationService\nLocationPermissionHandler\nCesium3DTileset\nCesiumGlobeAnchor\nPhysics"],
        ["LocationPermissionHandler",
         "IsPermissionGranted : bool\nOnPermissionGranted : Action\nOnPermissionDenied : Action\n"
         "CheckAndRequestPermission()",
         "UnityEngine.Android.Permission\nInput.location (iOS)"],
        ["RoadAssetPlacer",
         "assetPrefab : GameObject\ntreeInterval : float\nroadLayerMask : LayerMask\n"
         "BuildNavMesh()\nPlaceTreeLine(lat1, lon1, lat2, lon2)\nPlacePointAsset(lat, lon) : bool\nClearAllAssets()",
         "CesiumGeoreference\nCesiumWgs84Ellipsoid\nNavMeshSurface\nNavMesh\nPhysics\nCesiumGlobeAnchor"],
        ["MinimapController",
         "_minimapCam : Camera\n_rt : RenderTexture\n_cameraHeight : float = 400\n_orthographicSize : float = 80\n"
         "CreateMinimapCamera()\nCreateArrowTexture() : Texture2D",
         "FirstPersonGPSController\nCesiumCameraManager"],
        ["BikeRouteRenderer\n(예정)",
         "routeData : Vector3[]\nrouteWidth : float\nGenerateMesh(LineString)\nApplyShader()",
         "CesiumGeoreference\nMeshFilter / MeshRenderer\nURP Shader"],
        ["NavigationGuide\n(예정)",
         "destination : Vector3\ncurrentCornerIndex : int\nStartNavigation(dest)\nUpdateProgress(pos)\nShowTurnIndicator()",
         "GPSLocationService\nNavMesh\nTurnByTurnIndicator"],
    ],
    col_widths=[3.5, 7.5, 5],
)
doc.add_paragraph()

heading("Integrity Rules", 2)
rules = [
    "CesiumGeoreference: 씬 내 반드시 1개만 존재 — 모든 좌표 변환의 단일 기준점",
    "CesiumGlobeAnchor.detectTransformChanges = false — 런타임 Transform 변경 시 내부 좌표 재계산 방지",
    "NavMesh: Cesium 타일 로드 완료 이후에만 BuildNavMesh() 호출 (정적 베이크 불가)",
    "카메라 Y축: GPS 고도 노이즈로 인해 반드시 Cesium SampleHeight 또는 Raycast 결과만 사용",
    "StaticBatching: 배치 완료 후 1회만 적용 — 이후 동적 오브젝트 추가 시 재적용 금지",
]
for r in rules:
    p_add(f"• {r}")
doc.add_paragraph()

# ════════════════════════════════════════════════════════════════════════════
# 4. Development Environment
# ════════════════════════════════════════════════════════════════════════════
heading("4. Development Environment", 1)
table(
    ["Item", "Value"],
    [
        ["Unity",              "2022.3 LTS"],
        ["Language",           "C#  (.NET Standard 2.1 · IL2CPP 백엔드)"],
        ["Render Pipeline",    "Universal Render Pipeline (URP)  —  PC_RPAsset / Mobile_RPAsset"],
        ["Cesium for Unity",   "com.cesium.unity  (Cesium Ion 토큰 인증 필요)"],
        ["Input System",       "com.unity.inputsystem  (Touchscreen, AttitudeSensor)"],
        ["AI Navigation",      "com.unity.ai.navigation  (런타임 NavMesh 빌드)"],
        ["Mathematics",        "com.unity.mathematics  (double3 정밀도 좌표 연산)"],
        ["Android",            "API Level 24 (Android 7.0) 이상  —  FineLocation 권한"],
        ["iOS",                "iOS 13.0 이상  —  NSLocationWhenInUseUsageDescription"],
        ["Version Control",    "Git / GitHub  (브랜치: main / app_beta1)"],
        ["IDE",                "JetBrains Rider 또는 Visual Studio 2022"],
    ],
    col_widths=[5, 11],
)
doc.add_paragraph()

# ════════════════════════════════════════════════════════════════════════════
# 5. Traceability
# ════════════════════════════════════════════════════════════════════════════
heading("5. Traceability", 1)
table(
    ["Requirement", "Design Element"],
    [
        ["US-01  Cesium Ion 서버 연동",       "CesiumGeoreference + Cesium3DTileset 씬 설정  (완료)"],
        ["US-02  대상 지역 데이터 최적화",    "Cesium3DTileset 타일 우선순위 + 고도 이격 수정  (완료)"],
        ["US-03  모바일 위치 권한 획득",      "LocationPermissionHandler.CheckAndRequestPermission()  (완료)"],
        ["US-04  지리 좌표계 변환 엔진",      "GPSLocationService.ConvertToUnityPosition()  (완료)"],
        ["US-05  1인칭 카메라 위치 동기화",   "FirstPersonGPSController.SampleAndUpdateGroundHeight()  (완료)"],
        ["US-06  외부 좌표 데이터 파싱",      "RoadAssetPlacer — CSV 파싱 연동  (완료)"],
        ["US-07  데이터 기반 객체 자동 배치", "RoadAssetPlacer.PlaceTreeLine() / PlacePointAsset()  (완료)"],
        ["US-08  시설물 가시성 필터링",       "AssetLayerManager — Renderer.enabled 레이어 On/Off  (예정)"],
        ["US-09  자전거 도로 폴리라인 생성",  "BikeRouteRenderer.GenerateMesh()  (예정)"],
        ["US-10  자전거 도로 특화 셰이더",    "URP Shader Graph — Emission + 점선 애니메이션  (예정)"],
        ["US-11  터치 기반 시점 조작",        "FirstPersonGPSController.GetDragDelta() + CycleRotationMode()  (완료)"],
        ["US-12  조작 모드 심리스 전환",      "FirstPersonGPSController._isManualMode 플래그  (예정)"],
        ["US-13  '내 위치' 복귀 기능",        "OnGUI 버튼 → SmoothedUnityPosition 복귀  (예정)"],
        ["US-14  실시간 경로 가이드 시각화",  "NavigationGuide + TurnByTurnIndicator  (예정)"],
        ["US-15  거리 기반 LOD 및 컬링",      "LOD Component + Camera.cullingMask + Mobile_RPAsset  (예정)"],
        ["US-16  Cesium 지형 표면 높이 정밀 측정",
         "FirstPersonGPSController.SampleAndUpdateGroundHeight() → Cesium3DTileset.SampleHeightMostDetailed() 비동기  (완료)"],
        ["US-17  GPS 고도 노이즈 차단 및 지면 높이 캐싱",
         "FirstPersonGPSController._cachedGroundY + _lastGroundCheckXZ + GroundCheckMoveThreshold(2 m) — 수평 2 m 이상 이동 시에만 재감지  (완료)"],
        ["US-18  건물 내부 침투 감지 및 인접 도로 위치 고정",
         "FirstPersonGPSController.HandleBuildingCollision() + IsInsideBuilding() (4방향 수평 Raycast) + FindNearestRoadXZ() (방사형 탐색)  (완료)"],
        ["US-19  탑뷰 미니맵 (Cesium 타일 연동)",
         "MinimapController.CreateMinimapCamera() → RenderTexture 직교 카메라 + CesiumCameraManager.additionalCameras.Add() 등록  (완료)"],
        ["US-20  앱 백그라운드·포커스 복귀 시 센서 자동 재활성화",
         "FirstPersonGPSController.OnApplicationPause() + OnApplicationFocus() → InputSystem.EnableDevice(AttitudeSensor.current)  (완료)"],
    ],
    col_widths=[6, 10],
)
doc.add_paragraph()

doc.save(OUTPUT)
print(f"저장 완료: {OUTPUT}")
