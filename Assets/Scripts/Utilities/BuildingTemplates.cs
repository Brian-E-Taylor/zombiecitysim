using Unity.Collections;
using Unity.Mathematics;

public enum BuildingType : byte
{
    SmallHouse = 0,         // 1-4 cells, fills entire region
    MediumBuilding = 1,     // 5-12 cells, rectangular
    LargeBuilding = 2,      // 13-25 cells, rectangular with possible courtyard
    LShape = 3,             // L-shaped building
    UShape = 4,             // U-shaped building with courtyard
    Complex = 5,            // Multiple connected structures
    Tower = 6               // Tall single-cell or small footprint
}

public struct BuildingTemplate
{
    public BuildingType Type;
    public int MinWidth;
    public int MinHeight;
    public int MaxWidth;
    public int MaxHeight;
    public byte BaseHeight;     // Base building height
    public byte MaxExtraHeight; // Random additional height
    public bool HasCourtyard;
    public float SpawnProbability; // Weight for random selection
}

public static class BuildingTemplates
{
    /// <summary>
    /// Applies building templates to detected regions, assigning BuildingIds and Heights.
    /// </summary>
    public static void ApplyTemplatesToRegions(
        ref NativeArray<bool> tileExists,
        ref NativeArray<ushort> regionIds,
        ref NativeArray<ushort> buildingIds,
        ref NativeArray<byte> heights,
        int numTilesX,
        int numTilesY,
        ref NativeList<BuildingRegion> regions,
        ref Random rng)
    {
        ushort currentBuildingId = 1;
        var regionCells = new NativeList<int2>(256, Allocator.Temp);

        for (int i = 0; i < regions.Length; i++)
        {
            var region = regions[i];
            BuildingRegionDetector.GetRegionCells(ref regionIds, numTilesX, numTilesY, region.RegionId, ref regionCells);

            if (regionCells.Length == 0)
                continue;

            // Select template based on region classification
            var template = SelectTemplate(region, ref rng);

            // Apply the template to this region
            ApplyTemplateToRegion(
                ref tileExists,
                ref buildingIds,
                ref heights,
                numTilesX,
                numTilesY,
                ref region,
                ref regionCells,
                template,
                currentBuildingId,
                ref rng);

            currentBuildingId++;
        }

        regionCells.Dispose();
    }

    private static BuildingTemplate SelectTemplate(BuildingRegion region, ref Random rng)
    {
        // Select based on region size and shape
        switch (region.Size)
        {
            case RegionSize.Small:
                return new BuildingTemplate
                {
                    Type = BuildingType.SmallHouse,
                    MinWidth = 1, MinHeight = 1,
                    MaxWidth = 2, MaxHeight = 2,
                    BaseHeight = 1,
                    MaxExtraHeight = 1,
                    HasCourtyard = false,
                    SpawnProbability = 1.0f
                };

            case RegionSize.Medium:
                // Chance for L-shape (works for any shape, not just square)
                if (region.CellCount >= 6 && rng.NextFloat() < 0.4f)
                {
                    return new BuildingTemplate
                    {
                        Type = BuildingType.LShape,
                        MinWidth = 3, MinHeight = 3,
                        MaxWidth = 5, MaxHeight = 5,
                        BaseHeight = 2,
                        MaxExtraHeight = 2,
                        HasCourtyard = false,
                        SpawnProbability = 0.4f
                    };
                }
                if (region.Shape == RegionShape.Square)
                {
                    return new BuildingTemplate
                    {
                        Type = BuildingType.MediumBuilding,
                        MinWidth = 2, MinHeight = 2,
                        MaxWidth = 4, MaxHeight = 4,
                        BaseHeight = 2,
                        MaxExtraHeight = 2,
                        HasCourtyard = false,
                        SpawnProbability = 1.0f
                    };
                }
                else
                {
                    return new BuildingTemplate
                    {
                        Type = BuildingType.MediumBuilding,
                        MinWidth = 2, MinHeight = 2,
                        MaxWidth = 5, MaxHeight = 3,
                        BaseHeight = 1,
                        MaxExtraHeight = 2,
                        HasCourtyard = false,
                        SpawnProbability = 1.0f
                    };
                }

            case RegionSize.Large:
                float roll = rng.NextFloat();
                // 30% chance for L-shape
                if (roll < 0.3f)
                {
                    return new BuildingTemplate
                    {
                        Type = BuildingType.LShape,
                        MinWidth = 4, MinHeight = 4,
                        MaxWidth = 8, MaxHeight = 8,
                        BaseHeight = 2,
                        MaxExtraHeight = 3,
                        HasCourtyard = false,
                        SpawnProbability = 0.3f
                    };
                }
                // 30% chance for U-shape (for square-ish regions)
                if (roll < 0.6f && region.AspectRatio < 2.0f && region.CellCount >= 16)
                {
                    return new BuildingTemplate
                    {
                        Type = BuildingType.UShape,
                        MinWidth = 4, MinHeight = 4,
                        MaxWidth = 8, MaxHeight = 8,
                        BaseHeight = 2,
                        MaxExtraHeight = 3,
                        HasCourtyard = true,
                        SpawnProbability = 0.4f
                    };
                }
                // Large rectangular building
                return new BuildingTemplate
                {
                    Type = BuildingType.LargeBuilding,
                    MinWidth = 4, MinHeight = 3,
                    MaxWidth = 10, MaxHeight = 8,
                    BaseHeight = 2,
                    MaxExtraHeight = 4,
                    HasCourtyard = region.CellCount >= 25 && rng.NextFloat() < 0.3f,
                    SpawnProbability = 1.0f
                };

            default:
                return new BuildingTemplate
                {
                    Type = BuildingType.SmallHouse,
                    MinWidth = 1, MinHeight = 1,
                    MaxWidth = 2, MaxHeight = 2,
                    BaseHeight = 1,
                    MaxExtraHeight = 1,
                    HasCourtyard = false,
                    SpawnProbability = 1.0f
                };
        }
    }

