using System.Collections;
using System.Reflection;
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
    /// ── 크래시 방지 설계 ────────────────────────────────────────────
    ///  1. Reflection으로 backing field를 직접 써서 Refresh() → RemoveFromTileset() 우회
    ///  2. materialKey = "overlay1" 고정 (기본 "overlay0"은 Bing Maps와 충돌)
    ///  3. 오버레이 등록 동안 HeightSamplerToDisable 컴포넌트를 일시 비활성화
    ///     → SampleHeightMostDetailed 콜백이 Cesium 네이티브 재구성과 겹치면
    ///       Cesium3DTileset.Update()에서 abort() 발생하는 것을 방지
    /// ────────────────────────────────────────────────────────────────
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

        private static readonly FieldInfo FieldUrl =
            typeof(CesiumUrlTemplateRasterOverlay)
                .GetField("_templateUrl", BindingFlags.NonPublic | BindingFlags.Instance);
        private static readonly FieldInfo FieldMin =
            typeof(CesiumUrlTemplateRasterOverlay)
                .GetField("_minimumLevel", BindingFlags.NonPublic | BindingFlags.Instance);
        private static readonly FieldInfo FieldMax =
            typeof(CesiumUrlTemplateRasterOverlay)
                .GetField("_maximumLevel", BindingFlags.NonPublic | BindingFlags.Instance);
        private static readonly FieldInfo FieldMaterialKey =
            typeof(CesiumRasterOverlay)
                .GetField("_materialKey", BindingFlags.NonPublic | BindingFlags.Instance);

        IEnumerator Start()
        {
            if (_targetTileset == null)
            {
                Debug.LogError("[VWorldOverlay] Target Tileset이 연결되지 않았습니다.");
                yield break;
            }

            _overlay = _targetTileset.gameObject.GetComponent<CesiumUrlTemplateRasterOverlay>();
            if (_overlay == null)
            {
                Debug.LogError(
                    "[VWorldOverlay] Tileset GameObject에 CesiumUrlTemplateRasterOverlay가 없습니다.\n" +
                    "  Tileset 선택 → Add Component → Cesium URL Template Raster Overlay\n" +
                    "  추가된 컴포넌트의 체크박스를 OFF 상태로 설정한 뒤 다시 Play");
                yield break;
            }

            if (FieldUrl == null)
            {
                Debug.LogError(
                    "[VWorldOverlay] Cesium 내부 필드(_templateUrl)를 찾지 못했습니다.\n" +
                    "Cesium for Unity 버전이 변경됐을 수 있습니다.");
                yield break;
            }

            if (_heightSamplerToDisable == null)
                Debug.LogWarning("[VWorldOverlay] Height Sampler To Disable이 연결되지 않았습니다.\n" +
                                 "FirstPersonGPSController를 연결하지 않으면 오버레이 전환 중 크래시가 발생할 수 있습니다.");

            // 네이티브 Tileset 초기화 완료 대기
            yield return null;
            yield return null;

            if (_overlay.enabled)
                _overlay.enabled = false;
            _registered = false;

            yield return StartCoroutine(ApplyOverlayRoutine(_layerType));
        }

        /// <summary>지정 레이어를 적용합니다. 내부적으로 코루틴을 사용합니다.</summary>
        public void ApplyOverlay(VWorldLayerType layer)
        {
            if (_overlay == null || FieldUrl == null) return;
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

            // GPS 높이 샘플링 중단 — SampleHeightMostDetailed와 Cesium 재구성 충돌 방지
            bool samplerWasEnabled = false;
            if (_heightSamplerToDisable != null)
            {
                samplerWasEnabled = _heightSamplerToDisable.enabled;
                _heightSamplerToDisable.enabled = false;
            }

            yield return null; // 중단 반영 대기

            // 기존 오버레이 컴포넌트 파괴 — enabled 토글 대신 완전 재생성으로 abort 방지
            if (_overlay != null)
            {
                _overlay.enabled = false;
                yield return null;
                Destroy(_overlay);
                _overlay     = null;
                _registered  = false;
                // 네이티브 RemoveFromTileset + 타일셋 안정화 대기
                yield return new WaitForSeconds(0.5f);
                yield return null;
                yield return null;
            }

            // 새 오버레이 컴포넌트 생성 (비활성 상태로)
            _overlay = _targetTileset.gameObject.AddComponent<CesiumUrlTemplateRasterOverlay>();
            _overlay.enabled = false;
            yield return null;

            // 필드 설정
            FieldUrl.SetValue(_overlay, BuildUrl(key, layer));
            FieldMin?.SetValue(_overlay, Mathf.Max(_minimumLevel, 6));
            FieldMax?.SetValue(_overlay, Mathf.Min(_maximumLevel, 19));
            FieldMaterialKey?.SetValue(_overlay, "overlay1");

            yield return null;

            // OnEnable → AddToTileset
            _overlay.enabled = true;
            _registered      = true;
            _layerType       = layer;

            Debug.Log($"[VWorldOverlay] {layer} 등록 완료 — 네이티브 재구성 대기 중...");

            // Cesium 네이티브 재구성 완료 대기
            yield return new WaitForSeconds(1.5f);

            // GPS 샘플링 재개
            if (_heightSamplerToDisable != null)
                _heightSamplerToDisable.enabled = samplerWasEnabled;

            Debug.Log($"[VWorldOverlay] {layer} 적용 완료");
        }

        /// <summary>오버레이를 비활성화합니다 (컴포넌트 파괴 없음).</summary>
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
