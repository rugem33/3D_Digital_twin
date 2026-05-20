using CesiumForUnity;
using UnityEngine;
using UnityEngine.UI;

namespace Rugem.RoadTools
{
    /// <summary>
    /// 상단 미니맵을 관리합니다.
    /// 독립적인 직교 투영 카메라를 생성해 RenderTexture에 렌더링하고,
    /// Canvas의 RawImage에 바인딩합니다.
    ///
    /// 오버뷰 모드: EnterOverviewMode() 호출 시 카메라가 경로 중심으로 이동하고
    ///              orthographicSize를 확대해 전체 경로를 한눈에 보여 줍니다.
    /// </summary>
    public class MinimapController : MonoBehaviour
    {
        [Header("Follow Target")]
        /// <summary>미니맵 카메라가 추적할 대상 Transform (기본: Camera.main)</summary>
        [SerializeField] private Transform _followTarget;

        /// <summary>플레이어 방향 화살표 회전에 사용할 방향 기준 Transform (기본: Camera.main)</summary>
        [SerializeField] private Transform _directionTarget;

        [Header("Minimap Camera")]
        /// <summary>미니맵 카메라와 추적 대상 간의 수직 오프셋 (미터). 클수록 높이서 내려다봄.</summary>
        [SerializeField] private float _cameraHeight = 400f;

        /// <summary>직교 투영 크기 (미터). 클수록 넓은 범위 표시. 오버뷰 모드 시 자동 확대됨.</summary>
        [SerializeField] private float _orthographicSize = 80f;

        [Header("Minimap Texture")]
        /// <summary>RenderTexture 해상도 (픽셀, 정사각형). 512 권장.</summary>
        [SerializeField] private int _textureSize = 512;

        /// <summary>플레이어 방향 화살표 색상</summary>
        [SerializeField] private Color _markerColor = new Color(0.13f, 0.59f, 0.95f, 1f);

        [Header("Canvas UI")]
        /// <summary>RenderTexture를 표시할 Canvas의 RawImage 컴포넌트</summary>
        [SerializeField] private RawImage _minimapRawImage;

        /// <summary>플레이어 방향 화살표 RectTransform (회전으로 방향 표시)</summary>
        [SerializeField] private RectTransform _playerArrow;

        /// <summary>
        /// 화살표 UI의 기본 회전 오프셋 (도).
        /// 화살표 스프라이트가 위쪽(북쪽)을 가리키면 0, 오른쪽을 가리키면 -90 등으로 조정.
        /// </summary>
        [SerializeField] private float _playerArrowYawOffset = 90f;

        /// <summary>시작 시 나침반 값으로 화살표 초기 방향을 설정할지 여부</summary>
        [SerializeField] private bool _initializePlayerArrowFromCompass = true;

        /// <summary>미니맵 전체 루트 GameObject (오버뷰 모드 시 비활성화)</summary>
        [SerializeField] private GameObject _minimapRoot;

        /// <summary>미니맵 크기 상수 (화면 높이 대비 비율, NavigationUIController와 공유)</summary>
        internal const float MapSizeRatioConst = 0.22f;

        // ── 내부 상태 ───────────────────────────────────────────────────────────

        /// <summary>런타임 생성된 직교 투영 미니맵 카메라</summary>
        private Camera _minimapCam;

        /// <summary>미니맵 카메라가 렌더링하는 대상 텍스처</summary>
        private RenderTexture _rt;

        /// <summary>Cesium 타일 스트리밍에 미니맵 카메라를 등록하기 위한 매니저</summary>
        private CesiumCameraManager _cameraManager;

        /// <summary>CesiumCameraManager에 등록 완료 여부 (OnDestroy에서 해제에 사용)</summary>
        private bool _registeredWithCameraManager;

        /// <summary>현재 오버뷰 모드인지 여부</summary>
        private bool _overviewMode;

