# Unity 3D Digital Twin 파이프라인 개요

대상 프로젝트: `C:/Users/rugem/cesium2/3D_Digital_twin`  
작성 기준: 실제 파일을 읽어 확인한 구조만 반영했다.

## 1. 확인한 범위

- 런타임 스크립트: `Assets/RoadTools/Runtime/`
- GPS 스크립트: `Assets/RoadTools/Runtime/GPS/`
- Navigation 스크립트: `Assets/RoadTools/Runtime/Navigation/`
- 씬: `Assets/level2.unity`
- 문서 생성 스크립트: `build_sprint_backlog.py`, `build_system_design.py`, `build_system_design_v2.py`
- 패키지/엔진 정보: `Packages/manifest.json`, `ProjectSettings/ProjectVersion.txt`

확인된 주요 패키지는 `com.cesium.unity` 1.23.1, `com.unity.ai.navigation` 2.0.12이다. Unity 에디터 버전은 `6000.4.1f1`로 기록되어 있다.

## 2. 런타임 전체 구성

```
Cesium ion / Kakao API / Device GPS
        |
        v
CesiumGeoreference + Cesium3DTileset
        |
        +--> GPSLocationService
        |       |
        |       +--> FirstPersonGPSController
        |       +--> NavigationService
        |       +--> KakaoPlaceSearchService / KakaoDirectionsService
        |
        +--> RoadAssetPlacer
        +--> BuildingLabelManager

NavigationUIController
        |
        +--> NavigationService
        +--> RouteRenderer
        +--> MinimapController
        +--> CameraNavAnchor
```

주요 네임스페이스는 `Rugem.RoadTools`이다. 어셈블리 정의 파일 `Assets/RoadTools/Runtime/Rugem.RoadTools.Runtime.asmdef`는 `Rugem.RoadTools.Runtime` 어셈블리를 정의하고, Cesium, AI Navigation, Input System, Mathematics 계열 참조 GUID를 포함한다.

## 3. Cesium for Unity 데이터 파이프라인

### 3.1 좌표 변환 파이프라인

코드 참조:

- `Assets/RoadTools/Runtime/GPS/GPSLocationService.cs`
  - `GPSLocationService.ConvertToUnityPosition(double latitude, double longitude, double altitude = 0.0)`
  - `GPSLocationService.ProcessLocationData(double lat, double lon, double alt)`
- `Assets/RoadTools/Runtime/RoadAssetPlacer.cs`
  - `RoadAssetPlacer.PlaceTreeLine(...)`
  - `RoadAssetPlacer.PlacePointAsset(...)`
- `Assets/RoadTools/Runtime/GPS/BuildingLabelManager.cs`
  - `BuildingLabelManager.UnityToLonLatHeight(Vector3 worldPos)`

흐름:

1. WGS84 좌표가 입력된다. 입력 순서는 코드상 위도, 경도, 고도이며 Cesium 변환 호출에는 `(longitude, latitude, altitude)` 순서로 전달된다.
2. `CesiumWgs84Ellipsoid.LongitudeLatitudeHeightToEarthCenteredEarthFixed()`가 WGS84를 ECEF 좌표로 변환한다.
3. `CesiumGeoreference.TransformEarthCenteredEarthFixedPositionToUnity()`가 ECEF를 Unity 월드 좌표 `Vector3`로 변환한다.
4. `GPSLocationService.ProcessLocationData()`는 변환 결과를 `TargetUnityPosition`에 저장하고 첫 수신이면 `SmoothedUnityPosition`도 즉시 동기화한다.
5. `GPSLocationService.Update()`는 `Vector3.Lerp()`로 `SmoothedUnityPosition`을 `TargetUnityPosition` 쪽으로 보간한다.

역방향 변환은 `BuildingLabelManager.UnityToLonLatHeight()`에서 확인된다.

```
WGS84(lat, lon, alt)
  -> CesiumWgs84Ellipsoid.LongitudeLatitudeHeightToEarthCenteredEarthFixed()
  -> ECEF
  -> CesiumGeoreference.TransformEarthCenteredEarthFixedPositionToUnity()
  -> Unity Vector3
```

### 3.2 Cesium 타일셋/지형 구성

`Assets/level2.unity`에서 확인된 Cesium 오브젝트:

