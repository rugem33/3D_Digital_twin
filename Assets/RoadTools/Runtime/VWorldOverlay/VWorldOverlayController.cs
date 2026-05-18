using System.Collections;
using CesiumForUnity;
using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.SceneManagement;
#endif

namespace Rugem.RoadTools
{
    public enum VWorldLayerType
    {
        Satellite,
        Base,
        Hybrid
    }

    /// <summary>
    /// V-World WMTS 오버레이 컨트롤러
    ///
    /// ── 씬 설정 순서 ────────────────────────────────────────────────
    ///  1. Tileset GameObject 선택
    ///  2. Add Component → Cesium → Cesium URL Template Raster Overlay
    ///  3. 추가된 컴포넌트 체크박스를 OFF (비활성) 상태로 설정
    ///  4. 빈 GameObject에 이 컴포넌트 추가
    ///  5. Target Tileset 필드에 Tileset GameObject 연결
    ///  6. Height Sampler To Disable 에 FirstPersonGPSController 연결
    ///  7. Resources/vworld_api_key.txt에 V-World API 키 입력
    /// ────────────────────────────────────────────────────────────────
    /// </summary>
    [AddComponentMenu("RoadTools/V-World Overlay Controller")]
    public class VWorldOverlayController : MonoBehaviour
    {
        [Header("Cesium 연결")]
        [Tooltip("CesiumUrlTemplateRasterOverlay가 추가된 Tileset GameObject")]
        [SerializeField] private Cesium3DTileset _targetTileset;

        [Header("V-World 설정")]
        [Tooltip("비워두면 Resources/vworld_api_key.txt에서 자동 로드")]
        [SerializeField] private string _apiKey = "";
        [SerializeField] private VWorldLayerType _layerType = VWorldLayerType.Satellite;

        [Header("줌 레벨")]
        [SerializeField, Range(6, 19)] private int _minimumLevel = 6;
        [SerializeField, Range(6, 19)] private int _maximumLevel = 19;

        [Header("크래시 방지")]
        [Tooltip("오버레이 전환 중 일시 비활성화할 컴포넌트 (FirstPersonGPSController).")]
        [SerializeField] private MonoBehaviour _heightSamplerToDisable;

        private CesiumUrlTemplateRasterOverlay _overlay;

        public VWorldLayerType CurrentLayer   => _layerType;
        public bool            IsOverlayActive => _overlay != null && _overlay.enabled;

        IEnumerator Start()
        {
            if (_targetTileset == null)
            {
                Debug.LogError("[VWorldOverlay] Target Tileset이 연결되지 않았습니다.");
                yield break;
            }

            string key = VWorldApiKeyProvider.Resolve(_apiKey);
            if (string.IsNullOrEmpty(key))
            {
                Debug.LogError("[VWorldOverlay] V-World API 키가 없습니다.\n" +
                               "Resources/vworld_api_key.txt 에 키를 입력하거나\n" +
                               "Inspector의 Api Key 필드에 직접 입력하세요.");
                yield break;
            }

            // 씬에 배치된 컴포넌트 재사용, 없으면 생성
            _overlay = _targetTileset.gameObject.GetComponent<CesiumUrlTemplateRasterOverlay>();
            if (_overlay == null)
                _overlay = _targetTileset.gameObject.AddComponent<CesiumUrlTemplateRasterOverlay>();

            // 비활성 상태로 만들고 프로퍼티 설정
            _overlay.enabled = false;
            yield return null;

            _overlay.templateUrl  = BuildUrl(key, _layerType);
            _overlay.minimumLevel = Mathf.Max(_minimumLevel, 6);
            _overlay.maximumLevel = Mathf.Min(_maximumLevel, 19);

            yield return null;

            _overlay.enabled = true;

            Debug.Log($"[VWorldOverlay] 적용 완료 — URL: {_overlay.templateUrl}");
        }

        /// <summary>오버레이를 비활성화합니다.</summary>
        public void RemoveOverlay()
        {
            if (_overlay == null && _targetTileset != null)
                _overlay = _targetTileset.gameObject.GetComponent<CesiumUrlTemplateRasterOverlay>();

            if (_overlay != null)
                _overlay.enabled = false;

#if UNITY_EDITOR
            if (_overlay != null)
                EditorUtility.SetDirty(_overlay);
            EditorUtility.SetDirty(this);
            EditorSceneManager.MarkSceneDirty(gameObject.scene);
            EditorApplication.QueuePlayerLoopUpdate();
#endif
        }

