# FirstPersonGPSController

파일 위치: `Assets/RoadTools/Runtime/GPS/FirstPersonGPSController.cs`

`FirstPersonGPSController`는 GPS 기반 1인칭 카메라 컨트롤러입니다.
`GPSLocationService`가 GPS 좌표를 Unity 월드 좌표로 변환하면, 이 스크립트가 그 위치를 받아 카메라를 실제 사용자 위치처럼 이동시키고, 나침반/자이로/드래그 입력으로 카메라 방향을 제어합니다.

GPS 값을 직접 읽는 컴포넌트는 아니며, `GPSLocationService.OnRawPositionUpdated` 이벤트를 통해 변환된 Unity 좌표를 받아 사용합니다.

## 핵심 역할

- 위치 권한 허용 후 GPS 추적 시작
- `GPSLocationService.OnRawPositionUpdated` 이벤트 구독
- GPS Unity 좌표를 받아 카메라 목표 위치 갱신
- Cesium 지형 높이 또는 Physics Raycast로 카메라 Y 높이 보정
- 나침반/자이로/드래그 기반 카메라 회전 처리
- 회전 모드 전환 UI 표시
- GPS 오차로 건물 내부에 들어간 상황을 감지하고 가까운 도로/개방 공간으로 보정
- 특정 위도/경도로 즉시 이동하는 `TeleportTo()` 제공

## 필수 컴포넌트

```csharp
[RequireComponent(typeof(Camera))]
[RequireComponent(typeof(CesiumGlobeAnchor))]
```

이 스크립트는 `Camera`와 `CesiumGlobeAnchor`가 붙은 오브젝트에서 동작합니다.

`Camera`는 실제 1인칭 시야를 렌더링합니다.
`CesiumGlobeAnchor`는 Cesium 지구 좌표계와 Unity Transform을 연결하는 컴포넌트입니다.

`Awake()`와 `Start()`에서 `EnsureGlobeAnchor()`를 호출하므로, `CesiumGlobeAnchor`가 없으면 런타임에 추가됩니다.

## 주요 연결 필드

```csharp
[SerializeField] private GPSLocationService _gpsService;
[SerializeField] private LocationPermissionHandler _permissionHandler;
```

`_gpsService`는 GPS 수신과 좌표 변환을 담당합니다.
`_permissionHandler`는 위치 권한 허용/거부 이벤트를 제공합니다.

Inspector에 연결되어 있지 않으면 `ResolveDependencies()`에서 `FindAnyObjectByType()`로 자동 검색합니다.

```csharp
[SerializeField] private Cesium3DTileset _worldTerrain;
```

Cesium 지형 높이를 샘플링할 때 사용합니다.
설정되어 있으면 `SampleHeightMostDetailed()`를 통해 실제 지형 고도를 조회합니다.
없거나 실패하면 Physics Raycast를 대체 수단으로 사용합니다.

## 위치 관련 설정

```csharp
[SerializeField] private float _eyeHeight = 2.0f;
[SerializeField] private float _raycastOriginHeight = 500f;
[SerializeField] private LayerMask _groundLayerMask = ~0;
[SerializeField] private float _positionLerpSpeed = 8f;
```

- `_eyeHeight`: 지면에서 카메라 눈높이까지의 높이입니다.
- `_raycastOriginHeight`: Raycast로 지면을 찾을 때 시작점을 얼마나 위에 둘지 결정합니다.
- `_groundLayerMask`: Raycast가 지면으로 인식할 레이어입니다.
- `_positionLerpSpeed`: 현재 카메라 위치가 목표 위치를 따라가는 보간 속도입니다.

카메라의 최종 목표 높이는 일반적으로 다음 구조입니다.

```text
카메라 Y = 지면 Y + _eyeHeight
```

GPS 고도값은 흔들림이 크기 때문에, 이 컨트롤러는 Y 좌표를 GPS 고도에만 의존하지 않고 지면 높이 기준으로 보정합니다.

