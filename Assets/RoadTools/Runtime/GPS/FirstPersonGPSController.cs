using System.Collections;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.InputSystem;
using CesiumForUnity;
#if UNITY_ANDROID
using UnityEngine.Android;
#endif

namespace Rugem.RoadTools
{
    public enum RotationMode
    {
        Gyro,    // 자이로/컴퍼스 — 폰 방향이 카메라 방향을 결정
        Locked,  // 고정 — 현재 방향을 유지
        Drag     // 드래그 — 손가락 스와이프로 자유 회전
    }

    /// <summary>
    /// US-05: GPS 기반 1인칭 카메라 위치·방향 동기화
    /// 새 Input System(com.unity.inputsystem) 전용 — AttitudeSensor, Touchscreen 사용
    /// </summary>
    [RequireComponent(typeof(Camera))]
    [RequireComponent(typeof(CesiumGlobeAnchor))]
    public class FirstPersonGPSController : MonoBehaviour
    {
        [Header("의존성 연결")]
        [SerializeField] private GPSLocationService _gpsService;

        [Header("Cesium 지형 높이 샘플링")]
        [Tooltip("World Terrain Cesium3DTileset — 지형 표면 높이 정밀 측정에 사용됩니다.\n미설정 시 Physics.Raycast 폴백으로 동작합니다.")]
        [SerializeField] private Cesium3DTileset _worldTerrain;

        [Header("1인칭 카메라 높이")]
        [Tooltip("지면으로부터 눈높이 오프셋 (미터). 양수=위, 음수=아래")]
        [SerializeField] private float _eyeHeight = 2.0f;

        [Header("Raycast 폴백 지면 감지")]
        [SerializeField] private float _raycastOriginHeight = 500f;
        [SerializeField] private LayerMask _groundLayerMask = ~0;

        [Header("카메라 위치 보간")]
        [SerializeField] private float _positionLerpSpeed = 8f;

        [Header("방향 센서")]
        [SerializeField] private float _rotationLerpSpeed = 5f;
        [Tooltip("ON: 자이로 없는 기기에서 컴퍼스(Y축)만 사용 / OFF: 자동 감지")]
        [SerializeField] private bool _forceCompassOnly = false;
        [Tooltip("Use compass true heading first so the phone's real facing direction matches the world/minimap direction.")]
        [SerializeField] private bool _preferCompassHeading = false;
        [Tooltip("Yaw correction in degrees after a stable heading is accepted.")]
        [SerializeField] private float _headingYawCorrection = 0f;
        [Tooltip("Minimum horizontal heading strength required before the app accepts/recalibrates phone direction.")]
        [SerializeField, Range(0.1f, 0.95f)] private float _uprightHeadingMinHorizontal = 0.45f;
        [Tooltip("자이로 기준 yaw를 현재 카메라 yaw에 맞춰 시작합니다.")]
        [SerializeField] private bool _autoCalibrateGyroYaw = true;

        [Header("나침반 안정화")]
        [Tooltip("나침반 원형 평균 샘플 수. 1=비활성, 높을수록 부드럽고 반응 느림 (권장: 5)")]
        [SerializeField, Range(1, 9)] private int _compassSmoothSamples = 5;
        [Tooltip("GPS 점프 허용 최대치(m). 이 이상 급변한 신호는 노이즈로 거부. 0=비활성 (권장: 20)")]
        [SerializeField] private float _maxGPSJumpMeters = 20f;

        [Header("드래그 회전")]
        [Tooltip("드래그 감도 (높을수록 민감)")]
        [SerializeField] private float _dragSensitivity = 0.3f;

        [Header("건물 충돌 처리")]
        [Tooltip("건물로 인식할 레이어 (Cesium 3D 건물 타일 레이어 지정, 0=비활성화)")]
        [SerializeField] private LayerMask _buildingLayerMask;
        [Tooltip("도로로 인식할 레이어 (미설정 시 건물 밖 가장 가까운 위치로 고정)")]
        [SerializeField] private LayerMask _roadLayerMask;
        [Tooltip("건물 내부 감지용 수평 레이 길이 (미터)")]
        [SerializeField] private float _buildingRayLength = 15f;
        [Tooltip("인접 도로/개방 공간 탐색 최대 반경 (미터)")]
        [SerializeField] private float _roadSearchMaxRadius = 30f;

        // ── 내부 상태 ──────────────────────────────────────────────────────────

        private Vector3 _targetPosition;
        private bool _hasInitialPosition;

        private Quaternion _targetRotation;
        private bool _gyroAvailable;
        private bool _compassEnabled;
        private bool _gyroYawCalibrated;
        private float _gyroYawOffset;
        private float _compassCalibrationTimer;
        private const float CompassCalibrationTimeout = 3f;
        private ScreenOrientation _lastScreenOrientation;
        private bool _samplingHeight;

        private CesiumGlobeAnchor _globeAnchor;