        /// <summary>오버뷰 진입 전 orthographicSize 백업 (ExitOverviewMode 시 복원)</summary>
        private float _savedOrthoSize;

        /// <summary>나침반 진단 로그 타이머 (1초마다 출력)</summary>
        private float _diagnosticTimer;

        // ── 공개 프로퍼티 ───────────────────────────────────────────────────────

        /// <summary>미니맵 RenderTexture (NavigationUIController 오버뷰 표시에 사용)</summary>
        public RenderTexture OverviewTexture => _rt;

        /// <summary>현재 미니맵 카메라의 orthographicSize (오버뷰 좌표 변환에 사용)</summary>
        public float CurrentOrthoSize => _minimapCam != null ? _minimapCam.orthographicSize : _orthographicSize;

        /// <summary>현재 미니맵 카메라 Unity 월드 위치 (오버뷰 좌표 변환에 사용)</summary>
        public Vector3 CurrentCamPosition => _minimapCam != null ? _minimapCam.transform.position : Vector3.zero;

        // ── 오버뷰 모드 ─────────────────────────────────────────────────────────

        /// <summary>
        /// 미니맵 카메라를 플레이어와 목적지의 중간 지점으로 이동하고
        /// orthographicSize를 확대해 전체 경로가 보이는 오버뷰 모드로 전환합니다.
        /// </summary>
        public void EnterOverviewMode(Vector3 playerWorldPos, Vector3 destWorldPos)
        {
            if (_minimapCam == null) return;

            if (!_overviewMode)
                _savedOrthoSize = _minimapCam.orthographicSize;

            // 두 점의 중심으로 카메라 이동
            float midX = (playerWorldPos.x + destWorldPos.x) * 0.5f;
            float midZ = (playerWorldPos.z + destWorldPos.z) * 0.5f;
            float dx = Mathf.Abs(destWorldPos.x - playerWorldPos.x) * 0.5f;
            float dz = Mathf.Abs(destWorldPos.z - playerWorldPos.z) * 0.5f;
            // 1.4배 여백을 두어 경로 전체가 프레임에 들어오도록 크기 조정
            float size = Mathf.Max(dx, dz, 50f) * 1.4f;

            _minimapCam.transform.position = new Vector3(midX, _minimapCam.transform.position.y, midZ);
            _minimapCam.orthographicSize = size;
            _overviewMode = true;
            UpdateCanvasUI();
        }

        /// <summary>
        /// 오버뷰 모드를 해제하고 이전 orthographicSize로 복원합니다.
        /// 미니맵이 다시 플레이어를 팔로우하기 시작합니다.
        /// </summary>
        public void ExitOverviewMode()
        {
            if (_minimapCam == null || !_overviewMode) return;

            _minimapCam.orthographicSize = _savedOrthoSize;
            _overviewMode = false;
            UpdateCanvasUI();
        }

        /// <summary>플레이어 화살표 방향을 재보정합니다 (현재 빈 구현, 확장 예정).</summary>
        public void RecalibratePlayerArrow()
        {
        }

        // ── 생명주기 ────────────────────────────────────────────────────────────

        private void Awake()
        {
            ResolveFollowTarget();
            ResolveDirectionTarget();
        }

        private void Start()
        {
            ResolveFollowTarget();
            ResolveDirectionTarget();
            EnableCompassForPlayerArrow();
            CreateMinimapCamera();
            BindCanvasUI();
        }

        private void LateUpdate()
        {
            if (_minimapCam == null || _followTarget == null) return;

            // 오버뷰 모드가 아닐 때만 플레이어 추적
            if (!_overviewMode)
            {
                Vector3 p = _followTarget.position;
                _minimapCam.transform.position = new Vector3(p.x, p.y + _cameraHeight, p.z);
            }

            // 다른 오브젝트가 회전을 건드릴 수 있으므로 매 프레임 수직 하향을 강제
            _minimapCam.transform.rotation = Quaternion.Euler(90f, 0f, 0f);

            UpdateCanvasUI();
        }