    private static void ApplyTemplateToRegion(
        ref NativeArray<bool> tileExists,
        ref NativeArray<ushort> buildingIds,
        ref NativeArray<byte> heights,
        int numTilesX,
        int numTilesY,
        ref BuildingRegion region,
        ref NativeList<int2> regionCells,
        BuildingTemplate template,
        ushort buildingId,
        ref Random rng)
    {
        // Calculate building height (taller buildings near region center)
        byte buildingHeight = (byte)(template.BaseHeight + rng.NextInt(0, template.MaxExtraHeight + 1));

        // For complex templates, we might carve courtyards or create patterns
        switch (template.Type)
        {
            case BuildingType.UShape:
                ApplyUShapeTemplate(ref tileExists, ref buildingIds, ref heights, numTilesX, numTilesY,
                    ref region, ref regionCells, buildingId, buildingHeight, ref rng);
                break;

            case BuildingType.LShape:
                ApplyLShapeTemplate(ref tileExists, ref buildingIds, ref heights, numTilesX, numTilesY,
                    ref region, ref regionCells, buildingId, buildingHeight, ref rng);
                break;

            case BuildingType.LargeBuilding when template.HasCourtyard:
                ApplyCourtyardTemplate(ref tileExists, ref buildingIds, ref heights, numTilesX, numTilesY,
                    ref region, ref regionCells, buildingId, buildingHeight, ref rng);
                break;

            default:
                // Fill entire region with building
                ApplySolidTemplate(ref buildingIds, ref heights, numTilesX,
                    ref regionCells, buildingId, buildingHeight);
                break;
        }
    }

    private static void ApplySolidTemplate(
        ref NativeArray<ushort> buildingIds,
        ref NativeArray<byte> heights,
        int numTilesX,
        ref NativeList<int2> regionCells,
        ushort buildingId,
        byte buildingHeight)
    {
        for (int i = 0; i < regionCells.Length; i++)
        {
            var cell = regionCells[i];
            int idx = cell.y * numTilesX + cell.x;
            buildingIds[idx] = buildingId;
            heights[idx] = buildingHeight;
        }
    }

    private static void ApplyUShapeTemplate(
        ref NativeArray<bool> tileExists,
        ref NativeArray<ushort> buildingIds,
        ref NativeArray<byte> heights,
        int numTilesX,
        int numTilesY,
        ref BuildingRegion region,
        ref NativeList<int2> regionCells,
        ushort buildingId,
        byte buildingHeight,
        ref Random rng)
    {
        // U-shape: carve out center and one side to create actual U with opening
        int courtyardStartX = region.BoundsMin.x + 1;
        int courtyardEndX = region.BoundsMax.x - 1;
        int courtyardStartY = region.BoundsMin.y + 1;
        int courtyardEndY = region.BoundsMax.y - 1;

        // Only create courtyard if we have room
        if (courtyardEndX <= courtyardStartX || courtyardEndY <= courtyardStartY)
        {
            ApplySolidTemplate(ref buildingIds, ref heights, numTilesX, ref regionCells, buildingId, buildingHeight);
            return;
        }

        // Randomly choose which side is open (0=bottom, 1=top, 2=left, 3=right)
        int openSide = rng.NextInt(0, 4);

        for (int i = 0; i < regionCells.Length; i++)
        {
            var cell = regionCells[i];
            int idx = cell.y * numTilesX + cell.x;

            // Check if this cell is in the courtyard area (center)
            bool isCourtyardCell = cell.x >= courtyardStartX && cell.x <= courtyardEndX &&
                                   cell.y >= courtyardStartY && cell.y <= courtyardEndY;

            // Check if this cell is in the open side passage (all cells between courtyard and building edge)
            bool isOpenSideCell = openSide switch
            {
                0 => cell.y < courtyardStartY && cell.x >= courtyardStartX && cell.x <= courtyardEndX, // Bottom open
                1 => cell.y > courtyardEndY && cell.x >= courtyardStartX && cell.x <= courtyardEndX,   // Top open
                2 => cell.x < courtyardStartX && cell.y >= courtyardStartY && cell.y <= courtyardEndY, // Left open
                _ => cell.x > courtyardEndX && cell.y >= courtyardStartY && cell.y <= courtyardEndY    // Right open
            };

            if (isCourtyardCell || isOpenSideCell)
            {
                // Mark as open space (road-like, no building)
                tileExists[idx] = false;
                buildingIds[idx] = 0;
                heights[idx] = 0;
            }
            else
            {
                buildingIds[idx] = buildingId;
                heights[idx] = buildingHeight;
            }
        }
    }