        private RotationMode _rotationMode = RotationMode.Gyro;
        private Quaternion _lockedRotation;
        private float _dragYaw;
        private float _dragPitch;

        // 지면 높이 캐시 — GPS 고도 노이즈로 인한 카메라 상하 떨림 방지
        private float _cachedGroundY = float.MinValue;
        private Vector2 _lastGroundCheckXZ;
        private const float GroundCheckMoveThreshold = 2f; // 수평 2m 이상 이동 시 재감지

        // 건물 충돌 처리
        private bool _insideBuilding;
        private Vector2 _buildingLockedXZ; // x=world X, y=world Z
        private float _buildingCheckTimer;
        private const float BuildingCheckInterval = 0.2f; // 초당 5회 체크

        private GUIStyle _btnStyle;
        private GUIStyle _btnIconStyle;
        private GUIStyle _btnLabelStyle;
        private int _buttonStyleScreenWidth;
        private int _buttonStyleScreenHeight;
        private bool _gpsSubscribed;
        private int _heightSampleVersion;
        private Coroutine _iosPermissionCoroutine;

        // 나침반 안정화
        private float[] _compassBuffer;
        private int _compassBufferHead;
        private bool _compassBufferFull;
        private float _compassSinSum;
        private float _compassCosSum;
        private float _compassThrottleTimer;
        private float _cachedCompassHeading = -1f;
        private const float CompassUpdateInterval = 60f;

        // GPS 점프 필터
        private Vector2 _lastAcceptedXZ;
        private bool _hasLastAcceptedXZ;

        // ── 유니티 생명주기 ────────────────────────────────────────────────────

        private void Awake()
        {
            ResolveDependencies();
            EnsureGlobeAnchor();

            // CesiumCameraController와 위치·회전 충돌 방지
            var cesiumCam = GetComponent<CesiumCameraController>();
            if (cesiumCam != null)
            {
                cesiumCam.enabled = false;
                Debug.Log("[FirstPersonGPS] CesiumCameraController 비활성화");
            }
        }

        private void Start()
        {
            _targetRotation = transform.rotation;
            InitializeSensors();

            ResolveDependencies();
            EnsureGlobeAnchor();

            RequestLocationPermissionThenStartGPS();
        }

        private void Update()
        {
            // AttitudeSensor 지연 초기화 — Start() 시점에 null이었던 경우 대비
            if (!_gyroAvailable && !_forceCompassOnly && AttitudeSensor.current != null)
            {
                InputSystem.EnableDevice(AttitudeSensor.current);
                _gyroAvailable = true;
                ResetGyroCalibration();
                Debug.Log("[FirstPersonGPS] AttitudeSensor 지연 초기화 완료");
            }

            UpdateRotation();

            if (!_hasInitialPosition) return;
            EnsureGlobeAnchor();
            if (_globeAnchor == null) return;

            // 건물 충돌 처리 (0.2초 간격으로 체크)
            _buildingCheckTimer += Time.deltaTime;
            if (_buildingCheckTimer >= BuildingCheckInterval)
            {
                _buildingCheckTimer = 0f;
                HandleBuildingCollision();
            }

            Vector3 smoothed = Vector3.Lerp(
                transform.position, _targetPosition, Time.deltaTime * _positionLerpSpeed);
            _globeAnchor.transform.position = smoothed;
        }

        private void OnApplicationPause(bool paused)
        {
            if (_gyroAvailable && AttitudeSensor.current != null)
            {
                if (paused)
                    InputSystem.DisableDevice(AttitudeSensor.current);
                else
                {
                    InputSystem.EnableDevice(AttitudeSensor.current);
                    ResetGyroCalibration();
                    _compassThrottleTimer = CompassUpdateInterval; // 포그라운드 복귀 시 즉시 나침반 보정
                    Debug.Log("[FirstPersonGPS] AttitudeSensor 재활성화 (앱 재개)");
                }
            }
        }

        private void OnApplicationFocus(bool hasFocus)
        {
            if (hasFocus && _gyroAvailable && AttitudeSensor.current != null)
            {
                InputSystem.EnableDevice(AttitudeSensor.current);
                ResetGyroCalibration();
                _compassThrottleTimer = CompassUpdateInterval; // 포그라운드 복귀 시 즉시 나침반 보정
                Debug.Log("[FirstPersonGPS] AttitudeSensor 재활성화 (포커스 복귀)");
            }
        }

        private void OnDestroy()
        {
            if (_gyroAvailable && AttitudeSensor.current != null)
                InputSystem.DisableDevice(AttitudeSensor.current);
            if (_compassEnabled)
                Input.compass.enabled = false;

            if (_gpsService != null)
                _gpsService.OnRawPositionUpdated -= OnGPSPositionUpdated;
            _gpsSubscribed = false;
            if (_iosPermissionCoroutine != null)
                StopCoroutine(_iosPermissionCoroutine);
        }

        // ── 센서 초기화 (새 Input System) ─────────────────────────────────────

