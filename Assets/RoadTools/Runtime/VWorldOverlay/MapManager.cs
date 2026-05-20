using System.Collections;
using Rugem.RoadTools;
using UnityEngine;
using UnityEngine.UI;

public class MapManager : MonoBehaviour
{
    [Header("V-World overlay")]
    [SerializeField] private VWorldOverlayController _overlayController;
    [SerializeField] private bool _autoWireChildButtons = true;

    private void Awake()
    {
        if (_overlayController == null)
            _overlayController = FindAnyObjectByType<VWorldOverlayController>();

        if (_autoWireChildButtons)
            WireChildButtons();
    }

    private IEnumerator Start()
    {
        for (int i = 0; i < 10; i++)
        {
            if (_overlayController == null)
                _overlayController = FindAnyObjectByType<VWorldOverlayController>();

            if (_overlayController != null && _overlayController.IsOverlayActive)
                break;

            yield return null;
        }

        ShowSatelliteMap();
    }

    public void ShowSatelliteMap()
    {
        if (!TryGetOverlayController(out VWorldOverlayController controller)) return;

        controller.SwitchLayer(VWorldLayerType.Satellite);
    }

    public void ShowBaseMap()
    {
        if (!TryGetOverlayController(out VWorldOverlayController controller)) return;

        controller.SwitchLayer(VWorldLayerType.Base);
    }

    public void ShowTerrainOnly()
    {
        if (!TryGetOverlayController(out VWorldOverlayController controller)) return;

        controller.SetVisible(false);
    }

    private bool TryGetOverlayController(out VWorldOverlayController controller)
    {
        controller = _overlayController;
        if (controller == null)
            controller = _overlayController = FindAnyObjectByType<VWorldOverlayController>();

        if (controller != null)
            return true;

        Debug.LogError("[MapManager] VWorldOverlayController was not found.");
        return false;
    }

    private void WireChildButtons()
    {
        foreach (Button button in GetComponentsInChildren<Button>(true))
        {
            switch (button.name)
            {
                case "Btn_Satellite":
                    button.onClick.AddListener(ShowSatelliteMap);
                    break;
                case "Btn_Base":
                    button.onClick.AddListener(ShowBaseMap);
                    break;
                case "Btn_Terrain":
                    button.onClick.AddListener(ShowTerrainOnly);
                    break;
            }
        }
    }
}
