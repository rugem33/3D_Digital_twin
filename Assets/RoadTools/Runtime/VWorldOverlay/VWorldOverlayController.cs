using System.Collections;
using CesiumForUnity;
using UnityEngine;

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
        [Tooltip("오버레이 전환 중 일시 비활성화할 컴포넌트 (FirstPersonGPSController).\n" +
                 "Cesium 네이티브 재구성 도중 SampleHeightMostDetailed가 호출되면\n" +
                 "Cesium3DTileset.Update()에서 abort()가 발생하므로 반드시 연결할 것.")]
        [SerializeField] private MonoBehaviour _heightSamplerToDisable;

        private CesiumUrlTemplateRasterOverlay _overlay;
        private bool _registered;

        public VWorldLayerType CurrentLayer   => _layerType;
        public bool            IsOverlayActive => _registered && _overlay != null && _overlay.enabled;

        IEnumerator Start()
        {
            if (_targetTileset == null)
            {
                Debug.LogError("[VWorldOverlay] Target Tileset이 연결되지 않았습니다.");
                yield break;
            }

            if (_heightSamplerToDisable == null)
                Debug.LogWarning("[VWorldOverlay] Height Sampler To Disable이 연결되지 않았습니다.\n" +
                                 "FirstPersonGPSController를 연결하지 않으면 오버레이 전환 중 크래시가 발생할 수 있습니다.");

            // 씬에 미리 배치된 컴포넌트가 있으면 즉시 비활성화 (잘못된 URL로 요청 방지)
            var existing = _targetTileset.gameObject.GetComponent<CesiumUrlTemplateRasterOverlay>();
            if (existing != null)
            {
                existing.enabled = false;
                yield return null;
                Destroy(existing);
                yield return new WaitForSeconds(0.3f);
            }

            // 네이티브 Tileset 초기화 완료 대기
            yield return null;
            yield return null;

            yield return StartCoroutine(ApplyOverlayRoutine(_layerType));
        }

        /// <summary>지정 레이어를 적용합니다.</summary>
        public void ApplyOverlay(VWorldLayerType layer)
        {
            if (_targetTileset == null) return;
            StartCoroutine(ApplyOverlayRoutine(layer));
        }

        private IEnumerator ApplyOverlayRoutine(VWorldLayerType layer)
        {
            string key = VWorldApiKeyProvider.Resolve(_apiKey);
            if (string.IsNullOrEmpty(key))
            {
                Debug.LogError("[VWorldOverlay] V-World API 키가 없습니다.\n" +
                               "Resources/vworld_api_key.txt 에 키를 입력하거나\n" +
                               "Inspector의 Api Key 필드에 직접 입력하세요.");
                yield break;
            }

            // GPS 높이 샘플링 중단
            bool samplerWasEnabled = false;
            if (_heightSamplerToDisable != null)
            {
                samplerWasEnabled = _heightSamplerToDisable.enabled;
                _heightSamplerToDisable.enabled = false;
            }

            yield return null;

            // 기존 오버레이 파괴
            if (_overlay != null)
            {
                _overlay.enabled = false;
                yield return null;
                Destroy(_overlay);
                _overlay    = null;
                _registered = false;
                yield return new WaitForSeconds(0.5f);
                yield return null;
            }

            // 새 오버레이 생성 (비활성 상태로 — 프로퍼티 설정 후 활성화)
            _overlay = _targetTileset.gameObject.AddComponent<CesiumUrlTemplateRasterOverlay>();
            _overlay.enabled = false;
            yield return null;

            // 공개 프로퍼티로 설정 (Refresh()는 disabled 상태라 no-op)
            _overlay.templateUrl   = BuildUrl(key, layer);
            _overlay.minimumLevel  = Mathf.Max(_minimumLevel, 6);
            _overlay.maximumLevel  = Mathf.Min(_maximumLevel, 19);
            _overlay.materialKey   = "overlay1";

            yield return null;

            // OnEnable → AddToTileset
            _overlay.enabled = true;
            _registered      = true;
            _layerType       = layer;

            Debug.Log($"[VWorldOverlay] {layer} 등록 완료 — URL: {_overlay.templateUrl}");

            yield return new WaitForSeconds(1.5f);

            if (_heightSamplerToDisable != null)
                _heightSamplerToDisable.enabled = samplerWasEnabled;

            Debug.Log($"[VWorldOverlay] {layer} 적용 완료");
        }

        /// <summary>오버레이를 비활성화합니다.</summary>
        public void RemoveOverlay()
        {
            if (_overlay == null || !_registered) return;
            _overlay.enabled = false;
            _registered      = false;
        }

        /// <summary>표시/숨김 전환.</summary>
        public void SetVisible(bool visible)
        {
            if (_overlay == null) return;
            if (visible  && !_registered) ApplyOverlay(_layerType);
            if (!visible && _registered)  RemoveOverlay();
        }

        /// <summary>레이어 전환.</summary>
        public void SwitchLayer(VWorldLayerType layer)
        {
            if (layer == _layerType && IsOverlayActive) return;
            ApplyOverlay(layer);
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