    private static void ApplyLShapeTemplate(
        ref NativeArray<bool> tileExists,
        ref NativeArray<ushort> buildingIds,
        ref NativeArray<byte> heights,
        int numTilesX,
        int numTilesY,
        ref BuildingRegion region,
        ref NativeList<int2> regionCells,
        ushort buildingId,
        byte buildingHeight,
        ref Random rng)
    {
        // L-shape: remove one corner quadrant
        int midX = (region.BoundsMin.x + region.BoundsMax.x) / 2;
        int midY = (region.BoundsMin.y + region.BoundsMax.y) / 2;

        // Randomly choose which corner to remove (0=TL, 1=TR, 2=BL, 3=BR)
        int cornerToRemove = rng.NextInt(0, 4);

        for (int i = 0; i < regionCells.Length; i++)
        {
            var cell = regionCells[i];
            int idx = cell.y * numTilesX + cell.x;

            bool removeCell = cornerToRemove switch
            {
                0 => cell.x < midX && cell.y > midY, // Top-left
                1 => cell.x > midX && cell.y > midY, // Top-right
                2 => cell.x < midX && cell.y < midY, // Bottom-left
                _ => cell.x > midX && cell.y < midY  // Bottom-right
            };

            if (removeCell)
            {
                tileExists[idx] = false;
                buildingIds[idx] = 0;
                heights[idx] = 0;
            }
            else
            {
                buildingIds[idx] = buildingId;
                heights[idx] = buildingHeight;
            }
        }
    }

    private static void ApplyCourtyardTemplate(
        ref NativeArray<bool> tileExists,
        ref NativeArray<ushort> buildingIds,
        ref NativeArray<byte> heights,
        int numTilesX,
        int numTilesY,
        ref BuildingRegion region,
        ref NativeList<int2> regionCells,
        ushort buildingId,
        byte buildingHeight,
        ref Random rng)
    {
        // Create a central courtyard in a rectangular building with an entrance
        int width = region.Width;
        int height = region.Height;

        // Courtyard size (at least 1 cell in each dimension if building is big enough)
        int courtyardWidth = math.max(1, (width - 2) / 2);
        int courtyardHeight = math.max(1, (height - 2) / 2);

        int courtyardStartX = region.BoundsMin.x + (width - courtyardWidth) / 2;
        int courtyardEndX = courtyardStartX + courtyardWidth - 1;
        int courtyardStartY = region.BoundsMin.y + (height - courtyardHeight) / 2;
        int courtyardEndY = courtyardStartY + courtyardHeight - 1;

        // Calculate entrance position (middle of chosen side, extending from courtyard to building edge)
        // Entrance is 2-3 cells wide for better access
        int openSide = rng.NextInt(0, 4);
        int entranceMidX = (courtyardStartX + courtyardEndX) / 2;
        int entranceMidY = (courtyardStartY + courtyardEndY) / 2;
        int entranceHalfWidth = math.max(1, math.min(courtyardWidth / 2, 1)); // At least 1 cell on each side of center

        for (int i = 0; i < regionCells.Length; i++)
        {
            var cell = regionCells[i];
            int idx = cell.y * numTilesX + cell.x;

            bool isCourtyardCell = cell.x >= courtyardStartX && cell.x <= courtyardEndX &&
                                   cell.y >= courtyardStartY && cell.y <= courtyardEndY;

            // Check if this cell is part of the entrance passage (2-3 cells wide)
            bool isEntranceCell = openSide switch
            {
                0 => cell.x >= entranceMidX - entranceHalfWidth && cell.x <= entranceMidX + entranceHalfWidth &&
                     cell.y < courtyardStartY && cell.y >= region.BoundsMin.y, // Bottom entrance
                1 => cell.x >= entranceMidX - entranceHalfWidth && cell.x <= entranceMidX + entranceHalfWidth &&
                     cell.y > courtyardEndY && cell.y <= region.BoundsMax.y,   // Top entrance
                2 => cell.y >= entranceMidY - entranceHalfWidth && cell.y <= entranceMidY + entranceHalfWidth &&
                     cell.x < courtyardStartX && cell.x >= region.BoundsMin.x, // Left entrance
                _ => cell.y >= entranceMidY - entranceHalfWidth && cell.y <= entranceMidY + entranceHalfWidth &&
                     cell.x > courtyardEndX && cell.x <= region.BoundsMax.x    // Right entrance
            };

            if (isCourtyardCell || isEntranceCell)
            {
                tileExists[idx] = false;
                buildingIds[idx] = 0;
                heights[idx] = 0;
            }
            else
            {
                buildingIds[idx] = buildingId;
                heights[idx] = buildingHeight;
            }
        }
    }
}