        private void InitializeSensors()
        {
            Input.compass.enabled = true;
            _compassEnabled = Input.compass.enabled;
            Debug.Log($"[FirstPersonGPS] compass.enabled={_compassEnabled} (ts={Input.compass.timestamp:F2})");

            if (!_forceCompassOnly && AttitudeSensor.current != null)
            {
                InputSystem.EnableDevice(AttitudeSensor.current);
                _gyroAvailable = true;
                Debug.Log("[FirstPersonGPS] AttitudeSensor(자이로) 활성화");
            }

            InitNoiseFilters();
        }

        private void InitNoiseFilters()
        {
            _compassBuffer     = new float[Mathf.Max(1, _compassSmoothSamples)];
            _compassBufferHead = 0;
            _compassBufferFull = false;
            _compassSinSum     = 0f;
            _compassCosSum     = 0f;
        }

        // ── 토글 버튼 UI ───────────────────────────────────────────────────────

        private void OnGUI()
        {
            EnsureButtonStyles();

            float margin  = Mathf.Clamp(Screen.width * 0.03f, 14f, 28f);
            float btnSize = Mathf.Clamp(Screen.height * 0.090f, 70f, 90f);
            // 미니맵(화면 높이의 22%) 아래에 버튼 배치
            float mapSize = Screen.height * MinimapController.MapSizeRatioConst;
            float btnY    = margin + mapSize + margin * 0.4f;
            float btnX    = Screen.width - btnSize - margin;

            // ── 회전 모드 토글 버튼 ──
            Rect buttonRect = new Rect(btnX, btnY, btnSize, btnSize);
            if (GUI.Button(buttonRect, "", _btnStyle))
                CycleRotationMode();

            string icon = _rotationMode switch
            {
                RotationMode.Gyro   => "◎",
                RotationMode.Locked => "■",
                RotationMode.Drag   => "↔",
                _                   => "?"
            };

            string label = _rotationMode switch
            {
                RotationMode.Gyro   => "GYRO",
                RotationMode.Locked => "LOCK",
                RotationMode.Drag   => "DRAG",
                _                   => "MODE"
            };

            GUI.Label(new Rect(buttonRect.x, buttonRect.y + btnSize * 0.05f, btnSize, btnSize * 0.58f), icon, _btnIconStyle);
            GUI.Label(new Rect(buttonRect.x, buttonRect.y + btnSize * 0.60f, btnSize, btnSize * 0.32f), label, _btnLabelStyle);

        }

        private void EnsureButtonStyles()
        {
            if (_btnStyle != null
                && _buttonStyleScreenWidth == Screen.width
                && _buttonStyleScreenHeight == Screen.height)
                return;

            _buttonStyleScreenWidth = Screen.width;
            _buttonStyleScreenHeight = Screen.height;

            _btnStyle = new GUIStyle(GUI.skin.button)
            {
                fontSize  = Mathf.RoundToInt(Mathf.Clamp(Screen.height * 0.018f, 12f, 18f)),
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleCenter,
                padding   = new RectOffset(4, 4, 4, 4),
                normal    = { textColor = Color.white, background = MakeSolidTex(new Color(0.08f, 0.12f, 0.18f, 0.92f)) },
                hover     = { textColor = Color.white, background = MakeSolidTex(new Color(0.12f, 0.18f, 0.28f, 0.96f)) },
                active    = { textColor = Color.white, background = MakeSolidTex(new Color(0.05f, 0.42f, 0.80f, 0.96f)) },
            };

            _btnIconStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize  = Mathf.RoundToInt(Mathf.Clamp(Screen.height * 0.042f, 30f, 44f)),
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleCenter,
                normal    = { textColor = Color.white },
            };

