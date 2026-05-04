# MinimapController

파일 위치: `Assets/RoadTools/Runtime/GPS/MinimapController.cs`

`MinimapController`는 위에서 내려다보는 미니맵 전용 카메라를 만들고, 그 카메라 화면을 `RenderTexture`로 렌더링한 뒤 Canvas UI의 `RawImage`에 연결하는 스크립트입니다. 이전의 `OnGUI` 기반 그리기 방식은 제거되었고, 미니맵의 원형 마스크, 프레임, 북쪽 표시, 화살표 이미지는 씬에 배치된 UI 오브젝트가 담당합니다.

## 씬 UI 계층

현재 `level2` 씬의 미니맵 계층은 다음과 같습니다.

```text
Main Camera
└── Minimap Service
    └── UI(Canvas)
        └── Minimap                 -> _minimapRoot
            ├── CircleMask
            │   └── RawImage        -> _minimapRawImage
            ├── FrameImage
            ├── North
            └── playerArrow         -> _playerArrow
```

각 오브젝트의 역할은 다음과 같습니다.

- `Minimap`: 미니맵 UI 전체 부모 오브젝트입니다. `_minimapRoot`에 연결합니다.
- `CircleMask`: 미니맵 텍스처를 원형으로 잘라내는 UI 마스크 오브젝트입니다.
- `RawImage`: 런타임에 생성된 `RenderTexture`를 표시합니다. `_minimapRawImage`에 연결합니다.
- `FrameImage`: 미니맵 외곽선 또는 프레임을 표시합니다.
- `North`: 북쪽 표시용 정적 UI 텍스트입니다.
- `playerArrow`: 플레이어 방향을 나타내는 화살표 UI입니다. `_playerArrow`에 연결합니다.

## Inspector 연결 필드

```csharp
[SerializeField] private Transform _followTarget;
```

미니맵 카메라가 따라갈 대상입니다. Inspector에서 직접 연결하지 않으면 `FirstPersonGPSController`를 찾아 그 `transform`을 사용합니다.

```csharp
[SerializeField] private float _cameraHeight = 400f;
```

추적 대상 기준으로 미니맵 카메라를 얼마나 위에 배치할지 결정합니다.

```csharp
[SerializeField] private float _orthographicSize = 80f;
```

미니맵 카메라의 기본 직교 카메라 크기입니다. 값이 클수록 더 넓은 영역이 보입니다.

```csharp
[SerializeField] private int _textureSize = 256;
```

런타임에 생성할 정사각형 `RenderTexture`의 가로/세로 해상도입니다.

```csharp
[SerializeField] private Color _markerColor = new Color(0.13f, 0.59f, 0.95f, 1f);
```

`playerArrow`가 `Image` 컴포넌트를 사용할 때 적용되는 색상입니다. `RawImage`를 사용하는 경우에는 PNG 원본 색과 알파를 유지하기 위해 `Color.white`로 설정합니다.

```csharp
[SerializeField] private RawImage _minimapRawImage;
```

미니맵 카메라가 렌더링한 `RenderTexture`를 표시할 Canvas `RawImage`입니다.

```csharp
[SerializeField] private RectTransform _playerArrow;
```

매 프레임 플레이어 방향에 맞춰 회전할 Canvas 화살표 오브젝트의 `RectTransform`입니다.

```csharp
[SerializeField] private GameObject _minimapRoot;
```

미니맵 UI 전체 루트 오브젝트입니다. 오버뷰 모드에서는 이 오브젝트를 숨깁니다.

## 런타임 내부 객체

```csharp
private Camera _minimapCam;
private RenderTexture _rt;
```

`_minimapCam`은 런타임에 생성되는 미니맵 전용 카메라입니다. `_rt`는 해당 카메라의 출력 대상이며, 이후 Canvas `RawImage`에 연결됩니다.

```csharp
private CesiumCameraManager _cameraManager;
private bool _registeredWithCameraManager;
```

Cesium의 추가 카메라 목록 등록 상태를 관리합니다. 미니맵 카메라에서도 Cesium 지형과 타일이 정상 렌더링되도록 하기 위한 처리입니다.

