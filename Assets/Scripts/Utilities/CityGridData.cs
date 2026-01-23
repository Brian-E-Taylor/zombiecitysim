using Unity.Collections;

public enum CellType : byte
{
    Building = 0,
    Road = 1
}

public struct CityCell
{
    public CellType Type;       // Building or Road
    public byte RoadHierarchy;  // 0-4
    public ushort BlockId;
}

public static class CityGridHelper
{
    public static void ConvertToBoolArray(
        ref NativeArray<CityCell> cells,
        ref NativeArray<bool> tileExists,
        int numTilesX,
        int numTilesY)
    {
        for (int i = 0; i < cells.Length; i++)
        {
            tileExists[i] = cells[i].Type == CellType.Building;
        }
    }

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