            _btnLabelStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize  = Mathf.RoundToInt(Mathf.Clamp(Screen.height * 0.015f, 11f, 15f)),
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleCenter,
                normal    = { textColor = new Color(0.76f, 0.88f, 1f, 1f) },
            };
        }

        // ── 회전 모드 전환 ─────────────────────────────────────────────────────

        /// <summary>
        /// 나침반 및 자이로 방향을 초기화합니다.
        /// 다음 유효한 나침반 값이 수신되면 즉시 스냅하여 현재 방향을 반영합니다.
        /// </summary>
        public void RecalibrateCompass()
        {
            ResetGyroCalibration();
            Debug.Log("[FirstPersonGPS] 방위 재보정 요청");
        }

        public RotationMode CurrentRotationMode => _rotationMode;

        public void CycleRotationMode()
        {
            _rotationMode = (RotationMode)(((int)_rotationMode + 1) % 3);

            if (_rotationMode == RotationMode.Locked)
            {
                _lockedRotation = transform.rotation;
            }
            else if (_rotationMode == RotationMode.Gyro)
            {
                ResetGyroCalibration();
            }
            else if (_rotationMode == RotationMode.Drag)
            {
                EnsureGyroCalibrationForCurrentRotation();
                Vector3 euler = transform.rotation.eulerAngles;
                _dragPitch = euler.x > 180f ? euler.x - 360f : euler.x;
                _dragYaw   = euler.y;
            }

            Debug.Log($"[FirstPersonGPS] 회전 모드: {_rotationMode}");
        }

        // ── 방향 업데이트 ──────────────────────────────────────────────────────

        private void UpdateRotation()
        {
            switch (_rotationMode)
            {
                case RotationMode.Gyro:
                    if (_gyroAvailable && AttitudeSensor.current != null)
                    {
                        _targetRotation = GetCalibratedGyroRotation(AttitudeSensor.current.attitude.ReadValue());
                        transform.rotation = Quaternion.Slerp(
                            transform.rotation, _targetRotation, Time.deltaTime * _rotationLerpSpeed);
                    }
                    break;

                case RotationMode.Locked:
                    transform.rotation = _lockedRotation;
                    break;

                case RotationMode.Drag:
                {
                    Vector2 delta = GetDragDelta();
                    _dragYaw   += delta.x * _dragSensitivity;
                    _dragPitch -= delta.y * _dragSensitivity;
                    _dragPitch  = Mathf.Clamp(_dragPitch, -80f, 80f);
                    transform.rotation = Quaternion.Euler(_dragPitch, _dragYaw, 0f);
                    break;
                }
            }
        }

        /// <summary>
        /// 드래그 입력 델타 반환.
        /// Pointer.current 사용 — Touchscreen(실기기) · Mouse(에디터/시뮬레이터) 통합 처리.
        /// Device Simulator에서 마우스 클릭이 터치로 자동 인식됩니다.
        /// </summary>
        private Vector2 GetDragDelta()
        {
            // Pointer.current: 마지막으로 활성화된 포인터 장치 (Touchscreen 또는 Mouse)
            var pointer = Pointer.current;
            if (pointer != null && pointer.press.isPressed)
            {
                float scale = pointer is Touchscreen ? 1.0f : 0.5f;
                return pointer.delta.ReadValue() * scale;
            }
            return Vector2.zero;
        }

        /// <summary>
        /// AttitudeSensor attitude(오른손 좌표계)를 Unity 카메라 회전(왼손 좌표계)으로 변환합니다.
        /// Portrait 모드 기준 — X축 90° 보정.
        /// </summary>
        private void ResetGyroCalibration()
        {
            _gyroYawCalibrated       = false;
            _compassCalibrationTimer = 0f;
            _lastScreenOrientation   = Screen.orientation;
            _compassBufferHead       = 0;
            _compassBufferFull       = false;
            _compassSinSum           = 0f;
            _compassCosSum           = 0f;
            _compassThrottleTimer    = 0f;
            _cachedCompassHeading    = -1f;
        }

        private Quaternion GetCalibratedGyroRotation(Quaternion attitude)
        {
            Quaternion gyroRotation = GyroToWorldRotation(attitude, Screen.orientation);

            if (_lastScreenOrientation != Screen.orientation)
            {
                _lastScreenOrientation = Screen.orientation;
                _gyroYawCalibrated = false;
            }

            if (_autoCalibrateGyroYaw && !_gyroYawCalibrated)
            {
                if (!_gyroAvailable && _compassEnabled && Input.compass.timestamp <= 0.0)
                {
                    _compassCalibrationTimer += Time.deltaTime;
                    if (_compassCalibrationTimer < CompassCalibrationTimeout)
                        return transform.rotation;
                }

                if (TryExtractHorizontalHeading(gyroRotation, out Quaternion heading))
                    CalibrateGyroYawToCurrentRotation(heading);
            }
            else if (_preferCompassHeading && _gyroYawCalibrated)
            {
                TryPeriodicCompassRecalibration(gyroRotation);
            }

            return Quaternion.Euler(0f, _gyroYawOffset + _headingYawCorrection, 0f) * gyroRotation;
        }

        private void EnsureGyroCalibrationForCurrentRotation()
        {
            if (!_autoCalibrateGyroYaw || _gyroYawCalibrated || !_gyroAvailable || AttitudeSensor.current == null)
                return;

            Quaternion attitude = AttitudeSensor.current.attitude.ReadValue();
            if (TryExtractHorizontalHeading(GyroToWorldRotation(attitude, Screen.orientation), out Quaternion gyroRotation))
                CalibrateGyroYawToCurrentRotation(gyroRotation);
        }

        private void CalibrateGyroYawToCurrentRotation(Quaternion gyroRotation)
        {
            float correctedGyroYaw = gyroRotation.eulerAngles.y + _headingYawCorrection;

            // Use compass direction as the calibration target. Calibrating to the current
            // camera yaw would anchor the gyro to the scene's default direction (north),
            // causing the camera to always start facing north until the compass overrides it.
            float targetYaw = transform.eulerAngles.y;
            if (_compassEnabled && Input.compass.timestamp > 0.0)
            {
                float heading = Input.compass.trueHeading;
                if (heading < 0f || float.IsNaN(heading))
                    heading = Input.compass.magneticHeading;
                if (heading >= 0f && !float.IsNaN(heading))
                {
                    float northYaw = 0f;
                    if (_gpsService != null && _gpsService.TryGetWorldNorthYaw(out float worldNorthYaw))
                        northYaw = worldNorthYaw;
                    targetYaw = northYaw + heading + _headingYawCorrection;
                }
            }
            else if (_gyroAvailable)
            {
                // Input.compass unavailable (old/new InputSystem conflict).
                // AttitudeSensor uses TYPE_ROTATION_VECTOR which already encodes absolute
                // geomagnetic heading — trust the sensor directly with no offset correction.
                targetYaw = correctedGyroYaw;
            }

            _gyroYawOffset = Mathf.DeltaAngle(correctedGyroYaw, targetYaw);
            _gyroYawCalibrated = true;

            Debug.Log($"[FirstPersonGPS] 자이로 보정 완료 — targetYaw={targetYaw:F1} gyroYaw={correctedGyroYaw:F1} offset={_gyroYawOffset:F1}");
        }

        // 60초마다 나침반으로 자이로 yaw offset 재보정
        private void TryPeriodicCompassRecalibration(Quaternion gyroRotation)
        {
            _compassThrottleTimer += Time.deltaTime;
            if (_compassThrottleTimer < CompassUpdateInterval) return;
            if (!_compassEnabled || Input.compass.timestamp <= 0.0) return;
            if (!IsPhoneUprightForHeading()) return;

            float raw = Input.compass.trueHeading;
            if (raw < 0f || float.IsNaN(raw)) raw = Input.compass.magneticHeading;
            if (raw < 0f || float.IsNaN(raw)) return;

            _cachedCompassHeading = SmoothedCompassHeading(raw);
            _compassThrottleTimer = 0f;

            if (!TryExtractHorizontalHeading(gyroRotation, out Quaternion heading)) return;

            float northYaw = 0f;
            if (_gpsService != null && _gpsService.TryGetWorldNorthYaw(out float worldNorthYaw))
                northYaw = worldNorthYaw;

            float targetYaw = northYaw + _cachedCompassHeading;
            float correctedGyroYaw = heading.eulerAngles.y + _headingYawCorrection;
            _gyroYawOffset = Mathf.DeltaAngle(correctedGyroYaw, targetYaw);

            Debug.Log($"[FirstPersonGPS] 나침반 주기 보정 — heading={_cachedCompassHeading:F1}° offset={_gyroYawOffset:F1}°");
        }

        private bool IsPhoneUprightForHeading()
        {
            if (!_gyroAvailable || AttitudeSensor.current == null)
            {
                // acceleration.z ≈ 0 when upright, ≈ ±1 when flat — reject compass when phone is too flat
                float maxFlatAxis = Mathf.Sqrt(1f - _uprightHeadingMinHorizontal * _uprightHeadingMinHorizontal);
                return Mathf.Abs(Input.acceleration.z) <= maxFlatAxis;
            }

            Quaternion attitude = AttitudeSensor.current.attitude.ReadValue();
            return HasStableHorizontalHeading(GyroToWorldRotation(attitude, Screen.orientation));
        }

        private bool TryExtractHorizontalHeading(Quaternion rotation, out Quaternion heading)
        {
            heading = Quaternion.identity;
            Vector3 forward = rotation * Vector3.forward;
            forward.y = 0f;
            if (forward.magnitude < _uprightHeadingMinHorizontal)
                return false;

            heading = Quaternion.LookRotation(forward.normalized, Vector3.up);
            return true;
        }

        private bool HasStableHorizontalHeading(Quaternion rotation)
        {
            Vector3 forward = rotation * Vector3.forward;
            forward.y = 0f;
            return forward.magnitude >= _uprightHeadingMinHorizontal;
        }

        private static Quaternion GyroToWorldRotation(Quaternion attitude, ScreenOrientation orientation)
        {
            Quaternion q = new Quaternion(attitude.x, attitude.y, -attitude.z, -attitude.w);
            Quaternion screenCompensation = orientation switch
            {
                ScreenOrientation.LandscapeLeft      => Quaternion.Euler(0f, 0f, -90f),
                ScreenOrientation.LandscapeRight     => Quaternion.Euler(0f, 0f, 90f),
                ScreenOrientation.PortraitUpsideDown => Quaternion.Euler(0f, 0f, 180f),
                _                                    => Quaternion.identity
            };

            return Quaternion.Euler(90f, 0f, 0f) * screenCompensation * q;
        }

        // ── 이벤트 핸들러 ──────────────────────────────────────────────────────

        private void OnGPSPositionUpdated(Vector3 rawUnityPosition)
        {
            if (!isActiveAndEnabled) return;

            ResolveDependencies();
            EnsureGlobeAnchor();
            if (_gpsService == null || _globeAnchor == null)
                return;

            // GPS 점프 필터: 이전 위치에서 갑자기 크게 벗어난 신호 거부
            var currentXZ = new Vector2(rawUnityPosition.x, rawUnityPosition.z);
            if (_hasLastAcceptedXZ && _maxGPSJumpMeters > 0f)
            {
                float jump = Vector2.Distance(currentXZ, _lastAcceptedXZ);
                if (jump > _maxGPSJumpMeters)
                {
                    Debug.LogWarning($"[FirstPersonGPS] GPS 점프 거부 ({jump:F1}m > {_maxGPSJumpMeters:F0}m)");
                    return;
                }
            }
            _lastAcceptedXZ    = currentXZ;
            _hasLastAcceptedXZ = true;
            float movedDist = Vector2.Distance(currentXZ, _lastGroundCheckXZ);

            // 최초이거나 2m 이상 이동했을 때만 지면 재감지 (GPS 고도 노이즈로 인한 상하 떨림 방지)
            bool shouldCheck = _cachedGroundY == float.MinValue || movedDist > GroundCheckMoveThreshold;

            if (shouldCheck && !_samplingHeight)
            {
                // Cesium 지형 높이 샘플링 시작 (비동기) — 실패 시 Raycast 폴백
                SampleAndUpdateGroundHeight(
                    _gpsService.CurrentLatitude,
                    _gpsService.CurrentLongitude,
                    rawUnityPosition);
            }

            // Y는 GPS 고도 대신 캐시된 지면값만 사용 → 흔들림 없음
            float targetY = _cachedGroundY != float.MinValue
                ? _cachedGroundY + _eyeHeight
                : rawUnityPosition.y + _eyeHeight;

            _targetPosition = new Vector3(rawUnityPosition.x, targetY, rawUnityPosition.z);

            if (!_hasInitialPosition)
            {
                _globeAnchor.transform.position = _targetPosition;
                _hasInitialPosition = true;
                Debug.Log($"[FirstPersonGPS] 초기 위치 설정: {_targetPosition}");
            }
        }

        // ── 내부 구현 ──────────────────────────────────────────────────────────

        private void RequestLocationPermissionThenStartGPS()
        {
#if UNITY_ANDROID
            if (Permission.HasUserAuthorizedPermission(Permission.FineLocation))
            {
                HandleLocationPermissionGranted();
                return;
            }

            var callbacks = new PermissionCallbacks();
            callbacks.PermissionGranted += _ => HandleLocationPermissionGranted();
            callbacks.PermissionDenied += _ => HandleLocationPermissionDenied();
            callbacks.PermissionDeniedAndDontAskAgain += _ => HandleLocationPermissionDenied();
            Permission.RequestUserPermission(Permission.FineLocation, callbacks);
#elif UNITY_IOS
            if (_iosPermissionCoroutine != null)
                StopCoroutine(_iosPermissionCoroutine);
            _iosPermissionCoroutine = StartCoroutine(RequestIOSLocationPermissionThenStartGPS());
#else
            HandleLocationPermissionGranted();
#endif
        }

