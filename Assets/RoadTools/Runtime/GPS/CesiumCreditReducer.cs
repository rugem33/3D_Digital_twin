using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

namespace Rugem.RoadTools
{
    /// <summary>
    /// Cesium ion credit UI를 제어합니다.
    /// - UIDocument 루트 패널의 pickingMode를 Ignore로 설정해 OnGUI 터치 이벤트 차단 방지
    /// - 크레딧 글자 크기 축소 / 완전 숨김
    /// - 하이퍼링크 터치 차단
    /// - 경로 탐색 시 새로 로드된 타일셋 크레딧도 즉시 처리
    /// </summary>
    [AddComponentMenu("Cesium/Cesium Credit Reducer")]
    public class CesiumCreditReducer : MonoBehaviour
    {
        [Tooltip("크레딧 글자 크기 (원본 11px). 0으로 설정하면 완전히 숨깁니다.")]
        [SerializeField, Range(0, 11)] private int _fontSize = 0;

        [Tooltip("크레딧 패널 전체를 숨깁니다. true면 _fontSize 무관하게 display:none 적용.")]
        [SerializeField] private bool _hideCompletely = true;

        [Tooltip("하이퍼링크 터치(클릭) 차단 여부")]
        [SerializeField] private bool _disableLinks = true;

        [Tooltip("크레딧 재적용 주기 (초). 타일 로드 시 크레딧이 재표시되는 것을 방지합니다.")]
        [SerializeField] private float _applyInterval = 0.3f;

        private IEnumerator Start()
        {
            yield return null;
            yield return null;
            Apply();

            while (true)
            {
                yield return new WaitForSeconds(_applyInterval);
                Apply();
            }
        }

        private void Apply()
        {
            // 캐시 없이 매번 전체 UIDocument를 탐색 — 새 타일셋 크레딧 즉시 처리
            var docs = FindObjectsByType<UIDocument>(FindObjectsSortMode.None);
            foreach (var doc in docs)
            {
                if (doc.rootVisualElement == null) continue;

                // 모든 UIDocument 루트를 non-blocking으로 설정
                SetPickingModeRecursive(doc.rootVisualElement, PickingMode.Ignore);

                // OnScreenCredits 요소 처리
                var credits = doc.rootVisualElement.Q("OnScreenCredits");
                if (credits == null) continue;

                if (_hideCompletely || _fontSize <= 0)
                {
                    credits.style.display = DisplayStyle.None;
                }
                else
                {
                    credits.style.display   = DisplayStyle.Flex;
                    credits.style.fontSize  = _fontSize;
                    HideChildLinks(credits);
                }
            }
        }

        private void HideChildLinks(VisualElement root)
        {
            if (!_disableLinks) return;
            foreach (var child in root.Children())
            {
                if (child.GetType().Name.Contains("Link") ||
                    child.GetType().Name.Contains("Anchor"))
                    child.pickingMode = PickingMode.Ignore;
                HideChildLinks(child);
            }
        }

        private static void SetPickingModeRecursive(VisualElement root, PickingMode mode)
        {
            root.pickingMode = mode;
            foreach (var child in root.Children())
                SetPickingModeRecursive(child, mode);
        }
    }
}