- `CesiumGeoreference`
  - `CesiumForUnity.CesiumGeoreference`
  - `CesiumForUnity.CesiumCameraManager`
  - 원점 값: latitude `36.36235939207394`, longitude `127.34405167665527`, height `198.58942199942413`
- `Cesium World Terrain`
  - `CesiumForUnity.Cesium3DTileset`
  - `_ionAssetID: 1`
  - `CesiumForUnity.CesiumIonRasterOverlay`
  - `_ionAssetID: 3830184`
  - 레이어: `7`
- `dorohe`
  - `Unity.AI.Navigation.NavMeshSurface`
  - `CesiumForUnity.Cesium3DTileset`
  - `_ionAssetID: 4609071`
  - `_createPhysicsMeshes: 1`
  - `m_NavMeshData`가 `Assets/NavMesh-dorohe 1.asset` GUID를 참조
- `output_folder`
  - `CesiumForUnity.Cesium3DTileset`
  - `_ionAssetID: 4545115`

지형 높이 사용 흐름:

1. `FirstPersonGPSController.SampleAndUpdateGroundHeight()`가 `_worldTerrain.SampleHeightMostDetailed(new double3(lon, lat, 0.0))`를 호출한다.
2. 성공하면 반환 고도를 `GPSLocationService.ConvertToUnityPosition()`으로 Unity 좌표로 바꾼 뒤 Y 값을 지면 높이 캐시로 사용한다.
3. 실패하면 `FirstPersonGPSController.TryGetGroundHeightRaycast()`가 Physics Raycast로 지면 Y를 찾는다.
4. 최종 카메라 목표 Y는 지면 Y + `_eyeHeight`이다.

## 4. GPS/권한/1인칭 카메라 워크플로우

코드 참조:

- `Assets/RoadTools/Runtime/GPS/LocationPermissionHandler.cs`
  - `CheckAndRequestPermission()`
  - `HandlePermissionGranted()`
  - `HandlePermissionDenied()`
- `Assets/RoadTools/Runtime/GPS/GPSLocationService.cs`
  - `StartGPS()`
  - `GPSUpdateLoop()`
  - `ConvertToUnityPosition(...)`
- `Assets/RoadTools/Runtime/GPS/FirstPersonGPSController.cs`
  - `StartGPSTracking()`
  - `OnGPSPositionUpdated(Vector3 rawUnityPosition)`
  - `SampleAndUpdateGroundHeight(...)`
  - `UpdateRotation()`
  - `TeleportTo(double latitude, double longitude)`

단계별 흐름:

1. `LocationPermissionHandler.Start()`가 버튼 리스너를 등록하고 `CheckAndRequestPermission()`을 호출한다.
2. Android에서는 `Permission.FineLocation` 권한을 확인/요청한다.
3. iOS에서는 `Input.location.Start()` 후 상태를 확인하는 `CheckiOSPermission()` 코루틴을 사용한다.
4. 에디터/기타 플랫폼에서는 바로 권한 허용으로 처리한다.
5. 권한 허용 시 `OnPermissionGranted` 이벤트가 발생한다.
6. `FirstPersonGPSController.Start()`는 `LocationPermissionHandler.OnPermissionGranted`를 구독하고, 허용 상태이면 `StartGPSTracking()`을 호출한다.
7. `StartGPSTracking()`은 `GPSLocationService.OnRawPositionUpdated`에 `OnGPSPositionUpdated()`를 연결하고 `GPSLocationService.StartGPS()`를 호출한다.
8. `GPSLocationService.GPSUpdateLoop()`가 플랫폼별 GPS 데이터를 읽는다. 에디터에서는 `CesiumGeoreference`의 latitude/longitude를 시뮬레이션 값으로 사용한다.
9. GPS 좌표는 `ConvertToUnityPosition()`을 거쳐 Unity 좌표로 변환되고 `OnRawPositionUpdated`로 전달된다.
10. `FirstPersonGPSController.OnGPSPositionUpdated()`는 XZ를 반영하고, 지면 높이 캐시가 없거나 XZ 이동량이 2m를 넘으면 `SampleAndUpdateGroundHeight()`를 실행한다.
11. `FirstPersonGPSController.Update()`는 목표 위치로 보간하고 `CesiumGlobeAnchor.transform.position`을 갱신한다.
12. `UpdateRotation()`은 `RotationMode.Gyro`, `RotationMode.Locked`, `RotationMode.Drag` 상태에 따라 카메라 회전을 갱신한다.