## 회전 관련 설정

```csharp
[SerializeField] private float _rotationLerpSpeed = 10f;
[SerializeField] private bool _forceCompassOnly = false;
[SerializeField] private bool _preferCompassHeading = true;
[SerializeField] private float _headingYawCorrection = -90f;
[SerializeField] private float _uprightHeadingMinHorizontal = 0.45f;
[SerializeField] private bool _autoCalibrateGyroYaw = true;
[SerializeField] private float _dragSensitivity = 0.3f;
```

- `_rotationLerpSpeed`: 회전 보간 속도입니다.
- `_forceCompassOnly`: 자이로 대신 나침반만 강제로 사용할지 결정합니다.
- `_preferCompassHeading`: 가능하면 나침반 방향을 우선 사용할지 결정합니다.
- `_headingYawCorrection`: 월드 방향과 기기 방향 사이의 yaw 보정값입니다.
- `_uprightHeadingMinHorizontal`: 기기가 안정적인 수평 방향을 제공한다고 판단할 최소 기준입니다.
- `_autoCalibrateGyroYaw`: 자이로 yaw를 현재 카메라 방향에 맞춰 자동 보정할지 결정합니다.
- `_dragSensitivity`: 드래그 회전 감도입니다.

## 건물 보정 설정

```csharp
[SerializeField] private LayerMask _buildingLayerMask;
[SerializeField] private LayerMask _roadLayerMask;
[SerializeField] private float _buildingRayLength = 15f;
[SerializeField] private float _roadSearchMaxRadius = 30f;
```

GPS 오차로 카메라가 건물 내부에 들어간 것처럼 보일 때 위치를 보정하기 위한 설정입니다.

- `_buildingLayerMask`: 건물로 판단할 레이어입니다.
- `_roadLayerMask`: 도로로 판단할 레이어입니다.
- `_buildingRayLength`: 건물 내부 여부를 확인할 수평 Raycast 길이입니다.
- `_roadSearchMaxRadius`: 가까운 도로 또는 개방 공간을 찾을 최대 반경입니다.

## 회전 모드

```csharp
public enum RotationMode
{
    Gyro,
    Locked,
    Drag
}
```

회전 모드는 세 가지입니다.

- `Gyro`: 나침반 또는 자이로 기반으로 기기 방향을 따라갑니다.
- `Locked`: 현재 카메라 방향을 고정합니다.
- `Drag`: 터치 또는 마우스 드래그로 사용자가 직접 방향을 조작합니다.

`OnGUI()`에서 회전 모드 전환 버튼을 표시하고, 버튼을 누르면 `CycleRotationMode()`가 호출됩니다.

```text
Gyro -> Locked -> Drag -> Gyro
```

## 초기화 흐름

```csharp
private void Awake()
```

`Awake()`에서는 다음을 수행합니다.

1. `ResolveDependencies()`로 GPS 서비스와 권한 핸들러를 찾습니다.
2. `EnsureGlobeAnchor()`로 `CesiumGlobeAnchor`를 확보합니다.
3. 같은 오브젝트에 `CesiumCameraController`가 있으면 비활성화합니다.

`CesiumCameraController`를 끄는 이유는 이 스크립트가 카메라 위치와 회전을 직접 제어하기 때문입니다.
Cesium 기본 카메라 컨트롤러가 동시에 켜져 있으면 두 컨트롤러가 Transform을 서로 덮어쓸 수 있습니다.

```csharp
private void Start()
```

`Start()`에서는 다음을 수행합니다.

1. 현재 카메라 회전을 초기 목표 회전으로 저장합니다.
2. 나침반과 자이로 센서를 초기화합니다.
3. 의존성과 `CesiumGlobeAnchor`를 다시 확인합니다.
4. `LocationPermissionHandler`가 있으면 권한 이벤트를 구독합니다.
5. 이미 권한이 허용된 상태면 바로 GPS 추적을 시작합니다.
6. 권한 핸들러가 없으면 GPS 추적을 바로 시작합니다.

