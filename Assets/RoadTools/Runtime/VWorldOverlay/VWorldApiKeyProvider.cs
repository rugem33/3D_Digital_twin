using UnityEngine;

namespace Rugem.RoadTools
{
    /// <summary>
    /// V-World API 키를 Resources 폴더의 텍스트 파일에서 로드하는 정적 유틸리티 클래스.
    /// 키는 최초 로드 후 캐시됩니다.
    ///
    /// 키 파일 위치: Assets/Resources/vworld_api_key.txt
    /// 파일 작성 형식:
    ///   - 키만 한 줄: "XXXXXXXX-XXXX-XXXX-XXXX-XXXXXXXXXXXX"
    ///   - # 으로 시작하는 줄은 주석으로 무시됩니다.
    /// </summary>
    public static class VWorldApiKeyProvider
    {
        private const string ResourcePath = "vworld_api_key";
        private static string _cachedKey;
        private static bool _loaded;

        public static string GetApiKey()
        {
            if (_loaded) return _cachedKey;
            _loaded = true;

            TextAsset keyAsset = Resources.Load<TextAsset>(ResourcePath);
            if (keyAsset == null)
            {
                _cachedKey = "";
                Debug.LogWarning("[VWorldApiKeyProvider] Resources/vworld_api_key.txt 파일을 찾을 수 없습니다. " +
                                 "Assets/Resources/vworld_api_key.txt 를 생성하고 API 키를 입력하세요.");
                return _cachedKey;
            }

            _cachedKey = NormalizeKey(keyAsset.text);
            if (string.IsNullOrWhiteSpace(_cachedKey))
                Debug.LogWarning("[VWorldApiKeyProvider] vworld_api_key.txt에 유효한 API 키가 없습니다.");

            return _cachedKey;
        }

        /// <summary>Inspector 값이 있으면 그것을, 없으면 파일에서 로드한 키를 반환합니다.</summary>
        public static string Resolve(string inspectorValue)
        {
            return string.IsNullOrWhiteSpace(inspectorValue)
                ? GetApiKey()
                : inspectorValue.Trim();
        }

        private static string NormalizeKey(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return "";
            foreach (string line in raw.Split('\n'))
            {
                string trimmed = line.Trim();
                if (trimmed.Length == 0 || trimmed.StartsWith("#")) continue;
                return trimmed;
            }
            return "";
        }
    }
}
