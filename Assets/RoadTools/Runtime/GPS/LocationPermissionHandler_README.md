# LocationPermissionHandler

파일 위치: `Assets/RoadTools/Runtime/GPS/LocationPermissionHandler.cs`

`LocationPermissionHandler`는 Android/iOS 위치 권한을 확인하고 요청하는 컴포넌트입니다.
GPS 좌표를 직접 읽는 코드는 아니며, GPS를 시작해도 되는 권한 상태인지 판단한 뒤 다른 컴포넌트에 이벤트로 알려주는 권한 게이트 역할을 합니다.

실제 GPS 수신과 좌표 변환은 `GPSLocationService`가 담당합니다.
`LocationPermissionHandler`는 그 전에 "위치 권한이 있으니 GPS를 시작해도 된다" 또는 "위치 권한이 없으니 GPS를 시작할 수 없다"를 알려줍니다.

## 핵심 역할

- 위치 권한이 이미 있는지 확인
- 권한이 없으면 Android/iOS 시스템 권한 팝업 요청
- 권한 허용 상태를 `IsPermissionGranted`에 저장
- 권한 허용 시 `OnPermissionGranted` 이벤트 호출
- 권한 거부 시 `OnPermissionDenied` 이벤트 호출
- 권한 거부 안내 UI 표시
- 설정 화면 열기 버튼과 권한 재요청 버튼 처리

## Inspector 연결 필드

```csharp
[SerializeField] private GameObject _permissionDeniedPanel;
[SerializeField] private Button _openSettingsButton;
[SerializeField] private Button _retryButton;
```

각 필드의 역할은 다음과 같습니다.

- `_permissionDeniedPanel`: 권한이 거부되었을 때 보여줄 안내 패널입니다.
- `_openSettingsButton`: 앱 설정 화면으로 이동하는 버튼입니다.
- `_retryButton`: 권한 요청을 다시 시도하는 버튼입니다.

세 필드는 선택 사항입니다.
Inspector에 연결되어 있으면 UI까지 함께 동작하고, 연결되어 있지 않아도 권한 확인과 이벤트 호출은 동작합니다.

## 공개 상태와 이벤트

```csharp
public bool IsPermissionGranted { get; private set; }
public System.Action OnPermissionGranted;
public System.Action OnPermissionDenied;
```

`IsPermissionGranted`는 현재 위치 권한이 허용된 상태인지 나타냅니다.
외부에서는 읽을 수만 있고, 값 변경은 `LocationPermissionHandler` 내부에서만 합니다.

`OnPermissionGranted`는 권한이 허용되었을 때 호출됩니다.
`FirstPersonGPSController` 같은 GPS 사용 컴포넌트는 이 이벤트를 구독한 뒤 GPS 추적을 시작합니다.

`OnPermissionDenied`는 권한이 거부되었을 때 호출됩니다.
GPS 사용 컴포넌트는 이 이벤트를 통해 GPS 추적 불가 상태를 알 수 있습니다.

## 시작 동작

```csharp
private void Start()
```

씬에서 컴포넌트가 시작되면 다음 순서로 초기화합니다.

1. 설정 열기 버튼이 있으면 `OpenAppSettings`를 클릭 이벤트에 연결합니다.
2. 재시도 버튼이 있으면 `CheckAndRequestPermission`을 클릭 이벤트에 연결합니다.
3. 권한 거부 안내 패널이 있으면 처음에는 숨깁니다.
4. `CheckAndRequestPermission()`을 호출해 즉시 권한 상태를 확인합니다.

즉, 이 컴포넌트가 활성화되면 별도 호출 없이 바로 권한 확인 절차가 시작됩니다.

## 권한 확인 및 요청

```csharp
public void CheckAndRequestPermission()
```

플랫폼별로 권한 처리 방식이 다릅니다.

### Android

Android에서는 `UnityEngine.Android.Permission` API를 사용합니다.

```csharp
Permission.HasUserAuthorizedPermission(Permission.FineLocation)
```

이미 `FineLocation` 권한이 있으면 바로 `HandlePermissionGranted()`를 호출합니다.
권한이 없으면 `PermissionCallbacks`를 만들고 시스템 권한 팝업을 요청합니다.

```csharp
Permission.RequestUserPermission(Permission.FineLocation, callbacks);
```

권한 요청 결과는 다음 콜백으로 처리합니다.

- `PermissionGranted`: 권한 허용 처리
- `PermissionDenied`: 권한 거부 처리
- `PermissionDeniedAndDontAskAgain`: 권한 거부 처리

