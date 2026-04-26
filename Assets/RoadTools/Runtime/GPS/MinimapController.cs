using CesiumForUnity;
using UnityEngine;

namespace Rugem.RoadTools
{
    /// <summary>
    /// 탑뷰 미니맵을 RenderTexture로 렌더링하여 화면 우측 상단에 표시합니다.
    /// 직교 카메라가 플레이어 위 일정 높이에서 따라다니며 지형·건물을 렌더링합니다.
    /// </summary>
    public class MinimapController : MonoBehaviour
    {
        [Header("추적 대상")]
        [Tooltip("미니맵이 따라갈 Transform. 비어있으면 FirstPersonGPSController를 자동 탐색합니다.")]
        [SerializeField] private Transform _followTarget;

        [Header("미니맵 카메라")]
        [Tooltip("플레이어 위 카메라 높이 오프셋 (미터)")]
        [SerializeField] private float _cameraHeight = 400f;
        [Tooltip("직교 카메라 크기 — 클수록 넓은 범위 표시 (미터 단위 반경)")]
        [SerializeField] private float _orthographicSize = 80f;

        [Header("미니맵 UI")]
        [Tooltip("RenderTexture 해상도 (높을수록 선명, 성능 비용 증가)")]
        [SerializeField] private int _textureSize = 256;
        [Tooltip("화면 높이 대비 미니맵 크기 비율")]
        [SerializeField, Range(0.1f, 0.4f)] private float _mapSizeRatio = 0.22f;
        [Tooltip("플레이어 방향 마커 색상")]
        [SerializeField] private Color _markerColor = new Color(1f, 0.25f, 0.25f, 1f);
        [Tooltip("미니맵 테두리 색상")]
        [SerializeField] private Color _borderColor = new Color(0f, 0f, 0f, 0.8f);

        // FirstPersonGPSController의 OnGUI가 버튼 Y 위치 계산에 사용
        internal const float MapSizeRatioConst = 0.22f;

        private Camera _minimapCam;
        private RenderTexture _rt;
        private Texture2D _arrowTex;
        private GUIStyle _northStyle;
        private CesiumCameraManager _cameraManager;

        // ── 생명주기 ──────────────────────────────────────────────────────────

        private void Awake()
        {
            if (_followTarget == null)
            {
                var gps = FindAnyObjectByType<FirstPersonGPSController>();
                _followTarget = gps != null ? gps.transform : transform;
            }
        }

        private void Start()
        {
            CreateMinimapCamera();
            _arrowTex = CreateArrowTexture(32, _markerColor);
        }

        private void LateUpdate()
        {
            if (_minimapCam == null || _followTarget == null) return;
            Vector3 p = _followTarget.position;
            _minimapCam.transform.position = new Vector3(p.x, p.y + _cameraHeight, p.z);
        }

        private void OnDestroy()
        {
            if (_minimapCam != null)
            {
                if (_cameraManager != null)
                    _cameraManager.additionalCameras.Remove(_minimapCam);
                Destroy(_minimapCam.gameObject);
            }
            if (_rt != null) { _rt.Release(); Destroy(_rt); }
            if (_arrowTex != null) Destroy(_arrowTex);
        }

        // ── 카메라 설정 ────────────────────────────────────────────────────────

        private void CreateMinimapCamera()
        {
            var go = new GameObject("[MinimapCamera]");
            go.transform.rotation = Quaternion.Euler(90f, 0f, 0f); // 정면 하방

            _minimapCam = go.AddComponent<Camera>();
            _minimapCam.orthographic = true;
            _minimapCam.orthographicSize = _orthographicSize;
            _minimapCam.clearFlags = CameraClearFlags.SolidColor;
            _minimapCam.backgroundColor = new Color(0.08f, 0.10f, 0.14f);
            _minimapCam.nearClipPlane = 1f;
            _minimapCam.farClipPlane = _cameraHeight + 200f;
            _minimapCam.depth = -2; // 메인 카메라보다 먼저 렌더

            // UI 레이어 제외
            int uiLayer = LayerMask.NameToLayer("UI");
            _minimapCam.cullingMask = uiLayer >= 0 ? ~(1 << uiLayer) : ~0;

            _rt = new RenderTexture(_textureSize, _textureSize, 16, RenderTextureFormat.Default);
            _rt.Create();
            _minimapCam.targetTexture = _rt;

            // Cesium CameraManager에 등록 — 미니맵 frustum 기준으로도 타일 스트리밍
            _cameraManager = CesiumCameraManager.GetOrCreate(gameObject);
            if (_cameraManager != null)
            {
                _cameraManager.additionalCameras.Add(_minimapCam);
                Debug.Log("[Minimap] CesiumCameraManager에 미니맵 카메라 등록 완료");
            }
            else
            {
                Debug.LogWarning("[Minimap] CesiumCameraManager를 찾을 수 없음 — 미니맵 타일 스트리밍 제한될 수 있음");
            }
        }

        // ── GUI 렌더링 ─────────────────────────────────────────────────────────

        private void OnGUI()
        {
            if (_rt == null) return;

            float mapSize = Screen.height * _mapSizeRatio;
            float margin  = Screen.width  * 0.03f;
            float x = Screen.width  - mapSize - margin;
            float y = margin;

            // 테두리
            GUI.color = _borderColor;
            GUI.DrawTexture(new Rect(x - 3, y - 3, mapSize + 6, mapSize + 6), Texture2D.whiteTexture);

            // 미니맵 텍스처
            GUI.color = Color.white;
            GUI.DrawTexture(new Rect(x, y, mapSize, mapSize), _rt, ScaleMode.ScaleToFit, false);

            // 플레이어 방향 화살표 (카메라 yaw 기준 회전)
            if (_followTarget != null && _arrowTex != null)
            {
                float yaw      = _followTarget.eulerAngles.y;
                float cx       = x + mapSize * 0.5f;
                float cy       = y + mapSize * 0.5f;
                float arrowSz  = mapSize * 0.14f;

                Matrix4x4 saved = GUI.matrix;
                GUIUtility.RotateAroundPivot(yaw, new Vector2(cx, cy));
                GUI.color = _markerColor;
                GUI.DrawTexture(new Rect(cx - arrowSz * 0.5f, cy - arrowSz * 0.5f, arrowSz, arrowSz), _arrowTex);
                GUI.matrix = saved;
            }

            // 북 방향 라벨
            if (_northStyle == null)
            {
                _northStyle = new GUIStyle(GUI.skin.label)
                {
                    fontSize  = Mathf.RoundToInt(Screen.height * 0.022f),
                    fontStyle = FontStyle.Bold,
                    alignment = TextAnchor.UpperCenter,
                    normal    = { textColor = Color.white }
                };
            }
            GUI.color = Color.white;
            GUI.Label(new Rect(x, y + 2f, mapSize, mapSize * 0.25f), "N", _northStyle);
        }

        // ── 텍스처 생성 ────────────────────────────────────────────────────────

        /// <summary>위를 향하는 삼각형 화살표 Texture2D를 런타임에 생성합니다.</summary>
        private static Texture2D CreateArrowTexture(int size, Color color)
        {
            var tex    = new Texture2D(size, size, TextureFormat.RGBA32, false);
            var pixels = new Color[size * size];

            float cx       = size * 0.5f;
            float tipY     = size * 0.05f;
            float baseY    = size * 0.90f;
            float halfBase = size * 0.35f;

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
