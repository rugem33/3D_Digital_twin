using UnityEngine;

#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem.EnhancedTouch;
#endif

namespace Rugem.RoadTools
{
    /// <summary>
    /// 모바일 입력 초기화 헬퍼.
    /// EnhancedTouchSupport를 활성화해 Touch.activeTouches API를 사용 가능하게 합니다.
    ///
    /// Device Simulator에서 드래그 회전은 FirstPersonGPSController.GetDragDelta()가
    /// Pointer.current(마우스/터치 통합)를 사용하므로 별도 시뮬레이션 설정 불필요.
    /// </summary>
    [AddComponentMenu("RoadTools/Mobile Input Setup")]
    public class MobileInputSetup : MonoBehaviour
    {
        private void Awake()
        {
#if ENABLE_INPUT_SYSTEM
            if (!EnhancedTouchSupport.enabled)
            {
                EnhancedTouchSupport.Enable();
                Debug.Log("[MobileInputSetup] EnhancedTouchSupport 활성화");
            }
#endif
        }

        private void OnDestroy()
        {
#if ENABLE_INPUT_SYSTEM
            if (EnhancedTouchSupport.enabled)
                EnhancedTouchSupport.Disable();
#endif
        }
    }
}