```
권한 확인
  -> GPS 시작
  -> WGS84 수신
  -> Cesium 좌표 변환
  -> 지면 높이 샘플링 또는 Raycast
  -> 카메라 목표 위치 계산
  -> CesiumGlobeAnchor 위치 갱신
```

건물 충돌 보정은 `FirstPersonGPSController.HandleBuildingCollision()`에서 처리한다. `_buildingLayerMask`가 설정된 경우 0.2초 간격으로 `IsInsideBuilding()`을 확인하고, 건물 내부로 판단되면 `FindNearestRoadXZ()`로 가까운 도로/개방 위치를 찾아 XZ를 고정한다.

## 5. 내비게이션/검색/경로 표시 워크플로우

코드 참조:

- `Assets/RoadTools/Runtime/Navigation/NavigationUIController.cs`
  - `OpenSearch()`
  - `StartKakaoSearch()`
  - `SelectDestination(POIData poi)`
  - `HandleRouteCalculated(POIData poi, Vector3[] route)`
  - `HandleArrived()`
- `Assets/RoadTools/Runtime/Navigation/KakaoPlaceSearchService.cs`
  - `Search(string query, Action<List<POIData>, string> onComplete)`
  - `SearchCoroutine(...)`
  - `ParseDocuments(...)`
- `Assets/RoadTools/Runtime/Navigation/NavigationService.cs`
  - `SetDestination(POIData poi)`
  - `CalculateRoute()`
  - `CalculateNavMeshRoute()`
  - `TryCalculateNavMeshRoute(...)`
  - `TryCalculateRoadMeshRoute(...)`
  - `MoveToDestination()`
  - `ClearNavigation()`
- `Assets/RoadTools/Runtime/Navigation/KakaoDirectionsService.cs`
  - `RequestRoute(...)`
  - `RequestCoroutine(...)`
  - `ParseWaypoints(...)`
- `Assets/RoadTools/Runtime/Navigation/RouteRenderer.cs`
  - `ShowRoute(Vector3[] waypoints)`
  - `TrimFromPlayerPosition(Vector3 playerWorldPos)`
  - `ProjectOnNavMeshAndTerrain(Vector3[] waypoints)`

UI 상태 흐름:

```
None
  -> SearchOpen
  -> MapOverview
  -> Navigating
  -> Arrived
  -> None
```

검색/목적지 설정 흐름:

1. `NavigationUIController.OnGUI()`가 상태별 UI를 그린다.
2. `None` 상태에서 검색 버튼을 누르면 `OpenSearch()`가 `SearchOpen`으로 전환하고 최근 검색을 `PlayerPrefs`에서 읽는다.
3. 사용자가 검색어를 입력하면 `StartKakaoSearch()`가 `KakaoPlaceSearchService.Search()`를 호출한다.
4. `KakaoPlaceSearchService.SearchCoroutine()`은 `https://dapi.kakao.com/v2/local/search/keyword.json`에 REST 요청을 보낸다.
5. 응답은 `ParseDocuments()`에서 `POIData` 목록으로 변환된다.
6. 검색 결과 항목을 선택하면 `NavigationUIController.SelectDestination()`이 실행된다.
7. `SelectDestination()`은 최근 검색에 추가하고 `NavigationService.SetDestination()`을 호출한다.
8. `NavigationService.SetDestination()`은 목적지 WGS84 좌표를 Unity 월드 좌표로 변환하고 `CalculateRoute()`를 호출한다.

경로 계산 우선순위:

1. `KakaoDirectionsService`가 연결되어 있으면 `KakaoDirectionsService.RequestRoute()`를 먼저 사용한다.
2. Directions 응답이 성공하면 `ParseWaypoints()`가 Kakao vertexes 배열을 Unity 좌표 배열로 변환한다.
3. 실패하거나 Directions 서비스가 없으면 `CalculateNavMeshRoute()`를 사용한다.
4. `TryCalculateNavMeshRoute()`가 Unity `NavMesh.SamplePosition()`과 `NavMesh.CalculatePath()`로 경로를 찾는다.
5. NavMesh 경로가 실패하면 `TryCalculateRoadMeshRoute()`가 `_roadLayerMask`에 대해 Raycast 기반 도로 그리드를 만들고 A* 탐색(`FindRoadGridPath`)을 수행한다.
6. 모두 실패하면 시작점과 목적지를 잇는 직선 경로를 `CurrentRoute`로 사용한다.

