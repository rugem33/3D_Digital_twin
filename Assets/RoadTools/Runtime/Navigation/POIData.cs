using System;

namespace Rugem.RoadTools
{
    [Serializable]
    public class POIData
    {
        public string name;
        public string category;
        public double latitude;
        public double longitude;

        public POIData(string name, string category, double latitude, double longitude)
        {
            this.name     = name;
            this.category = category;
            this.latitude  = latitude;
            this.longitude = longitude;
        }
    }
}