```csharp
private bool _overviewMode;
private float _savedOrthoSize;
```

오버뷰 모드 상태와, 오버뷰 모드 종료 시 복구할 기존 `orthographicSize` 값을 저장합니다.

## 시작 동작 과정

```text
Awake()
└── ResolveFollowTarget()

Start()
├── ResolveFollowTarget()
├── CreateMinimapCamera()
└── BindCanvasUI()
```

`ResolveFollowTarget()`는 Inspector에 연결된 `_followTarget`이 있으면 그대로 사용합니다. 없으면 씬에서 `FirstPersonGPSController`를 찾아 추적 대상으로 지정합니다.

`CreateMinimapCamera()`는 `[MinimapCamera]` 오브젝트를 만들고, 위에서 아래를 보는 직교 카메라로 설정합니다. 이후 `RenderTexture`를 생성해 카메라의 `targetTexture`로 지정하고, `CesiumCameraManager.additionalCameras`에 등록합니다.

`BindCanvasUI()`는 생성된 `RenderTexture`를 `_minimapRawImage.texture`에 연결합니다. 또한 화살표 UI 색상 처리를 한 뒤 `UpdateCanvasUI()`를 호출합니다.

## 매 프레임 동작 과정

```text
LateUpdate()
├── 오버뷰 모드가 아니면 미니맵 카메라를 추적 대상 위로 이동
└── UpdateCanvasUI()
    ├── 오버뷰 모드 여부에 따라 _minimapRoot 표시/숨김
    └── _playerArrow를 추적 대상의 수평 방향 기준으로 회전
```

일반 모드에서 미니맵 카메라는 추적 대상의 위쪽을 따라갑니다.

```csharp
Vector3 p = _followTarget.position;
_minimapCam.transform.position = new Vector3(p.x, p.y + _cameraHeight, p.z);
```

카메라는 생성 시점에 아래 방향을 보도록 회전됩니다.

```csharp
Quaternion.Euler(90f, 0f, 0f)
```

화살표는 추적 대상의 수평 방향만 사용해 회전합니다.

```csharp
float yaw = GetHorizontalYaw(_followTarget);
_playerArrow.localEulerAngles = new Vector3(0f, 0f, -yaw);
```

UI에서 화살표가 반대 방향으로 회전한다면 `-yaw`를 `yaw`로 바꾸면 됩니다.

## 오버뷰 모드

`EnterOverviewMode(Vector3 playerWorldPos, Vector3 destWorldPos)`는 플레이어와 목적지가 모두 보이도록 미니맵 카메라 위치와 줌을 조정합니다.

```text
플레이어 위치 + 목적지 위치
└── X/Z 중간 지점 계산
    └── 미니맵 카메라를 중간 지점으로 이동
        └── 두 지점 사이 거리 기준으로 orthographicSize 확대
```

오버뷰 모드 중에는 다음 동작이 적용됩니다.

- 미니맵 카메라가 `_followTarget`을 따라가지 않습니다.
- `_minimapRoot`가 숨겨집니다.
- `_savedOrthoSize`에 기존 줌 값이 저장됩니다.

`ExitOverviewMode()`는 저장해둔 `orthographicSize`를 복구하고, 오버뷰 모드를 끈 뒤 미니맵 UI를 다시 표시합니다.

## 정리 동작

`OnDestroy()`는 `CesiumCameraManager.additionalCameras`에서 미니맵 카메라를 제거하고, 런타임에 생성한 카메라 오브젝트를 삭제합니다. 이후 `RenderTexture`를 `Release()`하고 `Destroy()`합니다.

## 책임 분리

스크립트가 담당하는 부분:

- 미니맵 카메라 생성
- `RenderTexture` 생성
- Cesium 추가 카메라 등록
- 플레이어 추적용 카메라 위치 갱신
- Canvas `RawImage` 텍스처 연결
- 플레이어 화살표 회전
- 오버뷰 모드의 카메라 위치와 줌 제어

Canvas 계층이 담당하는 부분:

- 원형 마스킹
- 프레임과 그림자 시각 요소
- 북쪽 표시 텍스트
- 화살표 PNG 외형
- 미니맵의 화면상 위치와 크기
