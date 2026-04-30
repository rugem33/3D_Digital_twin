using System.Collections;
using UnityEngine;
using UnityEngine.UIElements;

namespace Rugem.RoadTools
{
    /// <summary>
    /// Cesium ion credit UI를 제어합니다.
    /// - UIDocument 루트 패널의 pickingMode를 Ignore로 설정해 OnGUI 터치 이벤트 차단 방지
    /// - 크레딧 글자 크기 축소 / 완전 숨김
    /// - 하이퍼링크 터치 차단
    /// </summary>
    [AddComponentMenu("Cesium/Cesium Credit Reducer")]
    public class CesiumCreditReducer : MonoBehaviour
    {
        [Tooltip("크레딧 글자 크기 (원본 11px). 0으로 설정하면 완전히 숨깁니다.")]
        [SerializeField, Range(0, 11)] private int _fontSize = 5;

        [Tooltip("하이퍼링크 터치(클릭) 차단 여부")]
        [SerializeField] private bool _disableLinks = true;

        private UIDocument _creditDocument;
        private VisualElement _onScreenCredits;

        private IEnumerator Start()
        {
            yield return null;
            yield return null;

            Apply();

            while (true)
            {
                yield return new WaitForSeconds(3f);
                Apply();
            }
        }

        private void Apply()
        {
            FindCreditDocument();

            // UIDocument 루트 패널 전체를 non-blocking으로 설정
            // → OnGUI 버튼 및 게임 터치 이벤트가 패널에 흡수되지 않음
            if (_creditDocument != null && _creditDocument.rootVisualElement != null)
            {
                SetPickingModeRecursive(_creditDocument.rootVisualElement, PickingMode.Ignore);
            }

            if (_onScreenCredits == null) return;

            if (_fontSize <= 0)
            {
                _onScreenCredits.style.display = DisplayStyle.None;
            }
            else
            {
                _onScreenCredits.style.display  = DisplayStyle.Flex;
                _onScreenCredits.style.fontSize  = _fontSize;
            }
        }

        private void FindCreditDocument()
        {
            if (_creditDocument != null) return;

            var docs = FindObjectsByType<UIDocument>(FindObjectsSortMode.None);
            foreach (var doc in docs)
            {
                if (doc.rootVisualElement == null) continue;
                var el = doc.rootVisualElement.Q("OnScreenCredits");
                if (el != null)
                {
                    _creditDocument  = doc;
                    _onScreenCredits = el;
                    return;
                }
            }
        }

        /// <summary>
        /// 요소 트리 전체의 pickingMode를 지정값으로 일괄 설정합니다.
        /// Ignore로 설정 시 해당 패널이 포인터 이벤트를 흡수하지 않아
        /// OnGUI와 게임 입력이 정상적으로 동작합니다.
        /// </summary>
        private static void SetPickingModeRecursive(VisualElement root, PickingMode mode)
        {
            root.pickingMode = mode;
            foreach (var child in root.Children())
                SetPickingModeRecursive(child, mode);
        }
    }
}
