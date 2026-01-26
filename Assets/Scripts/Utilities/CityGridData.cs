public static class CityGridHelper
{
    public static float GetRoadBrightness(RoadHierarchyLevel level)
    {
        return level switch
        {
            RoadHierarchyLevel.Arterial => 0.9f,
            RoadHierarchyLevel.Secondary => 0.75f,
            RoadHierarchyLevel.Tertiary => 0.6f,
            RoadHierarchyLevel.Alley => 0.5f,
            _ => 0.8f
        };
    }

    public static float GetRoadBrightness(byte hierarchyLevel)
    {
        return GetRoadBrightness((RoadHierarchyLevel)hierarchyLevel);
    }
}