        /// <summary>지정 레이어를 적용합니다.</summary>
        public void ApplyOverlay(VWorldLayerType layer)
        {
#if UNITY_EDITOR
            if (!Application.isPlaying)
            {
                ApplyOverlayImmediate(layer, true);
                return;
            }
#endif

            if (_overlay == null) return;
            _layerType = layer;

            string key = VWorldApiKeyProvider.Resolve(_apiKey);
            if (string.IsNullOrEmpty(key)) return;

            StartCoroutine(SwitchRoutine(key, layer));
        }

        /// <summary>레이어 전환.</summary>
        public void SwitchLayer(VWorldLayerType layer)
        {
#if UNITY_EDITOR
            if (!Application.isPlaying)
            {
                ApplyOverlayImmediate(layer, true);
                return;
            }
#endif

            if (_overlay == null) return;
            _layerType = layer;

            string key = VWorldApiKeyProvider.Resolve(_apiKey);
            if (string.IsNullOrEmpty(key)) return;

            StartCoroutine(SwitchRoutine(key, layer));
        }

        private IEnumerator SwitchRoutine(string key, VWorldLayerType layer)
        {
            bool samplerWasEnabled = false;
            if (_heightSamplerToDisable != null)
            {
                samplerWasEnabled = _heightSamplerToDisable.enabled;
                _heightSamplerToDisable.enabled = false;
            }

            _overlay.enabled = false;
            yield return null;

            _overlay.templateUrl = BuildUrl(key, layer);
            yield return null;

            _overlay.enabled = true;

            yield return new WaitForSeconds(1.5f);

            if (_heightSamplerToDisable != null)
                _heightSamplerToDisable.enabled = samplerWasEnabled;

            Debug.Log($"[VWorldOverlay] {layer} 전환 완료");
        }

        /// <summary>표시/숨김 전환.</summary>
        public void SetVisible(bool visible)
        {
            if (_overlay == null && _targetTileset != null)
                _overlay = _targetTileset.gameObject.GetComponent<CesiumUrlTemplateRasterOverlay>();

            if (_overlay != null)
                _overlay.enabled = visible;

#if UNITY_EDITOR
            if (_overlay != null)
                EditorUtility.SetDirty(_overlay);
            EditorUtility.SetDirty(this);
            EditorSceneManager.MarkSceneDirty(gameObject.scene);
            EditorApplication.QueuePlayerLoopUpdate();
#endif
        }

        public void ApplyOverlayImmediate(VWorldLayerType layer, bool visible)
        {
            if (_targetTileset == null)
            {
                Debug.LogError("[VWorldOverlay] Target Tileset이 연결되지 않았습니다.");
                return;
            }

            string key = VWorldApiKeyProvider.Resolve(_apiKey);
            if (string.IsNullOrEmpty(key))
            {
                Debug.LogError("[VWorldOverlay] V-World API 키가 없습니다.\n" +
                               "Resources/vworld_api_key.txt 에 키를 입력하거나\n" +
                               "Inspector의 Api Key 필드에 직접 입력하세요.");
                return;
            }

            _overlay = _targetTileset.gameObject.GetComponent<CesiumUrlTemplateRasterOverlay>();
            if (_overlay == null)
                _overlay = _targetTileset.gameObject.AddComponent<CesiumUrlTemplateRasterOverlay>();

            _layerType = layer;
            _overlay.enabled = false;
            _overlay.templateUrl  = BuildUrl(key, layer);
            _overlay.minimumLevel = Mathf.Max(_minimumLevel, 6);
            _overlay.maximumLevel = Mathf.Min(_maximumLevel, 19);
            _overlay.enabled = visible;

#if UNITY_EDITOR
            EditorUtility.SetDirty(_overlay);
            EditorUtility.SetDirty(this);
            EditorSceneManager.MarkSceneDirty(gameObject.scene);
            EditorApplication.QueuePlayerLoopUpdate();
#endif

            Debug.Log($"[VWorldOverlay] 에디터 적용 완료 — URL: {_overlay.templateUrl}");
        }

        private string BuildUrl(string key, VWorldLayerType layer) =>
            layer switch
            {
                VWorldLayerType.Satellite =>
                    $"https://api.vworld.kr/req/wmts/1.0.0/{key}/Satellite/{{z}}/{{reverseY}}/{{x}}.jpeg",
                VWorldLayerType.Base =>
                    $"https://api.vworld.kr/req/wmts/1.0.0/{key}/Base/{{z}}/{{reverseY}}/{{x}}.png",
                VWorldLayerType.Hybrid =>
                    $"https://api.vworld.kr/req/wmts/1.0.0/{key}/Hybrid/{{z}}/{{reverseY}}/{{x}}.png",
                _ =>
                    $"https://api.vworld.kr/req/wmts/1.0.0/{key}/Satellite/{{z}}/{{reverseY}}/{{x}}.jpeg",
            };
    }
}
