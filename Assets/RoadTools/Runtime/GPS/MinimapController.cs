using CesiumForUnity;
using UnityEngine;

namespace Rugem.RoadTools
{
    /// <summary>
    /// 탑뷰 미니맵을 RenderTexture로 렌더링. 원형 마스크 + 드롭 쉐도우 적용.
    /// </summary>
    public class MinimapController : MonoBehaviour
    {
        [Header("추적 대상")]
        [SerializeField] private Transform _followTarget;

        [Header("미니맵 카메라")]
        [SerializeField] private float _cameraHeight    = 400f;
        [SerializeField] private float _orthographicSize = 80f;

        [Header("미니맵 UI")]
        [SerializeField] private int   _textureSize  = 256;
        [SerializeField, Range(0.1f, 0.4f)] private float _mapSizeRatio = 0.22f;
        [SerializeField] private Color _markerColor  = new Color(0.13f, 0.59f, 0.95f, 1f);

        internal const float MapSizeRatioConst = 0.22f;

        private Camera            _minimapCam;
        private RenderTexture     _rt;
        private Texture2D         _arrowTex;
        private Texture2D         _circleFrameTex;  // 원형 클리핑 마스크 (테두리 바깥 불투명)
        private Texture2D         _circleMaskTex;   // 원 내부만 흰색 (마커 위 적용용)
        private GUIStyle          _northStyle;
        private CesiumCameraManager _cameraManager;
        private bool              _registeredWithCameraManager;
        private int               _lastFrameTexSize; // 프레임 텍스처 재생성 감지용

        // ── 오버뷰 모드 ──────────────────────────────────────────────────────
        private bool  _overviewMode;
        private float _savedOrthoSize;

        public RenderTexture OverviewTexture   => _rt;
        public float         CurrentOrthoSize  => _minimapCam != null ? _minimapCam.orthographicSize : _orthographicSize;
        public Vector3       CurrentCamPosition => _minimapCam != null ? _minimapCam.transform.position : Vector3.zero;

        public void EnterOverviewMode(Vector3 playerWorldPos, Vector3 destWorldPos)
        {
            if (_minimapCam == null) return;
            if (!_overviewMode) _savedOrthoSize = _minimapCam.orthographicSize;
            float midX = (playerWorldPos.x + destWorldPos.x) * 0.5f;
            float midZ = (playerWorldPos.z + destWorldPos.z) * 0.5f;
            float dx   = Mathf.Abs(destWorldPos.x - playerWorldPos.x) * 0.5f;
            float dz   = Mathf.Abs(destWorldPos.z - playerWorldPos.z) * 0.5f;
            float size = Mathf.Max(dx, dz, 50f) * 1.4f;
            _minimapCam.transform.position = new Vector3(midX, _minimapCam.transform.position.y, midZ);
            _minimapCam.orthographicSize   = size;
            _overviewMode = true;
        }

        public void ExitOverviewMode()
        {
            if (_minimapCam == null || !_overviewMode) return;
            _minimapCam.orthographicSize = _savedOrthoSize;
            _overviewMode = false;
        }

        // ── 생명주기 ─────────────────────────────────────────────────────────

        private void Awake() => ResolveFollowTarget();

        private void Start()
        {
            ResolveFollowTarget();
            CreateMinimapCamera();
            _arrowTex = CreateArrowTexture(32, _markerColor);
            RebuildFrameTextures(_textureSize);
        }

        private void LateUpdate()
        {
            if (_overviewMode || _minimapCam == null || _followTarget == null) return;
            Vector3 p = _followTarget.position;
            _minimapCam.transform.position = new Vector3(p.x, p.y + _cameraHeight, p.z);
        }