## 권한과 GPS 시작

권한 핸들러가 있으면 다음 이벤트를 구독합니다.

```csharp
_permissionHandler.OnPermissionGranted += OnPermissionGranted;
_permissionHandler.OnPermissionDenied += OnPermissionDenied;
```

권한이 허용되면 `OnPermissionGranted()`가 호출되고, 내부에서 `StartGPSTracking()`을 실행합니다.

```csharp
private void OnPermissionGranted() => StartGPSTracking();
```

`StartGPSTracking()`의 역할은 다음과 같습니다.

1. `GPSLocationService`를 찾습니다.
2. `OnRawPositionUpdated` 이벤트를 구독합니다.
3. `GPSLocationService.StartGPS()`를 호출합니다.

```text
권한 허용
  -> StartGPSTracking()
  -> GPSLocationService.OnRawPositionUpdated 구독
  -> GPSLocationService.StartGPS()
```

## GPS 위치 수신 처리

```csharp
private void OnGPSPositionUpdated(Vector3 rawUnityPosition)
```

`GPSLocationService`에서 새 GPS 위치를 Unity 좌표로 변환하면 이 메서드가 호출됩니다.

처리 순서는 다음과 같습니다.

1. 컴포넌트가 활성 상태인지 확인합니다.
2. GPS 서비스와 `CesiumGlobeAnchor`를 다시 확보합니다.
3. GPS 위치의 X/Z 이동량을 확인합니다.
4. 지면 높이 캐시가 없거나 2m 이상 이동했으면 지면 높이 샘플링을 요청합니다.
5. 카메라 목표 Y를 `지면 높이 + 눈높이`로 계산합니다.
6. `_targetPosition`을 갱신합니다.
7. 최초 위치라면 카메라를 즉시 목표 위치에 배치합니다.

위치 계산 개념은 다음과 같습니다.

```text
rawUnityPosition
  -> X/Z: GPS 변환 위치 사용
  -> Y: 캐시된 지면 높이 + _eyeHeight
  -> _targetPosition 갱신
```

GPS 고도값이 흔들려도 카메라가 위아래로 튀지 않도록, Y 좌표는 지면 샘플링 결과를 우선 사용합니다.

## 매 프레임 이동 처리

```csharp
private void Update()
```

매 프레임 다음 순서로 동작합니다.

1. `UpdateRotation()`으로 카메라 회전을 갱신합니다.
2. 아직 초기 위치가 없다면 이동 처리를 중단합니다.
3. 0.2초 간격으로 건물 내부/충돌 상태를 확인합니다.
4. 현재 위치에서 `_targetPosition`까지 `Lerp`로 부드럽게 이동합니다.
5. 이동 결과를 `CesiumGlobeAnchor`의 Transform에 적용합니다.

```csharp
Vector3 smoothed = Vector3.Lerp(
    transform.position, _targetPosition, Time.deltaTime * _positionLerpSpeed);
_globeAnchor.transform.position = smoothed;
```

카메라 위치는 즉시 순간 이동하지 않고 목표 위치를 따라가도록 보간됩니다.

## 지면 높이 보정

```csharp
private async void SampleAndUpdateGroundHeight(double lat, double lon, Vector3 rawUnityPosition)
```

GPS 위치의 실제 지면 높이를 구하는 메서드입니다.

우선 `_worldTerrain`이 있으면 Cesium의 상세 지형 샘플링을 사용합니다.

```csharp
_worldTerrain.SampleHeightMostDetailed(new double3(lon, lat, 0.0))
```

성공하면 반환된 고도를 다시 `GPSLocationService.ConvertToUnityPosition()`으로 Unity 좌표로 바꿔 `_cachedGroundY`에 저장합니다.