        private void OnDestroy()
        {
            // CesiumCameraManager 등록 해제
            if (_minimapCam != null)
            {
                if (_cameraManager != null && _registeredWithCameraManager)
                    _cameraManager.additionalCameras.Remove(_minimapCam);

                Destroy(_minimapCam.gameObject);
            }

            if (_rt != null)
            {
                _rt.Release();
                Destroy(_rt);
            }
        }

        // ── 내부 초기화 ─────────────────────────────────────────────────────────

        /// <summary>Inspector 미설정 시 Camera.main을 팔로우 대상으로 자동 할당합니다.</summary>
        private void ResolveFollowTarget()
        {
            if (_followTarget != null)
                return;

            if (Camera.main != null)
                _followTarget = Camera.main.transform;
        }

        /// <summary>방향 기준 대상이 없으면 Camera.main → _followTarget 순으로 폴백합니다.</summary>
        private void ResolveDirectionTarget()
        {
            if (_directionTarget != null) return;

            if (Camera.main != null)
            {
                _directionTarget = Camera.main.transform;
                return;
            }

            _directionTarget = _followTarget;
        }

        /// <summary>플레이어 화살표 초기 방향 설정 옵션이 켜져 있으면 나침반을 활성화합니다.</summary>
        private void EnableCompassForPlayerArrow()
        {
            if (_initializePlayerArrowFromCompass)
                Input.compass.enabled = true;
        }

        /// <summary>
        /// RawImage에 RenderTexture를 바인딩하고 플레이어 화살표 색상을 설정합니다.
        /// </summary>
        private void BindCanvasUI()
        {
            if (_minimapRawImage != null)
                _minimapRawImage.texture = _rt;

            if (_playerArrow != null)
            {
                var arrowImage = _playerArrow.GetComponent<Image>();
                if (arrowImage != null)
                    arrowImage.color = _markerColor;

                var arrowRawImage = _playerArrow.GetComponent<RawImage>();
                if (arrowRawImage != null)
                    arrowRawImage.color = Color.white;
            }

            UpdateCanvasUI();
        }

        /// <summary>
        /// 미니맵 루트 표시/숨김 및 플레이어 화살표 회전을 갱신합니다.
        /// 오버뷰 모드 중에는 미니맵 루트를 비활성화합니다.
        /// </summary>
        private void UpdateCanvasUI()
        {
            if (_minimapRoot != null)
                _minimapRoot.SetActive(!_overviewMode);

            if (_playerArrow == null || _followTarget == null) return;

            float yaw = GetDirectionYaw();
            // UI는 Z축 회전으로 방향 표현, yaw 오프셋으로 스프라이트 기본 방향 보정
            _playerArrow.localEulerAngles = new Vector3(0f, 0f, -yaw);
        }

