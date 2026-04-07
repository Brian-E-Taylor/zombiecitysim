using Unity.Collections;
using Unity.Mathematics;

public enum RegionSize : byte
{
    Small = 0,      // 1-4 cells
    Medium = 1,     // 5-20 cells
    Large = 2       // 21+ cells
}

public enum RegionShape : byte
{
    Square = 0,     // Aspect ratio close to 1
    Elongated = 1,  // Long and thin
    Irregular = 2   // Complex shape
}

public struct BuildingRegion
{
    public ushort RegionId;
    public int2 BoundsMin;      // Bottom-left corner of bounding box
    public int2 BoundsMax;      // Top-right corner of bounding box
    public int CellCount;       // Total number of cells in region
    public float2 Centroid;     // Center of mass
    public RegionSize Size;
    public RegionShape Shape;

    public int Width => BoundsMax.x - BoundsMin.x + 1;
    public int Height => BoundsMax.y - BoundsMin.y + 1;
    public int BoundsArea => Width * Height;
    public float FillRatio => (float)CellCount / BoundsArea;
    public float AspectRatio => (float)math.max(Width, Height) / math.max(1, math.min(Width, Height));
}

public static class BuildingRegionDetector
{
    /// <summary>
    /// Detects all contiguous building regions using flood-fill algorithm.
    /// </summary>
    /// <param name="tileExists">Grid where true = building cell</param>
    /// <param name="numTilesX">Grid width</param>
    /// <param name="numTilesY">Grid height</param>
    /// <param name="regionIds">Output array mapping each cell to its region ID (0 = no region/road)</param>
    /// <param name="regions">Output list of detected regions</param>
    public static void DetectRegions(
        ref NativeArray<bool> tileExists,
        int numTilesX,
        int numTilesY,
        ref NativeArray<ushort> regionIds,
        ref NativeList<BuildingRegion> regions)
    {
        // Initialize all region IDs to 0
        for (var i = 0; i < regionIds.Length; i++)
            regionIds[i] = 0;

        ushort currentRegionId = 1;
        var cellQueue = new NativeList<int2>(256, Allocator.Temp);
        var regionCells = new NativeList<int2>(256, Allocator.Temp);

        for (var y = 0; y < numTilesY; y++)
        {
            for (var x = 0; x < numTilesX; x++)
            {
                var idx = y * numTilesX + x;

                // Skip if not a building or already assigned
                if (!tileExists[idx] || regionIds[idx] != 0)
                    continue;

                // Start flood-fill from this cell
                regionCells.Clear();
                cellQueue.Clear();
                cellQueue.Add(new int2(x, y));

                var boundsMin = new int2(x, y);
                var boundsMax = new int2(x, y);
                var centroidSum = float2.zero;

                while (cellQueue.Length > 0)
                {
                    var cell = cellQueue[^1];
                    cellQueue.RemoveAt(cellQueue.Length - 1);

                    var cellIdx = cell.y * numTilesX + cell.x;

                    // Skip if out of bounds, not a building, or already visited
                    if (cell.x < 0 || cell.x >= numTilesX ||
                        cell.y < 0 || cell.y >= numTilesY)
                        continue;

                    if (!tileExists[cellIdx] || regionIds[cellIdx] != 0)
                        continue;

                    // Mark as visited and add to region
                    regionIds[cellIdx] = currentRegionId;
                    regionCells.Add(cell);

                    // Update bounds
                    boundsMin = math.min(boundsMin, cell);
                    boundsMax = math.max(boundsMax, cell);
                    centroidSum += new float2(cell.x, cell.y);

                    // Add 4-connected neighbors to queue
                    cellQueue.Add(new int2(cell.x - 1, cell.y));
                    cellQueue.Add(new int2(cell.x + 1, cell.y));
                    cellQueue.Add(new int2(cell.x, cell.y - 1));
                    cellQueue.Add(new int2(cell.x, cell.y + 1));
                }

                // Create region if we found any cells
                if (regionCells.Length > 0)
                {
                    var region = new BuildingRegion
                    {
                        RegionId = currentRegionId,
                        BoundsMin = boundsMin,
                        BoundsMax = boundsMax,
                        CellCount = regionCells.Length,
                        Centroid = centroidSum / regionCells.Length
                    };

                    // Classify size
                    if (region.CellCount <= 4)
                        region.Size = RegionSize.Small;
                    else if (region.CellCount <= 20)
                        region.Size = RegionSize.Medium;
                    else
                        region.Size = RegionSize.Large;

                    // Classify shape
                    if (region is { AspectRatio: < 1.5f, FillRatio: > 0.8f })
                        region.Shape = RegionShape.Square;
                    else if (region.AspectRatio >= 2.5f || region.FillRatio < 0.5f)
                        region.Shape = RegionShape.Irregular;
                    else
                        region.Shape = RegionShape.Elongated;

                    regions.Add(region);
                    currentRegionId++;
                }
            }
        }

        cellQueue.Dispose();
        regionCells.Dispose();
    }

    /// <summary>
    /// Gets all cell coordinates belonging to a specific region.
    /// </summary>
    public static void GetRegionCells(
        ref NativeArray<ushort> regionIds,
        int numTilesX,
        int numTilesY,
        ushort targetRegionId,
        ref NativeList<int2> cells)
    {
        cells.Clear();
        for (var y = 0; y < numTilesY; y++)
        {
            for (var x = 0; x < numTilesX; x++)
            {
                if (regionIds[y * numTilesX + x] == targetRegionId)
                    cells.Add(new int2(x, y));
            }
        }
    }
}