```
목적지 선택
  -> Kakao Directions 시도
      -> 성공: Kakao waypoints 사용
      -> 실패: NavMesh 경로 계산
          -> 실패: Road 레이어 Raycast 그리드 A*
              -> 실패: 직선 경로
  -> OnRouteCalculated 이벤트
  -> RouteRenderer.ShowRoute()
```

경로 렌더링:

1. `NavigationService.OnRouteCalculated` 이벤트를 `NavigationUIController.HandleRouteCalculated()`가 받는다.
2. `RouteRenderer.ShowRoute()`가 경로를 `ProjectOnNavMeshAndTerrain()`에 전달한다.
3. `ProjectOnNavMeshAndTerrain()`은 구간을 `_terrainSampleStep` 간격으로 보간하고 `SnapToRoadSurface()`로 각 점을 도로/지형 표면에 투영한다.
4. `DrawLine()`이 `LineRenderer`에 좌표를 설정한다.
5. 목적지에는 런타임 생성 구체 `[NavDestinationMarker]`가 표시된다.
6. 주행 중에는 `NavigationUIController.Update()`가 `RouteRenderer.TrimFromPlayerPosition()`을 호출해 현재 위치 앞쪽 경로만 다시 그린다.

도착 판정:

1. `NavigationService.Update()`가 내비게이션 중 `UpdateDistance()`와 `CheckArrival()`을 호출한다.
2. `DistanceToDestination`이 `_arrivalRadius`보다 작으면 `OnArrived` 이벤트를 발생시키고 `IsNavigating`을 false로 바꾼다.
3. `NavigationUIController.HandleArrived()`가 경로를 숨기고 `Arrived` 상태로 전환한다.

## 6. 미니맵/카메라 기준점 워크플로우

코드 참조:

- `Assets/RoadTools/Runtime/GPS/MinimapController.cs`
  - `CreateMinimapCamera()`
  - `LateUpdate()`
  - `EnterOverviewMode(Vector3 playerWorldPos, Vector3 destWorldPos)`
  - `ExitOverviewMode()`
  - `OnGUI()`
- `Assets/RoadTools/Runtime/GPS/CameraNavAnchor.cs`
  - `Awake()`
  - `LateUpdate()`
  - `SampleGroundBelow(Vector3 camPos)`

미니맵 흐름:

1. `MinimapController.Start()`가 `CreateMinimapCamera()`를 호출한다.
2. 런타임에 `[MinimapCamera]` GameObject를 생성하고 orthographic Camera를 붙인다.
3. `RenderTexture`를 생성하여 미니맵 카메라의 `targetTexture`로 설정한다.
4. `CesiumCameraManager`가 있으면 `additionalCameras`에 미니맵 카메라를 등록한다.
5. 일반 모드에서는 `LateUpdate()`가 `_followTarget` 위치 위로 미니맵 카메라를 이동한다.
6. `OnGUI()`가 화면 오른쪽 상단에 RenderTexture와 방향 마커를 그린다.
7. `EnterOverviewMode()`는 플레이어/목적지 중점을 기준으로 카메라를 이동하고 orthographic size를 조정한다.

카메라 기준점 흐름:

1. `CameraNavAnchor.Awake()`는 자식 `mainCameraNav`가 있으면 사용하고 없으면 생성한다.
2. `LateUpdate()`마다 `Camera.main` 아래 방향으로 Raycast를 쏜다.
3. Raycast 성공 시 지면 표면 위치를 `NavTransform.position`에 저장한다.
4. `NavigationService.CalculateNavMeshRoute()`와 `NavigationUIController.Update()`는 이 지점이 있으면 경로 시작점/트리밍 기준으로 사용한다.

## 7. 건물 이름 라벨 워크플로우

코드 참조:

- `Assets/RoadTools/Runtime/GPS/BuildingLabelManager.cs`
  - `Update()`
  - `ScanViewport()`
  - `FetchBuildingName((int, int) key, double lat, double lon)`
  - `ToGpsKey(Vector3 worldPos)`
  - `UnityToLonLatHeight(Vector3 worldPos)`
  - `OnGUI()`

흐름:

