using System;

namespace Rugem.RoadTools
{
    /// <summary>
    /// 카카오 장소 검색 결과 또는 수동 지정 목적지를 나타내는 데이터 클래스입니다.
    /// NavigationService에 목적지로 전달되거나, 최근 검색 이력으로 PlayerPrefs에 저장됩니다.
    /// JsonUtility.ToJson/FromJson 직렬화를 위해 [Serializable]이 적용되어 있습니다.
    /// </summary>
    [Serializable]
    public class POIData
    {
        /// <summary>장소 이름 (예: "서울역", "스타벅스 광화문점")</summary>
        public string name;

        /// <summary>
        /// 카카오 API에서 반환된 최상위 카테고리 (예: "음식점", "교통", "공공기관").
        /// KakaoPlaceSearchService.SimplifyCategory()에 의해 최상위 분류만 추출됩니다.
        /// </summary>
        public string category;

        /// <summary>위도 (WGS84 도 단위, 예: 37.5662952)</summary>
        public double latitude;

        /// <summary>경도 (WGS84 도 단위, 예: 126.9779692)</summary>
        public double longitude;

        public POIData(string name, string category, double latitude, double longitude)
        {
            this.name      = name;
            this.category  = category;
            this.latitude  = latitude;
            this.longitude = longitude;
        }
    }
}
