using UnityEngine;

namespace Rugem.RoadTools
{
    /// <summary>
    /// 메인 카메라에서 world terrain과 직교하는 위치(수직 정사영)에
    /// 'mainCameraNav' 오브젝트를 항상 유지합니다.
    ///
    /// LateUpdate마다 Camera.main 아래로 Physics.Raycast를 쏴 지형 표면 Y를 구한 뒤
    /// 자식 오브젝트 mainCameraNav를 해당 지점에 배치합니다.
    /// NavigationService · RouteRenderer가 이 위치를 경로 시작점으로 사용합니다.
    /// </summary>
    public class CameraNavAnchor : MonoBehaviour
    {
        [Header("지면 감지")]
        [Tooltip("카메라 위에서 시작하는 하방 레이캐스트 높이 오프셋 (미터)")]
        [SerializeField] private float _raycastOriginHeight = 500f;
        [Tooltip("지형/도로로 인식할 레이어. 0이면 전체 레이어.")]
        [SerializeField] private LayerMask _groundLayerMask = ~0;

        /// <summary>지형 표면에 투영된 카메라 정사영 위치 (mainCameraNav Transform)</summary>
        public Transform NavTransform { get; private set; }

        private void Awake()
        {
            // 기존 mainCameraNav 재사용 또는 신규 생성
            var existing = transform.Find("mainCameraNav");
            if (existing != null)
            {
                NavTransform = existing;
            }
            else
            {
                var go = new GameObject("mainCameraNav");
                go.transform.SetParent(transform, false);
                NavTransform = go.transform;
            }
        }

        private void LateUpdate()
        {
            if (Camera.main == null || NavTransform == null) return;

            Vector3 camPos = Camera.main.transform.position;
            NavTransform.position = SampleGroundBelow(camPos);
        }

        /// <summary>
        /// 카메라 정사영 지점의 terrain 표면 Y를 샘플링합니다.
        /// 레이캐스트 실패(타일 미로드) 시 카메라 위치에서 눈높이만큼 내린 값을 폴백으로 반환합니다.
        /// </summary>
        private Vector3 SampleGroundBelow(Vector3 camPos)
        {
            Vector3 origin = new Vector3(camPos.x, camPos.y + _raycastOriginHeight, camPos.z);
            float   maxDist = _raycastOriginHeight * 2f + Mathf.Abs(camPos.y) + 100f;
            LayerMask mask  = _groundLayerMask == 0 ? ~0 : _groundLayerMask;

            if (Physics.Raycast(origin, Vector3.down, out RaycastHit hit, maxDist, mask))
                return new Vector3(camPos.x, hit.point.y, camPos.z);

            // 폴백: 카메라 아래 (지형 타일 미로드 상태)
            return new Vector3(camPos.x, camPos.y - 2f, camPos.z);
        }
    }
}
