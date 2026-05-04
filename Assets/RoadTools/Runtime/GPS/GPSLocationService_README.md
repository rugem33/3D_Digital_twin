# GPSLocationService

파일 위치: `Assets/RoadTools/Runtime/GPS/GPSLocationService.cs`

`GPSLocationService`는 실제 GPS 위치를 수신하고, 위도/경도/고도 좌표를 Cesium 기반 Unity 월드 좌표로 변환하는 서비스 컴포넌트입니다.

`LocationPermissionHandler`가 "GPS를 사용할 권한이 있는지"를 담당한다면, `GPSLocationService`는 "현재 GPS 값을 읽고 Unity 공간의 위치로 바꾸는 일"을 담당합니다.
카메라를 직접 움직이지는 않고, 변환된 위치를 이벤트로 다른 컴포넌트에 전달합니다.

## 핵심 역할

- Unity `Input.location`으로 GPS 위치 수신
- 마지막 위도, 경도, 고도 값 저장
- WGS84 위도/경도/고도 좌표를 Cesium ECEF 좌표로 변환
- Cesium ECEF 좌표를 Unity 월드 좌표로 변환
- GPS 위치가 튀지 않도록 `Lerp`로 부드러운 위치 값 유지
- 새 GPS 위치가 들어오면 `OnRawPositionUpdated` 이벤트 호출
- Unity Editor에서는 실제 GPS 대신 Cesium 기준 좌표로 시뮬레이션

## Inspector 설정 필드

```csharp
[SerializeField] private CesiumGeoreference _georeference;
```

Cesium 좌표 변환의 기준입니다.
GPS 좌표를 Unity 월드 좌표로 바꾸려면 반드시 필요합니다.
Inspector에서 직접 연결하지 않으면 `Awake()`에서 씬 안의 `CesiumGeoreference`를 자동 검색합니다.

```csharp
[SerializeField] private float _desiredAccuracyInMeters = 1f;
[SerializeField] private float _updateDistanceInMeters = 0.5f;
[SerializeField] private float _pollIntervalSeconds = 1f;
```

GPS 수신 설정입니다.

- `_desiredAccuracyInMeters`: 요청할 GPS 정확도입니다. 값이 낮을수록 더 높은 정확도를 요구합니다.
- `_updateDistanceInMeters`: 이 거리 이상 이동했을 때 위치 갱신을 요청합니다.
- `_pollIntervalSeconds`: GPS 데이터를 확인하는 주기입니다.

```csharp
[SerializeField] private float _lerpSpeed = 5f;
```

위치 보간 속도입니다.
GPS 좌표가 순간적으로 튀거나 계단식으로 갱신될 때, 외부에서 사용할 수 있는 `SmoothedUnityPosition`을 부드럽게 따라가게 합니다.

## 공개 상태 값

```csharp
public Vector3 SmoothedUnityPosition { get; private set; }
public Vector3 TargetUnityPosition { get; private set; }
```

`TargetUnityPosition`은 마지막 GPS 좌표를 Unity 좌표로 변환한 목표 위치입니다.
`SmoothedUnityPosition`은 `TargetUnityPosition`을 향해 `Lerp`로 보간된 위치입니다.

```csharp
public double CurrentLatitude { get; private set; }
public double CurrentLongitude { get; private set; }
public double CurrentAltitude { get; private set; }
```

마지막으로 수신한 GPS 위도, 경도, 고도입니다.
다른 컴포넌트가 현재 GPS 값을 참조할 때 사용합니다.

```csharp
public bool IsRunning { get; private set; }
```

GPS 서비스가 현재 실행 중인지 나타냅니다.

```csharp
public System.Action<Vector3> OnRawPositionUpdated;
```

새 GPS 좌표가 Unity 월드 좌표로 변환되었을 때 호출되는 이벤트입니다.
인자로 전달되는 값은 보간 전 원본 변환 위치입니다.

`FirstPersonGPSController`는 이 이벤트를 구독해서 카메라 목표 위치를 갱신합니다.

## 초기화

```csharp
private void Awake()
```

`_georeference`가 Inspector에 연결되어 있지 않으면 씬에서 `CesiumGeoreference`를 찾습니다.