        private void OnDestroy()
        {
            if (_minimapCam != null)
            {
                if (_cameraManager != null && _registeredWithCameraManager)
                    _cameraManager.additionalCameras.Remove(_minimapCam);
                Destroy(_minimapCam.gameObject);
            }
            if (_rt             != null) { _rt.Release(); Destroy(_rt); }
            if (_arrowTex       != null) Destroy(_arrowTex);
            if (_circleFrameTex != null) Destroy(_circleFrameTex);
            if (_circleMaskTex  != null) Destroy(_circleMaskTex);
        }

        private void ResolveFollowTarget()
        {
            if (_followTarget != null) return;
            var gps = FindAnyObjectByType<FirstPersonGPSController>();
            if (gps != null) _followTarget = gps.transform;
        }

        // ── 카메라 설정 ───────────────────────────────────────────────────────

        private void CreateMinimapCamera()
        {
            if (_minimapCam != null) return;
            var go = new GameObject("[MinimapCamera]");
            go.transform.SetParent(transform, false);
            go.transform.rotation = Quaternion.Euler(90f, 0f, 0f);

            _minimapCam = go.AddComponent<Camera>();
            _minimapCam.orthographic    = true;
            _minimapCam.orthographicSize = _orthographicSize;
            _minimapCam.clearFlags      = CameraClearFlags.SolidColor;
            _minimapCam.backgroundColor = new Color(0.15f, 0.18f, 0.24f);
            _minimapCam.nearClipPlane   = 1f;
            _minimapCam.farClipPlane    = _cameraHeight + 200f;
            _minimapCam.depth           = -2;

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
                Debug.Log("[Minimap] CesiumCameraManager에 미니맵 카메라 등록 완료");
            }
            else
            {
                Debug.LogWarning("[Minimap] CesiumCameraManager를 찾을 수 없음");
            }
        }

        // ── GUI ──────────────────────────────────────────────────────────────

        private void OnGUI()
        {
            if (_rt == null || _overviewMode) return;

            float mapSize = Screen.height * _mapSizeRatio;
            float margin  = Screen.width  * 0.03f;
            float x = Screen.width  - mapSize - margin;
            float y = margin;

            // 드롭 쉐도우
            GUI.color = new Color(0f, 0f, 0f, 0.18f);
            GUI.DrawTexture(new Rect(x + 2f, y + 5f, mapSize, mapSize), _circleMaskTex ?? Texture2D.whiteTexture);
            GUI.color = new Color(0f, 0f, 0f, 0.10f);
            GUI.DrawTexture(new Rect(x, y + 9f, mapSize, mapSize), _circleMaskTex ?? Texture2D.whiteTexture);
            GUI.color = Color.white;

            // 미니맵 렌더텍스처
            GUI.DrawTexture(new Rect(x, y, mapSize, mapSize), _rt, ScaleMode.ScaleToFit, false);

            // 원형 프레임 마스크 (바깥 모서리 덮기 → 원형 클리핑 효과)
            if (_circleFrameTex != null)
            {
                GUI.color = Color.white;
                GUI.DrawTexture(new Rect(x, y, mapSize, mapSize), _circleFrameTex);
            }

            // 플레이어 방향 화살표
            Matrix4x4 savedMatrix = GUI.matrix;
            if (_followTarget != null && _arrowTex != null)
            {
                float yaw     = GetHorizontalYaw(_followTarget);
                float cx      = x + mapSize * 0.5f;
                float cy      = y + mapSize * 0.5f;
                float arrowSz = mapSize * 0.14f;
                GUIUtility.RotateAroundPivot(yaw, new Vector2(cx, cy));
                GUI.color = _markerColor;
                GUI.DrawTexture(new Rect(cx - arrowSz * 0.5f, cy - arrowSz * 0.5f, arrowSz, arrowSz), _arrowTex);
                GUI.matrix = savedMatrix;
            }

            // 북 방향 라벨
            if (_northStyle == null)
            {
                _northStyle = new GUIStyle(GUI.skin.label)
                {
                    fontSize  = Mathf.RoundToInt(Screen.height * 0.020f),
                    fontStyle = FontStyle.Bold,
                    alignment = TextAnchor.UpperCenter,
                    normal    = { textColor = Color.white },
                };
            }
            GUI.color = Color.white;
            float labelSz = mapSize * 0.30f;
            GUI.Label(new Rect(x, y + mapSize * 0.04f, mapSize, labelSz), "N", _northStyle);
            GUI.matrix = savedMatrix;
        }

