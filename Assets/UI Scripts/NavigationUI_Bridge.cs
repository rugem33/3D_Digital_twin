using UnityEngine;
using Rugem.RoadTools; // 친구가 만든 네임스페이스 연결

public class NavigationUI_Bridge : MonoBehaviour
{
    [Header("연결된 UI 패널들")]
    public GameObject panel_SearchBar;
    public GameObject panel_SearchOpen;

    private NavigationUIController _mainController;

    private void Awake()
    {
        // 같은 오브젝트에 있는 원본 컨트롤러를 가져옵니다.
        _mainController = GetComponent<NavigationUIController>();
    }

    // 1. 상단 검색바를 눌렀을 때 실행 (Panel_SearchBar에 연결)
    public void OpenSearchPanel()
    {
        // 원본 스크립트의 검색 시작 로직 실행
        _mainController.GetType().GetMethod("OpenSearch", 
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)?.Invoke(_mainController, null);

        // UGUI 패널 전환
        panel_SearchBar.SetActive(false);
        panel_SearchOpen.SetActive(true);
    }

    // 2. 뒤로가기 버튼을 눌렀을 때 실행 (ButtonBack에 연결)
    public void CloseSearchPanel()
    {
        // 원본 스크립트의 닫기 로직 실행
        _mainController.GetType().GetMethod("CloseSearch", 
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)?.Invoke(_mainController, null);

        // UGUI 패널 전환
        panel_SearchBar.SetActive(true);
        panel_SearchOpen.SetActive(false);
    }
}
