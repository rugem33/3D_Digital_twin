using CesiumForUnity;
using UnityEngine;
using UnityEngine.UI;

namespace Rugem.RoadTools
{
    /// <summary>
    /// Renders a top-down minimap camera into a RenderTexture and binds it to Canvas UI.
    /// </summary>
    public class MinimapController : MonoBehaviour
    {
        [Header("Follow Target")]
        [SerializeField] private Transform _followTarget;
        [SerializeField] private Transform _directionTarget;

        [Header("Minimap Camera")]
        [SerializeField] private float _cameraHeight = 400f;
        [SerializeField] private float _orthographicSize = 80f;

        [Header("Minimap Texture")]
        [SerializeField] private int _textureSize = 256;
        [SerializeField] private Color _markerColor = new Color(0.13f, 0.59f, 0.95f, 1f);

        [Header("Canvas UI")]
        [SerializeField] private RawImage _minimapRawImage;
        [SerializeField] private RectTransform _playerArrow;
        [SerializeField] private float _playerArrowYawOffset = 90f;
        [SerializeField] private bool _initializePlayerArrowFromCompass = true;
        [SerializeField] private GameObject _minimapRoot;

        internal const float MapSizeRatioConst = 0.22f;

        private Camera _minimapCam;
        private RenderTexture _rt;
        private CesiumCameraManager _cameraManager;
        private bool _registeredWithCameraManager;

        private bool _overviewMode;
        private float _savedOrthoSize;
        private float _diagnosticTimer;

        public RenderTexture OverviewTexture => _rt;
        public float CurrentOrthoSize => _minimapCam != null ? _minimapCam.orthographicSize : _orthographicSize;
        public Vector3 CurrentCamPosition => _minimapCam != null ? _minimapCam.transform.position : Vector3.zero;

        public void EnterOverviewMode(Vector3 playerWorldPos, Vector3 destWorldPos)
        {
            if (_minimapCam == null) return;

            if (!_overviewMode)
                _savedOrthoSize = _minimapCam.orthographicSize;

            float midX = (playerWorldPos.x + destWorldPos.x) * 0.5f;
            float midZ = (playerWorldPos.z + destWorldPos.z) * 0.5f;
            float dx = Mathf.Abs(destWorldPos.x - playerWorldPos.x) * 0.5f;
            float dz = Mathf.Abs(destWorldPos.z - playerWorldPos.z) * 0.5f;
            float size = Mathf.Max(dx, dz, 50f) * 1.4f;

            _minimapCam.transform.position = new Vector3(midX, _minimapCam.transform.position.y, midZ);
            _minimapCam.orthographicSize = size;
            _overviewMode = true;
            UpdateCanvasUI();
        }

        public void ExitOverviewMode()
        {
            if (_minimapCam == null || !_overviewMode) return;

            _minimapCam.orthographicSize = _savedOrthoSize;
            _overviewMode = false;
            UpdateCanvasUI();
        }

        public void RecalibratePlayerArrow()
        {
        }

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

            if (!_overviewMode)
            {
                Vector3 p = _followTarget.position;
                _minimapCam.transform.position = new Vector3(p.x, p.y + _cameraHeight, p.z);
            }

            // Safety: keep the camera looking straight down every frame in case
            // something else in the scene hierarchy modifies its rotation.
            _minimapCam.transform.rotation = Quaternion.Euler(90f, 0f, 0f);

            UpdateCanvasUI();
        }

        private void OnDestroy()
        {
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

        private void ResolveFollowTarget()
        {
            if (_followTarget != null)
                return;

            if (Camera.main != null)
            {
                _followTarget = Camera.main.transform;
            }
        }

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

        private void EnableCompassForPlayerArrow()
        {
            if (_initializePlayerArrowFromCompass)
                Input.compass.enabled = true;
        }

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

        private void UpdateCanvasUI()
        {
            if (_minimapRoot != null)
                _minimapRoot.SetActive(!_overviewMode);

            if (_playerArrow == null || _followTarget == null) return;

            float yaw = GetDirectionYaw();
            _playerArrow.localEulerAngles = new Vector3(0f, 0f, -yaw + _playerArrowYawOffset);
        }

        private void CreateMinimapCamera()
        {
            if (_minimapCam != null) return;

            var go = new GameObject("[MinimapCamera]");
            // No parent: avoids inheriting the player camera's yaw/pitch rotation,
            // which would cause the rendered map to spin as the player turns.
            go.transform.rotation = Quaternion.Euler(90f, 0f, 0f);

            _minimapCam = go.AddComponent<Camera>();
            _minimapCam.orthographic = true;
            _minimapCam.orthographicSize = _orthographicSize;
            _minimapCam.clearFlags = CameraClearFlags.SolidColor;
            _minimapCam.backgroundColor = new Color(0.15f, 0.18f, 0.24f);
            _minimapCam.nearClipPlane = 1f;
            _minimapCam.farClipPlane = _cameraHeight + 200f;
            _minimapCam.depth = -2;

            int uiLayer = LayerMask.NameToLayer("UI");
            _minimapCam.cullingMask = uiLayer >= 0 ? ~(1 << uiLayer) : ~0;

            _rt = new RenderTexture(_textureSize, _textureSize, 16, RenderTextureFormat.Default);
            _rt.Create();
            _minimapCam.targetTexture = _rt;

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

        private static float GetHorizontalYaw(Transform target)
        {
            Vector3 forward = target.forward;
            forward.y = 0f;
            if (forward.sqrMagnitude < 0.0001f)
                return target.eulerAngles.y;

            return Quaternion.LookRotation(forward.normalized, Vector3.up).eulerAngles.y;
        }

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

            // Camera.main의 UpdateRotation()이 이미 compass+gyro 융합(northYaw 보정 포함)을
            // 수행한 뒤 LateUpdate가 실행되므로, 카메라 yaw를 직접 읽는 것이
            // northYaw+compassHeading과 동등하면서 USB 자기 간섭에 영향받지 않는다.
            return GetCameraYaw();
        }

        private float GetCameraYaw()
        {
            if (Camera.main != null)
                return GetHorizontalYaw(Camera.main.transform);

            Transform t = _directionTarget != null ? _directionTarget : _followTarget;
            return GetHorizontalYaw(t);
        }

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
