using System.Collections.Generic;
using UnityEngine;

namespace Rugem.RoadTools
{
    public class SearchProvider : MonoBehaviour
    {
        [SerializeField] private KakaoPlaceSearchService _kakaoSearch;

        private void Awake() => ResolveDependencies();

        private void ResolveDependencies()
        {
            if (_kakaoSearch == null) _kakaoSearch = FindAnyObjectByType<KakaoPlaceSearchService>();
        }

        public void Search(string query, int maxResults, int radiusMeters,
            System.Action<List<POIData>, string> callback)
        {
            if (_kakaoSearch == null)
            {
                callback?.Invoke(new List<POIData>(), null);
                return;
            }
            _kakaoSearch.Search(query, maxResults, radiusMeters, callback);
        }
    }
}