        // ── 텍스처 생성 ──────────────────────────────────────────────────────

        private static float GetHorizontalYaw(Transform target)
        {
            Vector3 forward = target.forward;
            forward.y = 0f;
            if (forward.sqrMagnitude < 0.0001f)
                return target.eulerAngles.y;

            return Quaternion.LookRotation(forward.normalized, Vector3.up).eulerAngles.y;
        }

        private void RebuildFrameTextures(int size)
        {
            if (_lastFrameTexSize == size && _circleFrameTex != null) return;
            _lastFrameTexSize = size;

            if (_circleFrameTex != null) Destroy(_circleFrameTex);
            if (_circleMaskTex  != null) Destroy(_circleMaskTex);

            // 배경색 (미니맵 카메라 배경 = 지도 영역 밖)
            Color bgColor = new Color(0.06f, 0.08f, 0.11f, 1f);
            _circleFrameTex = MakeCircleFrame(size, bgColor);
            _circleMaskTex  = MakeCircleMask(size, Color.white);
        }

        /// <summary>원 안: 투명 / 원 밖: frameColor → 미니맵 위에 덮으면 원형 클리핑</summary>
        private static Texture2D MakeCircleFrame(int size, Color frameColor)
        {
            var tex    = new Texture2D(size, size, TextureFormat.RGBA32, false);
            var pixels = new Color[size * size];
            float cx = size * 0.5f, cy = size * 0.5f;
            float r  = size * 0.5f - 1f;
            for (int py = 0; py < size; py++)
            for (int px = 0; px < size; px++)
            {
                float dx = px - cx + 0.5f, dy = py - cy + 0.5f;
                bool inside = dx * dx + dy * dy <= r * r;
                pixels[py * size + px] = inside ? Color.clear : frameColor;
            }
            tex.filterMode = FilterMode.Bilinear;
            tex.SetPixels(pixels);
            tex.Apply();
            return tex;
        }

        /// <summary>원 안: maskColor / 원 밖: 투명 → 쉐도우용</summary>
        private static Texture2D MakeCircleMask(int size, Color maskColor)
        {
            var tex    = new Texture2D(size, size, TextureFormat.RGBA32, false);
            var pixels = new Color[size * size];
            float cx = size * 0.5f, cy = size * 0.5f;
            float r  = size * 0.5f - 1f;
            for (int py = 0; py < size; py++)
            for (int px = 0; px < size; px++)
            {
                float dx = px - cx + 0.5f, dy = py - cy + 0.5f;
                pixels[py * size + px] = dx * dx + dy * dy <= r * r ? maskColor : Color.clear;
            }
            tex.filterMode = FilterMode.Bilinear;
            tex.SetPixels(pixels);
            tex.Apply();
            return tex;
        }

        private static Texture2D CreateArrowTexture(int size, Color color)
        {
            var tex    = new Texture2D(size, size, TextureFormat.RGBA32, false);
            var pixels = new Color[size * size];
            float cx = size * 0.5f, tipY = size * 0.05f, baseY = size * 0.90f, halfBase = size * 0.35f;
            for (int py = 0; py < size; py++)
            {
                float t  = Mathf.InverseLerp(tipY, baseY, py);
                float hw = halfBase * t;
                for (int px = 0; px < size; px++)
                {
                    bool inside = py >= tipY && py <= baseY && Mathf.Abs(px - cx) <= hw;
                    pixels[(size - 1 - py) * size + px] = inside ? color : Color.clear;
                }
            }
            tex.SetPixels(pixels);
            tex.Apply();
            return tex;
        }
    }
}
