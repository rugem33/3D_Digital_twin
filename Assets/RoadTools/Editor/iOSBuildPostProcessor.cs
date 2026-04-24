// iOS 빌드 시에만 활성화되는 에디터 스크립트
#if UNITY_IOS
using UnityEditor;
using UnityEditor.Callbacks;
using UnityEditor.iOS.Xcode;
using System.IO;

namespace Rugem.RoadTools.Editor
{
    /// <summary>
    /// US-03 iOS 지원: 빌드 후 Xcode 프로젝트의 Info.plist에
    /// 위치 권한 사용 목적 문자열을 자동으로 추가합니다.
    /// </summary>
    public static class iOSBuildPostProcessor
    {
        [PostProcessBuild(100)]
        public static void OnPostProcessBuild(BuildTarget target, string buildPath)
        {
            if (target != BuildTarget.iOS) return;

            string plistPath = Path.Combine(buildPath, "Info.plist");
            var plist = new PlistDocument();
            plist.ReadFromFile(plistPath);

            PlistElementDict rootDict = plist.root;

            // 앱 사용 중 위치 접근 권한 안내 문구
            if (!rootDict.values.ContainsKey("NSLocationWhenInUseUsageDescription"))
            {
                rootDict.SetString(
                    "NSLocationWhenInUseUsageDescription",
                    "지도 위에 현재 위치를 표시하고 실시간으로 길을 안내하기 위해 위치 정보가 필요합니다.");
            }

            // 항상 위치 접근 권한 (필요 시 활성화)
            // rootDict.SetString("NSLocationAlwaysAndWhenInUseUsageDescription", "...");

            plist.WriteToFile(plistPath);

            UnityEngine.Debug.Log("[iOSBuildPostProcessor] Info.plist에 위치 권한 설명을 추가했습니다.");
        }
    }
}
#endif
