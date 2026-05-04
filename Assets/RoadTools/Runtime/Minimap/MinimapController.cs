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
        [SerializeField] private GPSLocationService _gpsService;

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
        private bool _playerArrowCompassInitialized;

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
            _playerArrowCompassInitialized = false;
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
            ResolveGPSService();
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
            if (_followTarget != null) return;

            var gps = FindAnyObjectByType<FirstPersonGPSController>();
            if (gps != null)
                _followTarget = gps.transform;
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

        private void ResolveGPSService()
        {
            if (_gpsService == null)
                _gpsService = FindAnyObjectByType<GPSLocationService>();
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
            // Always prefer the compass so the arrow reflects the phone's real-world
            // facing direction from the very first frame and through all camera modes
            // (Gyro / Locked / Drag). The minimap is north-up (camera locked to
            // Euler(90,0,0)), so compass heading maps directly to arrow rotation.
            if (_initializePlayerArrowFromCompass && TryGetCompassHeading(out float compassHeading))
            {
                if (!_playerArrowCompassInitialized)
                {
                    _playerArrowCompassInitialized = true;
                    Debug.Log($"[Minimap] Player arrow compass active: {GetCardinalDirection(compassHeading)} ({compassHeading:F0}°)");
                }
                TryGetWorldNorthYaw(out float northYaw);
                return northYaw + compassHeading;
            }

            // Fallback when compass is unavailable (editor / no hardware sensor).
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

        private bool TryGetWorldNorthYaw(out float yaw)
        {
            yaw = 0f;
            ResolveGPSService();
            if (_gpsService == null)
                return false;

            double lat = _gpsService.CurrentLatitude;
            double lon = _gpsService.CurrentLongitude;
            if (System.Math.Abs(lat) < 0.000001 && System.Math.Abs(lon) < 0.000001)
                return false;

            Vector3 here = _gpsService.ConvertToUnityPosition(lat, lon, _gpsService.CurrentAltitude);
            Vector3 north = _gpsService.ConvertToUnityPosition(lat + 0.00001, lon, _gpsService.CurrentAltitude);
            Vector3 northFlat = north - here;
            northFlat.y = 0f;
            if (northFlat.sqrMagnitude < 0.0001f)
                return false;

            yaw = Quaternion.LookRotation(northFlat.normalized, Vector3.up).eulerAngles.y;
            return true;
        }

        private static string GetCardinalDirection(float heading)
        {
            float normalized = Mathf.Repeat(heading, 360f);
            if (normalized >= 315f || normalized < 45f) return "N";
            if (normalized < 135f) return "E";
            if (normalized < 225f) return "S";
            return "W";
        }
    }
}