#if UNITY_IOS
        private IEnumerator RequestIOSLocationPermissionThenStartGPS()
        {
            Input.location.Start();

            float timeout = 8f;
            while (Input.location.status == LocationServiceStatus.Initializing && timeout > 0f)
            {
                yield return new WaitForSeconds(0.5f);
                timeout -= 0.5f;
            }

            if (Input.location.status == LocationServiceStatus.Running)
            {
                Input.location.Stop();
                HandleLocationPermissionGranted();
            }
            else
            {
                Input.location.Stop();
                HandleLocationPermissionDenied();
            }

            _iosPermissionCoroutine = null;
        }
#endif

        private void HandleLocationPermissionDenied()
        {
            Debug.LogWarning("[FirstPersonGPS] 위치 권한 없음 — GPS 동기화 불가");
        }

        private void HandleLocationPermissionGranted()
        {
            if (!isActiveAndEnabled)
                return;

            StartGPSTracking();
        }

        private void StartGPSTracking()
        {
            ResolveDependencies();
            if (_gpsService == null)
            {
                Debug.LogError("[FirstPersonGPS] GPSLocationService가 연결되지 않았습니다.");
                return;
            }
            if (!_gpsSubscribed)
            {
                _gpsService.OnRawPositionUpdated -= OnGPSPositionUpdated;
                _gpsService.OnRawPositionUpdated += OnGPSPositionUpdated;
                _gpsSubscribed = true;
            }
            _gpsService.StartGPS();
            Debug.Log("[FirstPersonGPS] GPS 추적 시작");
        }

        private void ResolveDependencies()
        {
            if (_gpsService == null)
                _gpsService = FindAnyObjectByType<GPSLocationService>();
        }

        private void EnsureGlobeAnchor()
        {
            if (_globeAnchor != null) return;

            _globeAnchor = GetComponent<CesiumGlobeAnchor>();
            if (_globeAnchor == null)
                _globeAnchor = gameObject.AddComponent<CesiumGlobeAnchor>();
            _globeAnchor.detectTransformChanges = false;
        }

        /// <summary>
        /// Cesium SampleHeightMostDetailed로 실제 지형 표면 높이를 비동기 조회합니다.
        /// Cesium 타일이 아직 로드되지 않았거나 실패하면 Physics.Raycast 폴백을 사용합니다.
        /// </summary>
        private async void SampleAndUpdateGroundHeight(double lat, double lon, Vector3 rawUnityPosition)
        {
            if (!isActiveAndEnabled || _gpsService == null) return;

            int sampleVersion = ++_heightSampleVersion;
            _samplingHeight = true;
            try
            {
                bool success = false;

                if (_worldTerrain != null && _worldTerrain.isActiveAndEnabled)
                {
                    try
                    {
                        CesiumSampleHeightResult result = await _worldTerrain.SampleHeightMostDetailed(
                            new double3(lon, lat, 0.0));

                        if (this == null || !isActiveAndEnabled || sampleVersion != _heightSampleVersion || _gpsService == null)
                            return;

                        if (result.sampleSuccess != null && result.sampleSuccess.Length > 0 && result.sampleSuccess[0])
                        {
                            double sampledAlt = result.longitudeLatitudeHeightPositions[0].z;
                            // 타일 고도(ellipsoid 기준)를 Unity 월드 좌표로 변환
                            Vector3 groundUnity = _gpsService.ConvertToUnityPosition(lat, lon, sampledAlt);
                            _cachedGroundY = groundUnity.y;
                            _lastGroundCheckXZ = new Vector2(rawUnityPosition.x, rawUnityPosition.z);
                            // 비동기 완료 즉시 카메라 목표 Y 갱신
                            _targetPosition = new Vector3(_targetPosition.x, _cachedGroundY + _eyeHeight, _targetPosition.z);
                            success = true;
                            Debug.Log($"[FirstPersonGPS] Cesium 지면 높이 성공: 고도={sampledAlt:F1}m → Unity Y={_cachedGroundY:F1}");
                        }
                        else
                        {
                            string warn = result.warnings != null && result.warnings.Length > 0 ? result.warnings[0] : "없음";
                            Debug.LogWarning($"[FirstPersonGPS] Cesium 지면 샘플링 실패 (경고: {warn}) — Raycast 폴백");
                        }
                    }
                    catch (System.Exception e)
                    {
                        if (this == null || !isActiveAndEnabled) return;
                        Debug.LogWarning($"[FirstPersonGPS] Cesium 높이 샘플링 오류: {e.Message} — Raycast 폴백");
                    }
                }

                // Cesium 샘플링 실패 또는 _worldTerrain 미설정 시 Physics.Raycast 폴백
                if (!success)
                {
                    if (TryGetGroundHeightRaycast(rawUnityPosition, out float groundY))
                    {
                        _cachedGroundY = groundY;
                        _lastGroundCheckXZ = new Vector2(rawUnityPosition.x, rawUnityPosition.z);
                        _targetPosition = new Vector3(_targetPosition.x, _cachedGroundY + _eyeHeight, _targetPosition.z);
                        Debug.Log($"[FirstPersonGPS] Raycast 지면 높이: Unity Y={groundY:F1}");
                    }
                }
            }
            finally
            {
                if (this != null && sampleVersion == _heightSampleVersion)
                    _samplingHeight = false;
            }
        }

        private bool TryGetGroundHeightRaycast(Vector3 position, out float groundY)
        {
            float originHeight = Mathf.Max(_raycastOriginHeight, 500f);
            Vector3 rayOrigin = new Vector3(position.x, position.y + originHeight, position.z);
            float maxDist     = originHeight * 2f + Mathf.Abs(position.y) + 100f;

            if (Physics.Raycast(rayOrigin, Vector3.down, out RaycastHit hit, maxDist, _groundLayerMask))
            {
                // Cesium 대형 삼각형 오탐 필터링: GPS 고도 기준 200m 이상 아래는 거부
                float dropFromGPS = position.y - hit.point.y;
                if (dropFromGPS > 200f)
                {
                    Debug.LogWarning($"[FirstPersonGPS] Raycast 이상값 거부: Y={hit.point.y:F1} ({dropFromGPS:F0}m 아래)");
                    groundY = 0f;
                    return false;
                }
                groundY = hit.point.y;
                return true;
            }
            groundY = 0f;
            return false;
        }

        // ── 건물 충돌 처리 ─────────────────────────────────────────────────────

        /// <summary>
        /// 카메라가 건물 내부/겹침 상태인지 감지하고, 해당 시 인접 도로 위치로 XZ를 고정합니다.
        /// </summary>
        private void HandleBuildingCollision()
        {
            if (_buildingLayerMask == 0) return;

            bool inside = IsInsideBuilding(transform.position);

            if (inside && !_insideBuilding)
            {
                Vector3 roadXZ = FindNearestRoadXZ(transform.position);
                _buildingLockedXZ = new Vector2(roadXZ.x, roadXZ.z);
                _insideBuilding = true;
                Debug.Log($"[FirstPersonGPS] 건물 내부 감지 → 도로 XZ 고정: ({_buildingLockedXZ.x:F1}, {_buildingLockedXZ.y:F1})");
            }
            else if (!inside && _insideBuilding)
            {
                _insideBuilding = false;
                Debug.Log("[FirstPersonGPS] 건물 충돌 해제 → XZ 잠금 해제");
            }

            if (_insideBuilding)
                _targetPosition = new Vector3(_buildingLockedXZ.x, _targetPosition.y, _buildingLockedXZ.y);
        }

        /// <summary>
        /// 수평 4방향 레이 모두 건물에 막히면 내부로 판단합니다.
        /// </summary>
        private bool IsInsideBuilding(Vector3 pos)
        {
            int blocked = 0;
            if (Physics.Raycast(pos, Vector3.forward, _buildingRayLength, _buildingLayerMask)) blocked++;
            if (Physics.Raycast(pos, Vector3.back,    _buildingRayLength, _buildingLayerMask)) blocked++;
            if (Physics.Raycast(pos, Vector3.right,   _buildingRayLength, _buildingLayerMask)) blocked++;
            if (Physics.Raycast(pos, Vector3.left,    _buildingRayLength, _buildingLayerMask)) blocked++;
            return blocked >= 4;
        }

        /// <summary>
        /// 현재 위치에서 가장 가까운 도로(또는 건물 밖 개방 공간) XZ 좌표를 반환합니다.
        /// 도로 레이어가 설정된 경우 해당 레이어 우선, 없으면 건물 밖 개방 위치로 폴백합니다.
        /// </summary>
        private Vector3 FindNearestRoadXZ(Vector3 fromPos)
        {
            // 방사형 탐색: 2m 간격으로 반경을 넓혀가며 8→16→24 방향 검사
            for (float r = 2f; r <= _roadSearchMaxRadius; r += 2f)
            {
                int steps = Mathf.Max(8, Mathf.RoundToInt(r * Mathf.PI)); // 반경에 비례한 방향 수
                for (int i = 0; i < steps; i++)
                {
                    float angle = i * (Mathf.PI * 2f / steps);
                    float tx = fromPos.x + Mathf.Cos(angle) * r;
                    float tz = fromPos.z + Mathf.Sin(angle) * r;
                    Vector3 testPos = new Vector3(tx, fromPos.y, tz);

                    // 도로 레이어 지정 시: 해당 위치 수직 하방에 도로 콜라이더 존재 여부 확인
                    if (_roadLayerMask != 0)
                    {
                        Vector3 rayOrigin = new Vector3(tx, fromPos.y + 200f, tz);
                        if (Physics.Raycast(rayOrigin, Vector3.down, 400f, _roadLayerMask))
                            return testPos;
                    }
                    else
                    {
                        // 도로 레이어 미설정: 건물 내부가 아닌 가장 가까운 위치 사용
                        if (!IsInsideBuilding(testPos))
                            return testPos;
                    }
                }
            }

            Debug.LogWarning("[FirstPersonGPS] 인접 도로/개방 공간 탐색 실패 — 현재 위치 유지");
            return fromPos;
        }

        private static Texture2D MakeSolidTex(Color color)
        {
            var tex = new Texture2D(1, 1);
            tex.SetPixel(0, 0, color);
            tex.Apply();
            return tex;
        }

        // ── 나침반 원형 평균 스무싱 ────────────────────────────────────────────

        /// <summary>
        /// 나침반 heading을 링 버퍼에 쌓아 원형 평균을 반환합니다.
        /// 단순 산술 평균은 359°↔1° 경계에서 오류가 나므로 sin/cos 평균을 사용합니다.
        /// </summary>
        private float SmoothedCompassHeading(float rawHeading)
        {
            if (_compassBuffer == null || _compassBuffer.Length <= 1) return rawHeading;

            // 덮어쓸 이전 값 누적합에서 제거 (버퍼가 꽉 찬 경우만)
            if (_compassBufferFull)
            {
                float oldRad = _compassBuffer[_compassBufferHead] * Mathf.Deg2Rad;
                _compassSinSum -= Mathf.Sin(oldRad);
                _compassCosSum -= Mathf.Cos(oldRad);
            }

            float newRad = rawHeading * Mathf.Deg2Rad;
            _compassBuffer[_compassBufferHead] = rawHeading;
            _compassSinSum += Mathf.Sin(newRad);
            _compassCosSum += Mathf.Cos(newRad);

            _compassBufferHead = (_compassBufferHead + 1) % _compassBuffer.Length;
            if (_compassBufferHead == 0) _compassBufferFull = true;

            int count = _compassBufferFull ? _compassBuffer.Length : _compassBufferHead;
            float mean = Mathf.Atan2(_compassSinSum / count, _compassCosSum / count) * Mathf.Rad2Deg;
            return (mean + 360f) % 360f;
        }

    }
}