```csharp
_georeference = FindAnyObjectByType<CesiumGeoreference>();
```

찾지 못하면 좌표 변환을 할 수 없으므로 오류 로그를 남깁니다.

## GPS 시작

```csharp
public void StartGPS()
```

GPS 수신 코루틴을 시작합니다.
이미 실행 중인 코루틴이 있으면 먼저 중지한 뒤 새로 시작합니다.

```csharp
if (_gpsCoroutine != null)
    StopCoroutine(_gpsCoroutine);

_gpsCoroutine = StartCoroutine(GPSUpdateLoop());
```

일반적으로 이 메서드는 위치 권한이 허용된 뒤 호출해야 합니다.
예를 들어 `FirstPersonGPSController`는 `LocationPermissionHandler.OnPermissionGranted` 이벤트를 받은 뒤 `StartGPS()`를 호출합니다.

## GPS 중지

```csharp
public void StopGPS()
```

GPS 수신을 중단합니다.

1. `IsRunning`을 `false`로 설정합니다.
2. 실행 중인 GPS 코루틴을 중지합니다.
3. Unity 위치 서비스가 실행 중이면 `Input.location.Stop()`을 호출합니다.

`OnDestroy()`에서도 `StopGPS()`가 호출되므로, 오브젝트가 제거될 때 위치 서비스가 정리됩니다.

## 모바일 GPS 수신 흐름

```csharp
private IEnumerator GPSUpdateLoop()
```

Unity Editor가 아닌 실제 빌드에서는 다음 순서로 동작합니다.

1. 기기 위치 서비스가 켜져 있는지 확인합니다.
2. `Input.location.Start()`로 Unity 위치 서비스를 시작합니다.
3. 최대 20초 동안 초기화를 기다립니다.
4. 상태가 `Running`이면 GPS 수신 루프에 들어갑니다.
5. 주기적으로 `Input.location.lastData`를 읽습니다.
6. 읽은 위도/경도/고도를 `ProcessLocationData()`에 전달합니다.
7. 위치 서비스가 `Failed` 또는 `Stopped`가 되면 잠시 대기 후 재시작을 시도합니다.

흐름은 다음과 같습니다.

```text
GPSUpdateLoop()
  -> Input.location.isEnabledByUser 확인
  -> Input.location.Start()
  -> Initializing 상태 대기, 최대 20초
  -> Running 상태 확인
  -> IsRunning = true
  -> 반복:
       Running이면 lastData 읽기
       Failed/Stopped이면 Stop 후 재시작 시도
       _pollIntervalSeconds 만큼 대기
```

## Unity Editor 시뮬레이션

Unity Editor에서는 실제 GPS를 사용하지 않습니다.
대신 `CesiumGeoreference`의 기준 위도/경도를 현재 위치처럼 사용합니다.

```csharp
double simLat = _georeference != null ? _georeference.latitude : 37.5662952;
double simLon = _georeference != null ? _georeference.longitude : 126.9779692;
```

`CesiumGeoreference`가 없으면 서울 시청 근처 좌표를 기본값으로 사용합니다.
이후 `ProcessLocationData(simLat, simLon, 0.0)`을 한 번 호출하고 코루틴을 종료합니다.

## 위치 데이터 처리

```csharp
private void ProcessLocationData(double lat, double lon, double alt)
```

GPS 값이 들어오면 다음 순서로 처리합니다.

1. `CurrentLatitude`, `CurrentLongitude`, `CurrentAltitude`를 갱신합니다.
2. `ConvertToUnityPosition()`으로 GPS 좌표를 Unity 월드 좌표로 변환합니다.
3. 변환 결과를 `TargetUnityPosition`에 저장합니다.
4. 첫 GPS 수신이라면 `SmoothedUnityPosition`도 즉시 같은 값으로 맞춥니다.
5. `OnRawPositionUpdated` 이벤트를 호출합니다.
6. 로그를 남깁니다.

첫 수신에서 `SmoothedUnityPosition`을 즉시 맞추는 이유는 시작 위치가 `Vector3.zero`에서 천천히 이동하는 문제를 막기 위해서입니다.

## 좌표 변환 과정