1. `Update()`가 `_checkInterval`마다 `ScanViewport()` 코루틴을 시작한다.
2. `ScanViewport()`는 화면을 `_gridSize x _gridSize` 격자로 나누고 각 격자 중심에서 Raycast를 수행한다.
3. 충돌점 Y가 지면 기준 + `_buildingMinHeight` 이상이면 건물 후보로 간주한다.
4. 충돌점 Unity 좌표를 `UnityToLonLatHeight()`로 WGS84 좌표로 변환한다.
5. `ToGpsKey()`가 좌표를 `_gpsQuantizeScale` 단위로 양자화해 캐시 키를 만든다.
6. 캐시에 없으면 `FetchBuildingName()`이 Kakao coord2address API `https://dapi.kakao.com/v2/local/geo/coord2address.json`로 요청한다.
7. 응답의 `road_address.building_name` 값을 캐시에 저장한다.
8. `OnGUI()`는 카메라 가시 영역과 `_labelMaxDistance` 조건을 통과한 라벨을 화면에 그린다.

## 8. 도로/시설물 배치 및 NavMesh 워크플로우

코드 참조:

- `Assets/RoadTools/Runtime/RoadAssetPlacer.cs`
  - `GetOrCreateTypeGroup(string typeName)`
  - `SetTypeVisible(string typeName, bool visible)`
  - `DetachMeshes(Transform container, Action<GameObject> onCreated = null)`
  - `BuildNavMesh()`
  - `PlaceTreeLine(...)`
  - `PlacePointAsset(double latitude, double longitude, Transform parent = null)`
  - `ClearAllAssets()`

타입 그룹 관리:

1. `GetOrCreateTypeGroup()`은 `[Type] {typeName}` 자식 GameObject를 만들거나 기존 그룹을 반환한다.
2. `SetTypeVisible()`은 타입 그룹 GameObject의 active 상태를 바꾼다.
3. `GetAllTypeNames()`는 현재 생성된 타입 그룹 이름 목록을 반환한다.

NavMesh 빌드:

1. `BuildNavMesh()`는 현재 GameObject에 `NavMeshSurface`가 없으면 추가한다.
2. `collectObjects`를 `CollectObjects.Children`로 설정한다.
3. `layerMask`를 `roadLayerMask`로 설정한다.
4. `BuildNavMesh()`를 호출한다.

선형 시설물 배치:

1. `PlaceTreeLine()`이 시작/종료 WGS84 좌표를 입력받는다.
2. 각 좌표를 Cesium ECEF 변환 후 Unity 좌표로 변환한다.
3. `NavMesh.SamplePosition()`으로 시작/종료 지점을 NavMesh 위로 스냅한다.
4. `NavMesh.CalculatePath()`로 경로를 계산한다.
5. 경로 corner 구간을 `treeInterval` 간격으로 샘플링한다.
6. 각 샘플 위치에서 아래 방향 Raycast를 `roadLayerMask`로 수행한다.
7. 성공 지점에 `assetPrefab`을 Instantiate하고 `CesiumGlobeAnchor`를 추가한다.
8. `disableShadows`가 true이면 하위 Renderer의 shadow casting을 끈다.
9. `applyStaticBatching`이 true이면 `StaticBatchingUtility.Combine(lineParent)`를 호출한다.

점 시설물 배치:

1. `PlacePointAsset()`이 WGS84 좌표 하나를 입력받는다.
2. Cesium 변환으로 Unity 좌표를 만든다.
3. 해당 XZ 위치 위에서 Raycast로 실제 지면을 찾는다.
4. 성공하면 prefab을 배치하고 `CesiumGlobeAnchor`를 추가한다.

## 9. 모바일 입력 및 Cesium Credit 처리

코드 참조:

- `Assets/RoadTools/Runtime/GPS/MobileInputSetup.cs`
  - `Awake()`
  - `OnDestroy()`
- `Assets/RoadTools/Runtime/GPS/CesiumCreditReducer.cs`
  - `Start()`
  - `Apply()`
  - `FindCreditDocument()`
  - `SetPickingModeRecursive(VisualElement root, PickingMode mode)`

`MobileInputSetup`은 `ENABLE_INPUT_SYSTEM`이 정의된 경우 `EnhancedTouchSupport.Enable()`을 호출하고, 파괴 시 Disable한다.

`CesiumCreditReducer`는 2프레임 대기 후 3초마다 `Apply()`를 실행한다. `UIDocument`에서 `OnScreenCredits` VisualElement를 찾고, root 하위 전체의 `pickingMode`를 `Ignore`로 설정한다. `_fontSize <= 0`이면 credit 표시를 숨기고, 아니면 font size를 조정한다.

