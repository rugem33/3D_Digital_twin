using UnityEngine;
using UnityEngine.UIElements;

namespace Rugem.RoadTools
{
    /// <summary>
    /// Disables the Cesium credit / data attribution UI at runtime.
    /// </summary>
    [AddComponentMenu("Cesium/Cesium Credit Reducer")]
    public class CesiumCreditReducer : MonoBehaviour
    {
        private UIDocument _cesiumDoc;
        private int _rescanFrame;

        private void LateUpdate()
        {
            if (_cesiumDoc == null || Time.frameCount >= _rescanFrame)
            {
                FindCesiumDoc();
                _rescanFrame = Time.frameCount + 30;
            }

            if (_cesiumDoc == null || _cesiumDoc.rootVisualElement == null)
                return;

            DisableCreditUi(_cesiumDoc.rootVisualElement);
        }

        private void FindCesiumDoc()
        {
            _cesiumDoc = null;

            foreach (UIDocument doc in Resources.FindObjectsOfTypeAll<UIDocument>())
            {
                if (doc == null || doc.rootVisualElement == null)
                    continue;

                if (IsCesiumCreditDocument(doc.rootVisualElement))
                {
                    _cesiumDoc = doc;
                    return;
                }
            }
        }

        private static bool IsCesiumCreditDocument(VisualElement root)
        {
            return root.Q("OnScreenCredits") != null ||
                   root.Q("PopupCredits") != null ||
                   ContainsDataAttributionLabel(root);
        }

        private static bool ContainsDataAttributionLabel(VisualElement root)
        {
            foreach (VisualElement element in root.Query<VisualElement>().ToList())
            {
                if (element is Label label && label.text != null && label.text.Contains("Data Attribution"))
                    return true;
            }

            return false;
        }

        private static void DisableCreditUi(VisualElement root)
        {
            root.style.display = DisplayStyle.None;
            root.visible = false;
            SetPickingModeRecursive(root, PickingMode.Ignore);

            VisualElement onScreenCredits = root.Q("OnScreenCredits");
            if (onScreenCredits != null)
            {
                onScreenCredits.style.display = DisplayStyle.None;
                onScreenCredits.visible = false;
            }

            VisualElement popupCredits = root.Q("PopupCredits");
            if (popupCredits != null)
            {
                popupCredits.style.display = DisplayStyle.None;
                popupCredits.visible = false;
            }
        }

        private static void SetPickingModeRecursive(VisualElement root, PickingMode mode)
        {
            root.pickingMode = mode;
            foreach (VisualElement child in root.Children())
                SetPickingModeRecursive(child, mode);
        }
    }
}