```csharp
public Vector3 ConvertToUnityPosition(double latitude, double longitude, double altitude = 0.0)
```

GPS 좌표를 Unity 월드 좌표로 변환하는 핵심 메서드입니다.

먼저 WGS84 위도/경도/고도를 Cesium ECEF 좌표로 변환합니다.
ECEF는 지구 중심 기준의 3D 좌표계입니다.

```csharp
double3 ecef = CesiumWgs84Ellipsoid.LongitudeLatitudeHeightToEarthCenteredEarthFixed(
    new double3(longitude, latitude, altitude));
```

여기서 `double3`의 순서는 `longitude, latitude, altitude`입니다.
일반적으로 말하는 위도/경도 순서와 다르므로 주의해야 합니다.

그 다음 `CesiumGeoreference`를 기준으로 ECEF 좌표를 Unity 월드 좌표로 바꿉니다.

```csharp
double3 unityCoords = _georeference.TransformEarthCenteredEarthFixedPositionToUnity(ecef);
```

마지막으로 Unity에서 쓰기 쉬운 `Vector3`로 변환해서 반환합니다.

## 위치 보간

```csharp
private void Update()
```

GPS 서비스가 실행 중이면 매 프레임 `SmoothedUnityPosition`을 `TargetUnityPosition` 쪽으로 보간합니다.

```csharp
SmoothedUnityPosition = Vector3.Lerp(
    SmoothedUnityPosition,
    TargetUnityPosition,
    Time.deltaTime * _lerpSpeed);
```

이 값은 GPS 갱신 간격이 길거나 위치가 갑자기 튀는 상황에서 부드러운 위치 표시가 필요할 때 사용할 수 있습니다.

현재 `OnRawPositionUpdated` 이벤트는 보간된 위치가 아니라 원본 변환 위치를 전달합니다.
따라서 카메라 쪽에서 자체 보간을 할 수도 있고, 필요한 경우 `SmoothedUnityPosition`을 직접 참조할 수도 있습니다.

## 전체 동작 구조

```text
Awake()
  -> CesiumGeoreference 자동 검색

StartGPS()
  -> 기존 GPS 코루틴 중지
  -> GPSUpdateLoop 코루틴 시작

GPSUpdateLoop()
  -> 모바일: Input.location.Start()
  -> GPS 초기화 대기
  -> Running 상태 확인
  -> 주기적으로 lastData 읽기
  -> ProcessLocationData()

ProcessLocationData()
  -> 위도/경도/고도 저장
  -> ConvertToUnityPosition()
  -> TargetUnityPosition 갱신
  -> 첫 위치면 SmoothedUnityPosition 즉시 동기화
  -> OnRawPositionUpdated 이벤트 호출

Update()
  -> SmoothedUnityPosition을 TargetUnityPosition 쪽으로 Lerp

StopGPS()
  -> 코루틴 중지
  -> Input.location.Stop()
```

## 다른 GPS 코드와의 관계

GPS 관련 컴포넌트의 책임은 다음처럼 나뉩니다.

- `LocationPermissionHandler`: 위치 권한 확인과 요청
- `GPSLocationService`: GPS 수신, 위도/경도/고도 저장, Cesium/Unity 좌표 변환
- `FirstPersonGPSController`: 변환된 위치를 받아 카메라 위치와 방향 갱신

전체 연결 흐름은 다음과 같습니다.

```text
LocationPermissionHandler
  -> 권한 허용 이벤트 발생

FirstPersonGPSController
  -> GPSLocationService.StartGPS() 호출
  -> GPSLocationService.OnRawPositionUpdated 구독

GPSLocationService
  -> GPS 좌표 수신
  -> Unity 월드 좌표로 변환
  -> OnRawPositionUpdated(rawUnityPos) 호출

FirstPersonGPSController
  -> rawUnityPos를 받아 카메라 목표 위치 갱신
```

정리하면 `GPSLocationService`는 GPS 시스템의 위치 데이터 중심 서비스입니다.
권한 UI나 카메라 이동은 담당하지 않고, GPS 좌표를 신뢰 가능한 Unity 월드 좌표로 변환해서 외부에 제공하는 역할에 집중합니다.