Cesium 샘플링이 실패하거나 `_worldTerrain`이 없으면 `TryGetGroundHeightRaycast()`로 대체합니다.
Raycast는 현재 위치 위쪽에서 아래 방향으로 쏴서 지면 충돌 지점을 찾습니다.

흐름은 다음과 같습니다.

```text
SampleAndUpdateGroundHeight()
  -> Cesium Terrain 높이 샘플링 시도
      -> 성공: Unity Y로 변환 후 캐시
      -> 실패: Raycast 대체
  -> Raycast 성공 시 지면 Y 캐시
  -> _targetPosition.y = _cachedGroundY + _eyeHeight
```

높이 샘플링은 비동기이므로 `_heightSampleVersion`을 사용해 오래된 요청 결과가 최신 위치를 덮어쓰지 않도록 막습니다.

## 회전 처리

```csharp
private void UpdateRotation()
```

현재 `RotationMode`에 따라 회전 처리 방식이 달라집니다.

### Gyro 모드

먼저 나침반 방향을 시도합니다.

```text
TryGetCompassYawRotation()
  -> true heading 또는 magnetic heading 사용
  -> 월드 북쪽 방향 보정
  -> yaw correction 적용
```

나침반 방향을 사용할 수 없으면 자이로 센서를 사용합니다.

```text
TryGetCalibratedGyroRotation()
  -> AttitudeSensor attitude 읽기
  -> Unity 카메라 회전으로 변환
  -> 수평 heading 추출
  -> yaw 자동 보정
  -> yaw correction 적용
```

최종 회전은 `Quaternion.Slerp()`로 부드럽게 적용합니다.

### Locked 모드

`CycleRotationMode()`로 `Locked`에 진입할 때 저장한 `_lockedRotation`을 그대로 유지합니다.

```text
transform.rotation = _lockedRotation
```

### Drag 모드

`Pointer.current`에서 터치 또는 마우스 드래그 delta를 읽습니다.
드래그 X는 yaw, 드래그 Y는 pitch에 반영합니다.
pitch는 너무 위/아래로 꺾이지 않도록 -80도에서 80도 사이로 제한합니다.

```text
delta.x -> yaw
delta.y -> pitch
pitch clamp: -80 ~ 80
```

## 센서 수명 주기

```csharp
private void InitializeSensors()
```

나침반을 켜고, 가능한 경우 Input System의 `AttitudeSensor`를 활성화합니다.
`_forceCompassOnly`가 켜져 있으면 자이로는 사용하지 않습니다.

앱이 pause/focus 상태를 오갈 때는 자이로 센서를 다시 활성화하고 보정 상태를 초기화합니다.

```text
OnApplicationPause(false)
  -> AttitudeSensor 재활성화
  -> gyro yaw calibration reset

OnApplicationFocus(true)
  -> AttitudeSensor 재활성화
  -> gyro yaw calibration reset
```

`OnDestroy()`에서는 자이로와 나침반을 끄고, GPS/권한 이벤트 구독을 해제합니다.

## 건물 내부 보정

```csharp
private void HandleBuildingCollision()
```

GPS 오차 때문에 카메라가 건물 내부에 들어간 것처럼 보이는 상황을 줄이기 위한 처리입니다.

처리 방식은 다음과 같습니다.

1. `IsInsideBuilding()`으로 현재 위치가 건물 내부인지 확인합니다.
2. 내부로 처음 진입했다고 판단되면 `FindNearestRoadXZ()`로 가까운 도로 또는 개방 공간을 찾습니다.
3. 내부 상태 동안 `_targetPosition`의 X/Z를 찾은 위치로 고정합니다.
4. 내부가 아니게 되면 고정을 해제합니다.

`IsInsideBuilding()`은 현재 위치에서 앞, 뒤, 오른쪽, 왼쪽 4방향으로 Raycast를 쏩니다.
4방향이 모두 건물 레이어에 막히면 건물 내부로 판단합니다.

