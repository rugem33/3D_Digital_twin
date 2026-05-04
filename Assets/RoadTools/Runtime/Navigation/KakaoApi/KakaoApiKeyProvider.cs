using UnityEngine;

namespace Rugem.RoadTools
{
    public static class KakaoApiKeyProvider
    {
        private const string ResourcePath = "kakao_api_key";
        private static string _cachedKey;
        private static bool _loaded;

        public static string GetRestApiKey()
        {
            if (_loaded)
                return _cachedKey;

            _loaded = true;
            TextAsset keyAsset = Resources.Load<TextAsset>(ResourcePath);
            if (keyAsset == null)
            {
                _cachedKey = "";
                Debug.LogWarning($"[KakaoApiKeyProvider] Resources/{ResourcePath}.txt 파일을 찾을 수 없습니다.");
                return _cachedKey;
            }

            _cachedKey = NormalizeKey(keyAsset.text);
            if (string.IsNullOrWhiteSpace(_cachedKey))
                Debug.LogWarning($"[KakaoApiKeyProvider] Resources/{ResourcePath}.txt에 유효한 REST API 키가 없습니다.");

            return _cachedKey;
        }

        public static string Resolve(string inspectorValue)
        {
            return string.IsNullOrWhiteSpace(inspectorValue)
                ? GetRestApiKey()
                : NormalizeKey(inspectorValue);
        }

        private static string NormalizeKey(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
                return "";

            string[] lines = raw.Split('\n');
            foreach (string line in lines)
            {
                string trimmed = line.Trim();
                if (trimmed.Length == 0 || trimmed.StartsWith("#"))
                    continue;

                return trimmed.StartsWith("KakaoAK ")
                    ? trimmed.Substring("KakaoAK ".Length).Trim()
                    : trimmed;
            }

            return "";
        }
    }
}