## 10. level2.unity 씬 레벨 구성

`Assets/level2.unity`에서 확인된 주요 GameObject와 컴포넌트:

| GameObject | 확인된 주요 컴포넌트/설정 |
|---|---|
| `navUI` | `RoadAssetPlacer`, `RouteRenderer`, `LineRenderer`, `NavigationUIController`, `NavigationService`, `KakaoPlaceSearchService` |
| `Main Camera` | `Camera`, `FirstPersonGPSController`, `CesiumOriginShift`, `CesiumGlobeAnchor`, `CesiumCameraController`, `CesiumCreditReducer`, `CameraNavAnchor` |
| `building nameTag Manager` | `BuildingLabelManager` |
| `CesiumGeoreference` | `CesiumCameraManager`, `CesiumGeoreference` |
| `dorohe` | `NavMeshSurface`, `Cesium3DTileset` |
| `gpsm minimap` | `RoadAssetPlacer`, `LocationPermissionHandler`, `GPSLocationService`, `MinimapController` |
| `Cesium World Terrain` | `CesiumIonRasterOverlay`, `Cesium3DTileset` |
| `output_folder` | `Cesium3DTileset` |

중요 연결:

- `navUI.NavigationUIController`
  - `_navService` -> 같은 GameObject의 `NavigationService`
  - `_routeRenderer` -> 같은 GameObject의 `RouteRenderer`
  - `_gpsService` -> `gpsm minimap`의 `GPSLocationService`
  - `_kakaoSearch` -> 같은 GameObject의 `KakaoPlaceSearchService`
  - `_minimapController` -> `gpsm minimap`의 `MinimapController`
  - `_navAnchor` -> `Main Camera`의 `CameraNavAnchor`
- `navUI.NavigationService`
  - `_gpsService` -> `gpsm minimap`의 `GPSLocationService`
  - `_playerController` -> `Main Camera`의 `FirstPersonGPSController`
  - `_navAnchor` -> `Main Camera`의 `CameraNavAnchor`
  - `_directionsService` -> 미연결 `{fileID: 0}`
- `Main Camera.FirstPersonGPSController`
  - `_gpsService` -> `gpsm minimap`의 `GPSLocationService`
  - `_permissionHandler` -> `gpsm minimap`의 `LocationPermissionHandler`
  - `_worldTerrain` -> `Cesium World Terrain`의 `Cesium3DTileset`
- `gpsm minimap.GPSLocationService`
  - `_georeference` -> `CesiumGeoreference`의 `CesiumGeoreference`
- `gpsm minimap.MinimapController`
  - `_followTarget` -> `Main Camera` Transform
- `building nameTag Manager.BuildingLabelManager`
  - `_georeference` -> `CesiumGeoreference`의 `CesiumGeoreference`

레이어 마스크 값은 씬 직렬화 기준으로 다음처럼 사용된다.

- Road 계열로 보이는 마스크: `m_Bits: 64`
- 지형/지면 계열로 보이는 마스크: `m_Bits: 128`
- 건물 계열로 보이는 마스크: `m_Bits: 256`

레이어 이름 자체는 `level2.unity` 직렬화 구간만으로 확정하지 않았다.

## 11. 빌드/문서 생성 파이프라인

실제 `build_*.py` 파일은 Unity 플레이어 빌드 스크립트가 아니라 `.docx` 문서 생성 스크립트다. 세 스크립트 모두 `python-docx`의 `Document`를 사용한다.

### 11.1 Sprint Backlog 문서 생성

파일: `build_sprint_backlog.py`

확인된 함수:

- `shd(cell, hex_color)`
- `set_col_width(table, col_idx, width_cm)`
- `header_row(table, headers, bg='4472C4', fg='FFFFFF')`
- `data_rows(table, rows, alt='D9E2F3')`
- `task_section(doc, heading, tasks)`

흐름:

1. 표 셀 음영, 컬럼 너비, 헤더/데이터 행 생성을 위한 헬퍼 함수를 정의한다.
2. `[양식 6-2] Sprint Backlog 양식 (1).docx` 템플릿을 `Sprint_Backlog_Sprint1_완성.docx`로 복사한다.
3. `Document("Sprint_Backlog_Sprint1_완성.docx")`로 문서를 연다.
4. 기존 문서 본문 일부를 제거한다.
5. Sprint Goal, Selected User Stories, Task Breakdown, Definition of Done Mapping 섹션을 추가한다.
6. `doc.save("Sprint_Backlog_Sprint1_완성.docx")`로 저장한다.

