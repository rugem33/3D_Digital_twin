using UnityEngine;

namespace Rugem.RoadTools
{
    /// <summary>
    /// 카카오 REST API 키를 Resources 폴더의 텍스트 파일에서 로드하는 정적 유틸리티 클래스입니다.
    /// 키는 최초 로드 후 캐시되어 이후 호출 시 재로드 없이 재사용됩니다.
    ///
    /// 키 파일 위치: Assets/Resources/kakao_api_key.txt
    /// 파일 작성 형식:
    ///   - 키만 한 줄: "abc1234567890"
    ///   - KakaoAK 접두사 포함: "KakaoAK abc1234567890"  (붙여넣기 실수 자동 처리)
    ///   - # 으로 시작하는 줄은 주석으로 무시됩니다.
    /// </summary>
    public static class KakaoApiKeyProvider
    {
        /// <summary>Resources 폴더 내 API 키 텍스트 파일 경로 (확장자 제외)</summary>
        private const string ResourcePath = "kakao_api_key";

        /// <summary>
        /// 로드된 REST API 키 캐시.
        /// 빈 문자열("")도 "로드 시도 완료" 상태로 취급해 매번 재로드하지 않습니다.
        /// </summary>
        private static string _cachedKey;

        /// <summary>
        /// 파일 로드 시도 여부.
        /// true가 된 이후에는 파일 읽기를 건너뛰고 _cachedKey를 반환합니다.
        /// </summary>
        private static bool _loaded;

        /// <summary>
        /// Resources/kakao_api_key.txt에서 REST API 키를 로드합니다.
        /// 최초 호출 시 파일을 읽고, 이후는 캐시된 값을 반환합니다.
        /// </summary>
        /// <returns>정규화된 REST API 키 문자열. 실패 시 빈 문자열("").</returns>
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

        /// <summary>
        /// Inspector SerializeField 값과 Resources 파일 중 유효한 키를 선택합니다.
        /// Inspector 값이 비어 있으면 Resources 파일에서 로드한 키를 사용합니다.
        /// </summary>
        /// <param name="inspectorValue">Inspector에서 직접 입력한 키 (비어 있으면 파일 우선)</param>
        public static string Resolve(string inspectorValue)
        {
            return string.IsNullOrWhiteSpace(inspectorValue)
                ? GetRestApiKey()
                : NormalizeKey(inspectorValue);
        }

        /// <summary>
        /// 키 문자열을 정규화합니다:
        ///   1. 빈 줄과 # 주석 줄을 제거합니다.
        ///   2. "KakaoAK " 접두사가 있으면 제거합니다.
        ///   3. 앞뒤 공백을 제거합니다.
        /// </summary>
        private static string NormalizeKey(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
                return "";

            string[] lines = raw.Split('\n');
            foreach (string line in lines)
            {
                string trimmed = line.Trim();
                // 빈 줄 또는 주석 줄 건너뜀
                if (trimmed.Length == 0 || trimmed.StartsWith("#"))
                    continue;

                // "KakaoAK " 접두사 자동 제거
                return trimmed.StartsWith("KakaoAK ")
                    ? trimmed.Substring("KakaoAK ".Length).Trim()
                    : trimmed;
            }

            return "";
        }
    }
}