`DontAskAgain`도 거부와 동일하게 처리합니다.
이 경우 이후 재요청 버튼만으로는 시스템 팝업이 다시 뜨지 않을 수 있으므로, 설정 화면 이동 버튼이 필요합니다.

### iOS

iOS에서는 Unity 위치 서비스를 시작하면 시스템 권한 팝업이 표시됩니다.

```csharp
Input.location.Start();
```

`CheckiOSPermission()` 코루틴은 최대 8초 동안 `Input.location.status`가 `Initializing`에서 벗어나기를 기다립니다.

```csharp
while (Input.location.status == LocationServiceStatus.Initializing && timeout > 0)
```

이후 상태가 `Running`이면 권한 허용으로 보고, 위치 서비스를 잠시 중지한 뒤 `HandlePermissionGranted()`를 호출합니다.
그 외 상태이면 위치 서비스를 중지하고 `HandlePermissionDenied()`를 호출합니다.

### Editor 및 기타 플랫폼

Android/iOS가 아닌 플랫폼에서는 권한 팝업 없이 바로 허용 처리합니다.

```csharp
HandlePermissionGranted();
```

Unity Editor에서 GPS 흐름을 테스트할 수 있도록 하기 위한 처리입니다.

## 권한 허용 처리

```csharp
private void HandlePermissionGranted()
```

권한이 허용되면 다음 순서로 처리합니다.

1. `IsPermissionGranted`를 `true`로 설정합니다.
2. 권한 거부 안내 패널이 있으면 숨깁니다.
3. `OnPermissionGranted` 이벤트를 호출합니다.
4. 디버그 로그를 남깁니다.

이 이벤트를 받은 `FirstPersonGPSController`는 `GPSLocationService.StartGPS()`를 호출해 실제 GPS 추적을 시작할 수 있습니다.

## 권한 거부 처리

```csharp
private void HandlePermissionDenied()
```

권한이 거부되면 다음 순서로 처리합니다.

1. `IsPermissionGranted`를 `false`로 설정합니다.
2. 권한 거부 안내 패널이 있으면 표시합니다.
3. `OnPermissionDenied` 이벤트를 호출합니다.
4. 경고 로그를 남깁니다.

이 상태에서는 GPS 추적을 시작하면 안 됩니다.
사용자는 재시도 버튼을 누르거나, 설정 화면으로 이동해서 위치 권한을 직접 허용해야 합니다.

## 앱 설정 화면 열기

```csharp
private void OpenAppSettings()
```

권한이 거부된 뒤 사용자가 직접 앱 권한을 바꿀 수 있도록 설정 화면을 엽니다.

Android에서는 현재 앱의 상세 설정 화면으로 이동하는 `Intent`를 만듭니다.

```csharp
android.settings.APPLICATION_DETAILS_SETTINGS
```

앱의 패키지 식별자는 `Application.identifier`를 사용합니다.

iOS에서는 앱 설정 URL을 엽니다.

```csharp
Application.OpenURL("app-settings:");
```

## 전체 동작 흐름

```text
Start()
  -> 설정 열기 버튼 이벤트 연결
  -> 재시도 버튼 이벤트 연결
  -> 권한 거부 패널 숨김
  -> CheckAndRequestPermission()

CheckAndRequestPermission()
  -> Android: FineLocation 권한 확인/요청
  -> iOS: Input.location.Start()로 권한 확인
  -> Editor/기타: 바로 허용 처리

권한 허용
  -> IsPermissionGranted = true
  -> 권한 거부 패널 숨김
  -> OnPermissionGranted 이벤트 호출
  -> GPS 추적 시작 가능

권한 거부
  -> IsPermissionGranted = false
  -> 권한 거부 패널 표시
  -> OnPermissionDenied 이벤트 호출
  -> GPS 추적 중단 또는 시작 보류
  -> 재시도 버튼 또는 설정 버튼으로 복구 유도
```

## 다른 GPS 코드와의 관계

`LocationPermissionHandler`는 권한만 담당합니다.
위치 좌표를 읽거나 Cesium 좌표로 변환하지 않습니다.

관련 컴포넌트의 책임은 다음과 같이 나뉩니다.

- `LocationPermissionHandler`: 위치 권한 확인, 요청, 거부 UI 처리
- `GPSLocationService`: 실제 GPS 수신, 위도/경도/고도 저장, Cesium/Unity 좌표 변환
- `FirstPersonGPSController`: 권한 허용 이벤트를 받은 뒤 GPS 추적 시작, 카메라 위치와 방향 갱신

따라서 이 파일은 GPS 시스템의 첫 단계이며, "권한을 가져오는 역할"이라고 보면 됩니다.