### 11.2 시스템 구조 설계 문서 생성

파일: `build_system_design.py`

확인된 함수:

- `add_paragraph(...)`
- `add_heading(text, level=1)`
- `add_table(headers, rows, col_widths=None)`

흐름:

1. `[양식 5] 시스템 구조 설계 및 개발 환경 양식.docx` 템플릿을 `[양식 5] 시스템 구조 설계 및 개발 환경 완성.docx`로 복사한다.
2. 복사본을 `Document(OUTPUT)`으로 연다.
3. 기존 paragraph/table 요소를 제거한다.
4. 문단, 제목, 표 생성을 위한 헬퍼를 사용한다.
5. 문서 이력, 메타 정보, 프로젝트 개요, 아키텍처 설계, 핵심 업무 흐름, 데이터 설계, 향후 개발 요소, 개발 환경, 요구사항 추적 섹션을 작성한다.
6. `doc.save(OUTPUT)`로 저장하고 저장 완료 메시지를 출력한다.

### 11.3 시스템 구조 설계 v2 문서 생성

파일: `build_system_design_v2.py`

확인된 함수:

- `p_add(...)`
- `heading(text, level=1)`
- `shade_cell(cell, hex_color="D9E1F2")`
- `table(headers, rows, col_widths=None, header_color="D9E1F2")`

흐름:

1. `[양식 5] 시스템 구조 설계 및 개발 환경 양식.docx` 템플릿을 `[양식 5] 시스템 구조 설계 및 개발 환경 완성_v2.docx`로 복사한다.
2. 복사본을 `Document(OUTPUT)`으로 연다.
3. 기존 paragraph/table 요소를 제거한다.
4. 문단, 제목, 음영, 표 생성을 위한 헬퍼를 사용한다.
5. 프로젝트/시스템 설계 내용을 다시 구성한다.
6. `doc.save(OUTPUT)`로 저장하고 저장 완료 메시지를 출력한다.

## 12. 주요 런타임 이벤트/데이터 연결 요약

```
LocationPermissionHandler.OnPermissionGranted
  -> FirstPersonGPSController.StartGPSTracking()
      -> GPSLocationService.StartGPS()
          -> GPSLocationService.OnRawPositionUpdated
              -> FirstPersonGPSController.OnGPSPositionUpdated()
```

```
NavigationUIController.SelectDestination()
  -> NavigationService.SetDestination()
      -> NavigationService.CalculateRoute()
          -> NavigationService.OnRouteCalculated
              -> NavigationUIController.HandleRouteCalculated()
                  -> RouteRenderer.ShowRoute()
```

```
NavigationService.Update()
  -> UpdateDistance()
  -> CheckArrival()
      -> NavigationService.OnArrived
          -> NavigationUIController.HandleArrived()
              -> RouteRenderer.HideRoute()
```

```
MinimapController.EnterOverviewMode()
  <- NavigationUIController.SelectDestination()
  -> RenderTexture 기반 overview 지도 표시
```

## 13. 실제 코드 기준 특이사항

- `NavigationService._directionsService`는 `level2.unity`에서 연결되어 있지 않다. 따라서 현재 씬 직렬화 기준으로는 Kakao Directions 경로가 아니라 NavMesh/도로 메쉬/직선 fallback 흐름이 사용된다.
- `KakaoPlaceSearchService`와 `BuildingLabelManager`에는 `_restApiKey` 값이 씬에 직렬화되어 있다.
- `KakaoDirectionsService.cs` 파일은 존재하지만 `level2.unity`에서 해당 컴포넌트 인스턴스는 확인되지 않았다.
- `MobileInputSetup.cs` 파일은 존재하지만 `level2.unity`의 주요 검색 결과에서는 해당 컴포넌트가 확인되지 않았다.
- `RoadAssetPlacer`는 `navUI`와 `gpsm minimap` 양쪽에 붙어 있다. `navUI` 쪽은 `assetPrefab`이 연결되어 있고, `gpsm minimap` 쪽은 `assetPrefab`이 `{fileID: 0}`이다.
- 소스 파일의 일부 한글 주석/문자열은 현재 읽기 결과에서 인코딩이 깨져 보였지만, 클래스명/메서드명/로직은 C# 구문 기준으로 확인했다.