        /// <summary>
        /// 런타임에 직교 투영 미니맵 카메라를 생성하고 RenderTexture를 연결합니다.
        /// UI 레이어를 cullingMask에서 제외해 UI가 미니맵에 겹치지 않도록 합니다.
        /// Cesium3DTileset 스트리밍을 위해 CesiumCameraManager에 등록합니다.
        /// </summary>
        private void CreateMinimapCamera()
        {
            if (_minimapCam != null) return;

            var go = new GameObject("[MinimapCamera]");
            // 부모 없이 씬 루트에 배치 — 플레이어 카메라 회전(yaw/pitch)을 상속받으면
            // 미니맵 지도가 플레이어가 돌 때마다 같이 회전하는 문제가 발생한다.
            go.transform.rotation = Quaternion.Euler(90f, 0f, 0f);

            _minimapCam = go.AddComponent<Camera>();
            _minimapCam.orthographic = true;
            _minimapCam.orthographicSize = _orthographicSize;
            _minimapCam.clearFlags = CameraClearFlags.SolidColor;
            _minimapCam.backgroundColor = new Color(0.15f, 0.18f, 0.24f);
            _minimapCam.nearClipPlane = 1f;
            _minimapCam.farClipPlane = _cameraHeight + 200f;
            _minimapCam.depth = -2; // 메인 카메라보다 먼저 렌더링

            // UI 레이어를 렌더링 대상에서 제외
            int uiLayer = LayerMask.NameToLayer("UI");
            _minimapCam.cullingMask = uiLayer >= 0 ? ~(1 << uiLayer) : ~0;

            _rt = new RenderTexture(_textureSize, _textureSize, 16, RenderTextureFormat.Default);
            _rt.Create();
            _minimapCam.targetTexture = _rt;

            // Cesium 타일 스트리밍 대상에 미니맵 카메라 등록
            _cameraManager = FindAnyObjectByType<CesiumCameraManager>();
            if (_cameraManager != null)
            {
                if (!_cameraManager.additionalCameras.Contains(_minimapCam))
                    _cameraManager.additionalCameras.Add(_minimapCam);

                _registeredWithCameraManager = true;
                Debug.Log("[Minimap] Registered minimap camera with CesiumCameraManager.");
            }
            else
            {
                Debug.LogWarning("[Minimap] CesiumCameraManager not found.");
            }
        }

        // ── 방향 계산 ───────────────────────────────────────────────────────────

        /// <summary>
        /// 대상 Transform의 수평(XZ) yaw를 반환합니다.
        /// forward가 거의 수직이면 eulerAngles.y를 폴백으로 사용합니다.
        /// </summary>
        private static float GetHorizontalYaw(Transform target)
        {
            Vector3 forward = target.forward;
            forward.y = 0f;
            if (forward.sqrMagnitude < 0.0001f)
                return target.eulerAngles.y;

            return Quaternion.LookRotation(forward.normalized, Vector3.up).eulerAngles.y;
        }

        /// <summary>
        /// 플레이어 화살표에 사용할 yaw를 반환합니다.
        /// Camera.main의 UpdateRotation()이 이미 compass+gyro 융합(northYaw 보정)을 완료했으므로
        /// 카메라 yaw를 직접 읽는 것이 가장 정확합니다.
        /// </summary>
        private float GetDirectionYaw()
        {
            // [DIAG] 1초마다 compass 상태 출력 — 확인 후 제거
            _diagnosticTimer += Time.deltaTime;
            if (_diagnosticTimer >= 1f)
            {
                _diagnosticTimer = 0f;
                float camYaw = GetCameraYaw();
                string mode = "camera";
                Debug.Log($"[Minimap] ts={Input.compass.timestamp:F2} true={Input.compass.trueHeading:F1} mag={Input.compass.magneticHeading:F1} camYaw={camYaw:F1} mode={mode}");
            }

            return GetCameraYaw();
        }

        /// <summary>Camera.main 또는 _directionTarget의 수평 yaw를 반환합니다.</summary>
        private float GetCameraYaw()
        {
            if (Camera.main != null)
                return GetHorizontalYaw(Camera.main.transform);

            Transform t = _directionTarget != null ? _directionTarget : _followTarget;
            return GetHorizontalYaw(t);
        }

        /// <summary>
        /// Input.compass에서 유효한 heading(도)을 읽어 반환합니다.
        /// trueHeading이 유효하면 우선 사용하고, 아니면 magneticHeading을 사용합니다.
        /// </summary>
        private bool TryGetCompassHeading(out float heading)
        {
            heading = 0f;
            if (Input.compass.timestamp <= 0.0) return false;

            float h = Input.compass.trueHeading;
            if (h < 0f || float.IsNaN(h))
                h = Input.compass.magneticHeading;
            if (h < 0f || float.IsNaN(h))
                return false;

            heading = h;
            return true;
        }
    }
}
