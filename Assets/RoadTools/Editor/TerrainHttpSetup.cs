// localhost 지형 서버에 HTTP로 접속하려면
// Player Settings > Other Settings > Allow downloads over HTTP = Always allowed 이 필요합니다.
// Unity가 로드될 때 자동으로 설정합니다.

#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

namespace Rugem.RoadTools.Editor
{
    [InitializeOnLoad]
    internal static class TerrainHttpSetup
    {
        static TerrainHttpSetup()
        {
#if UNITY_2022_2_OR_NEWER
            if (PlayerSettings.insecureHttpOption != InsecureHttpOption.AlwaysAllowed)
            {
                PlayerSettings.insecureHttpOption = InsecureHttpOption.AlwaysAllowed;
                Debug.Log("[RoadTools] Player Settings > Allow downloads over HTTP → Always allowed 적용 (localhost 지형 서버용)");
            }
#endif
        }
    }
}
#endif
