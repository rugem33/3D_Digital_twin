using System.Collections;
using UnityEngine;
using UnityEngine.UI;
#if UNITY_ANDROID
using UnityEngine.Android;
#endif

namespace Rugem.RoadTools
{
    /// <summary>
    /// US-03: Android/iOS 위치 권한 요청 팝업 및 거부 시 설정 안내 처리
    /// </summary>
    public class LocationPermissionHandler : MonoBehaviour
    {
        [Header("권한 거부 안내 UI (선택)")]
        [Tooltip("권한 거부 시 표시할 안내 패널")]
        [SerializeField] private GameObject _permissionDeniedPanel;
        [Tooltip("앱 설정 화면으로 이동하는 버튼")]
        [SerializeField] private Button _openSettingsButton;
        [Tooltip("권한 재요청 버튼")]
        [SerializeField] private Button _retryButton;

        /// <summary>현재 위치 권한이 허용된 상태인지 여부</summary>
        public bool IsPermissionGranted { get; private set; }

        /// <summary>권한 허용 시 호출되는 이벤트</summary>
        public System.Action OnPermissionGranted;
        /// <summary>권한 거부 시 호출되는 이벤트</summary>
        public System.Action OnPermissionDenied;

        private void Start()
        {
            if (_openSettingsButton != null)
                _openSettingsButton.onClick.AddListener(OpenAppSettings);
            if (_retryButton != null)
                _retryButton.onClick.AddListener(CheckAndRequestPermission);

            if (_permissionDeniedPanel != null)
                _permissionDeniedPanel.SetActive(false);

            CheckAndRequestPermission();
        }

        /// <summary>
        /// 위치 권한을 확인하고 없으면 요청합니다.
        /// </summary>
        public void CheckAndRequestPermission()
        {
#if UNITY_ANDROID
            if (Permission.HasUserAuthorizedPermission(Permission.FineLocation))
            {
                HandlePermissionGranted();
                return;
            }

            var callbacks = new PermissionCallbacks();
            callbacks.PermissionGranted += _ => HandlePermissionGranted();
            callbacks.PermissionDenied += _ => HandlePermissionDenied();
            callbacks.PermissionDeniedAndDontAskAgain += _ => HandlePermissionDenied();
            Permission.RequestUserPermission(Permission.FineLocation, callbacks);

#elif UNITY_IOS
            // iOS는 LocationService.Start() 호출 시 시스템이 자동으로 권한 팝업을 표시함
            StartCoroutine(CheckiOSPermission());
#else
            // 에디터 / 데스크탑: 권한 없이 바로 진행
            HandlePermissionGranted();
#endif
        }

#if UNITY_IOS
        private IEnumerator CheckiOSPermission()
        {
            Input.location.Start();

            float timeout = 8f;
            while (Input.location.status == LocationServiceStatus.Initializing && timeout > 0)
            {
                yield return new WaitForSeconds(0.5f);
                timeout -= 0.5f;
            }

            if (Input.location.status == LocationServiceStatus.Running)
            {
                Input.location.Stop();
                HandlePermissionGranted();
            }
            else
            {
                Input.location.Stop();
                HandlePermissionDenied();
            }
        }
#endif

        private void HandlePermissionGranted()
        {
            IsPermissionGranted = true;
            if (_permissionDeniedPanel != null)
                _permissionDeniedPanel.SetActive(false);

            OnPermissionGranted?.Invoke();
            Debug.Log("[LocationPermission] 위치 권한이 허용되었습니다.");
        }

        private void HandlePermissionDenied()
        {
            IsPermissionGranted = false;
            if (_permissionDeniedPanel != null)
                _permissionDeniedPanel.SetActive(true);

            OnPermissionDenied?.Invoke();
            Debug.LogWarning("[LocationPermission] 위치 권한이 거부되었습니다. 설정에서 위치 권한을 허용해 주세요.");
        }

        /// <summary>
        /// 앱 설정 화면으로 이동합니다. (Android: 앱 상세 정보, iOS: 앱 설정)
        /// </summary>
        private void OpenAppSettings()
        {
#if UNITY_ANDROID
            using (var unityPlayer = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
            using (var activity = unityPlayer.GetStatic<AndroidJavaObject>("currentActivity"))
            using (var intent = new AndroidJavaObject(
                "android.content.Intent", "android.settings.APPLICATION_DETAILS_SETTINGS"))
            {
                var uriClass = new AndroidJavaClass("android.net.Uri");
                var packageUri = uriClass.CallStatic<AndroidJavaObject>(
                    "fromParts", "package", Application.identifier, null);
                intent.Call<AndroidJavaObject>("setData", packageUri).Dispose();
                packageUri.Dispose();
                activity.Call("startActivity", intent);
            }
#elif UNITY_IOS
            Application.OpenURL("app-settings:");
#endif
        }
    }
}