```text
앞/뒤/좌/우 Raycast
  -> 4방향 모두 건물 충돌
  -> 건물 내부로 판단
```

`FindNearestRoadXZ()`는 현재 위치 주변을 반경 2m 단위로 넓혀가며 도로 레이어 또는 건물 밖 공간을 찾습니다.

## TeleportTo

```csharp
public void TeleportTo(double latitude, double longitude)
```

특정 위도/경도로 카메라를 즉시 이동시키는 메서드입니다.
길찾기에서 목적지 또는 선택 위치로 카메라를 이동시키는 기능에 사용할 수 있습니다.

동작 순서는 다음과 같습니다.

1. GPS 서비스와 `CesiumGlobeAnchor`를 확보합니다.
2. `GPSLocationService.ConvertToUnityPosition()`으로 위도/경도를 Unity 좌표로 변환합니다.
3. 임시 Y를 `변환 위치 Y + _eyeHeight`로 설정합니다.
4. `_targetPosition`과 카메라 위치를 즉시 갱신합니다.
5. 지면 높이 캐시를 무효화합니다.
6. 새 위치의 실제 지면 높이 샘플링을 시작합니다.

```text
TeleportTo(lat, lon)
  -> GPS 좌표를 Unity 좌표로 변환
  -> 카메라 즉시 이동
  -> 지면 높이 캐시 초기화
  -> Cesium/Raycast 지면 높이 다시 샘플링
```

## 전체 동작 구조

```text
Awake()
  -> GPS/권한 컴포넌트 자동 검색
  -> CesiumGlobeAnchor 확보
  -> CesiumCameraController 비활성화

Start()
  -> 회전 초기값 저장
  -> 나침반/자이로 센서 초기화
  -> 권한 이벤트 구독
  -> 이미 권한이 있으면 GPS 추적 시작

권한 허용
  -> StartGPSTracking()
  -> GPSLocationService.OnRawPositionUpdated 구독
  -> GPSLocationService.StartGPS()

GPS 위치 수신
  -> OnGPSPositionUpdated(rawUnityPosition)
  -> X/Z는 GPS 변환 위치 사용
  -> Y는 지면 높이 + 눈높이로 보정
  -> _targetPosition 갱신
  -> 최초 위치면 즉시 배치

Update()
  -> UpdateRotation()
  -> 건물 내부 보정 확인
  -> 현재 위치에서 _targetPosition으로 Lerp 이동

OnDestroy()
  -> 센서 비활성화
  -> GPS 이벤트 구독 해제
  -> 권한 이벤트 구독 해제
```

## 다른 GPS 코드와의 관계

GPS 관련 컴포넌트의 책임은 다음처럼 나뉩니다.

- `LocationPermissionHandler`: 위치 권한 확보
- `GPSLocationService`: GPS 수신, 위도/경도/고도 저장, Cesium/Unity 좌표 변환
- `FirstPersonGPSController`: 변환된 위치 수신, 카메라 위치/방향 갱신, 지면 높이 보정, 건물 내부 보정
- `MinimapController`: `FirstPersonGPSController`의 Transform을 따라가며 미니맵 표시

연결 흐름은 다음과 같습니다.

```text
LocationPermissionHandler
  -> OnPermissionGranted

FirstPersonGPSController
  -> GPSLocationService.StartGPS()
  -> GPSLocationService.OnRawPositionUpdated 구독

GPSLocationService
  -> GPS 좌표 수신
  -> Unity 좌표 변환
  -> OnRawPositionUpdated(rawUnityPos)

FirstPersonGPSController
  -> rawUnityPos 기반 카메라 목표 위치 계산
  -> 지면 높이 보정
  -> 카메라 위치와 방향 갱신
```

정리하면 `FirstPersonGPSController`는 GPS 좌표를 실제 1인칭 탐색 경험으로 바꿔주는 카메라 제어 레이어입니다.
