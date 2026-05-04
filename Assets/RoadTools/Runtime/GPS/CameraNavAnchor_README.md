# CameraNavAnchor

파일 위치: `Assets/RoadTools/Runtime/GPS/CameraNavAnchor.cs`

`CameraNavAnchor`는 메인 카메라의 현재 위치를 지면 위 위치로 투영해서 `mainCameraNav`라는 앵커 Transform으로 유지하는 보조 컴포넌트입니다.

1인칭 카메라는 사람 눈높이에 있지만, 길찾기나 경로 렌더링에서는 "카메라 바로 아래의 지면 위치"가 필요한 경우가 많습니다.
이 스크립트는 그 지면 위치를 계속 계산해서 별도 Transform으로 제공합니다.

## 핵심 역할

- 메인 카메라 아래 지면 위치 계산
- `mainCameraNav` 자식 오브젝트 생성 또는 재사용
- 매 프레임 `mainCameraNav`를 카메라 아래 지면 위치로 이동
- Raycast 실패 시 카메라보다 2m 아래 위치를 fallback으로 사용
- `NavigationService`, `RouteRenderer` 같은 경로 관련 코드가 출발점으로 사용할 수 있는 위치 제공

## 설정 필드

```csharp
[SerializeField] private float _raycastOriginHeight = 500f;
[SerializeField] private LayerMask _groundLayerMask = ~0;
```

`_raycastOriginHeight`는 카메라 위치보다 얼마나 위에서 Raycast를 시작할지 정합니다.
기본값은 500m입니다.

`_groundLayerMask`는 지면으로 인정할 레이어입니다.
기본값 `~0`은 모든 레이어를 대상으로 Raycast한다는 뜻입니다.
값이 0으로 설정되어도 코드에서 다시 `~0`으로 처리하므로 모든 레이어를 대상으로 삼습니다.

## 공개 속성

```csharp
public Transform NavTransform { get; private set; }
```

외부 코드가 사용할 수 있는 내비게이션용 지면 앵커 Transform입니다.
실제 GameObject 이름은 `mainCameraNav`입니다.

외부에서는 읽기만 가능하고, 이 스크립트 내부에서만 할당됩니다.

## 초기화 동작

```csharp
private void Awake()
```

`Awake()`에서는 현재 오브젝트의 자식 중 `mainCameraNav`를 찾습니다.

이미 있으면 해당 Transform을 `NavTransform`으로 사용합니다.
없으면 새 GameObject를 만들고 현재 오브젝트의 자식으로 붙입니다.

```text
Awake()
  -> transform.Find("mainCameraNav")
      -> 있음: 기존 Transform 재사용
      -> 없음: GameObject("mainCameraNav") 생성
  -> NavTransform에 저장
```

따라서 씬에 `mainCameraNav`를 미리 만들어 둘 수도 있고, 만들지 않아도 런타임에 자동 생성됩니다.

## 매 프레임 동작

```csharp
private void LateUpdate()
```

`LateUpdate()`에서는 `Camera.main`의 현재 위치를 가져온 뒤, 그 아래 지면 위치를 계산합니다.

```text
LateUpdate()
  -> Camera.main 확인
  -> NavTransform 확인
  -> Camera.main.transform.position 읽기
  -> SampleGroundBelow(camPos)
  -> NavTransform.position 갱신
```

`LateUpdate()`를 사용하는 이유는 다른 카메라 이동 코드가 `Update()`에서 먼저 위치를 변경한 뒤, 그 최종 결과를 기준으로 앵커 위치를 맞추기 좋기 때문입니다.

## 지면 위치 계산

```csharp
private Vector3 SampleGroundBelow(Vector3 camPos)
```

이 메서드는 카메라 아래의 지면 높이를 찾습니다.
X/Z는 카메라 위치를 그대로 사용하고, Y만 지면 높이로 바꿉니다.

처리 순서는 다음과 같습니다.

1. 카메라 위치보다 `_raycastOriginHeight`만큼 위에 Raycast 시작점을 만듭니다.
2. 아래 방향 `Vector3.down`으로 Raycast를 쏩니다.
3. 지면 충돌에 성공하면 충돌 지점의 Y를 사용합니다.
4. 실패하면 카메라보다 2m 아래 위치를 fallback으로 사용합니다.

개념은 다음과 같습니다.

```text
camPos
  -> origin = (camPos.x, camPos.y + _raycastOriginHeight, camPos.z)
  -> 아래 방향 Raycast
      -> 성공: (camPos.x, hit.point.y, camPos.z)
      -> 실패: (camPos.x, camPos.y - 2f, camPos.z)
```

코드에서는 Raycast 최대 거리를 다음처럼 계산합니다.

```csharp
float maxDist = _raycastOriginHeight * 2f + Mathf.Abs(camPos.y) + 100f;
```

카메라의 현재 Y가 높거나 낮아도 충분히 아래까지 검사하기 위한 값입니다.

## 전체 동작 구조

```text
Awake()
  -> 자식 mainCameraNav 검색
  -> 없으면 mainCameraNav 생성
  -> NavTransform 저장

LateUpdate()
  -> Camera.main 위치 확인
  -> 카메라 아래 지면으로 Raycast
  -> 성공하면 지면 Y 사용
  -> 실패하면 카메라보다 2m 아래 사용
  -> NavTransform.position 갱신
```

## 다른 코드와의 관계

`CameraNavAnchor`는 GPS를 직접 읽지 않고, 카메라를 직접 움직이지도 않습니다.
대신 현재 카메라 기준의 지면 위치를 만들어서 내비게이션 코드가 사용할 수 있게 합니다.

책임을 나누면 다음과 같습니다.

- `FirstPersonGPSController`: GPS 기반으로 카메라 위치와 방향을 이동
- `CameraNavAnchor`: 카메라 바로 아래 지면 위치를 `mainCameraNav`로 유지
- `NavigationService` / `RouteRenderer`: `mainCameraNav` 위치를 경로 시작점으로 사용 가능

연결 흐름은 다음과 같습니다.

```text
FirstPersonGPSController
  -> 카메라를 사용자 눈높이 위치로 이동

CameraNavAnchor
  -> Camera.main 아래 지면 위치 계산
  -> mainCameraNav Transform 갱신

NavigationService / RouteRenderer
  -> mainCameraNav를 출발점 또는 경로 기준점으로 사용
```

정리하면 `CameraNavAnchor`는 눈높이 카메라 위치를 내비게이션용 지면 위치로 변환해주는 앵커 컴포넌트입니다.
