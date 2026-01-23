using Unity.Collections;
using Unity.Mathematics;

public struct CityBlock
{
    public int2 Min;        // Bottom-left corner
    public int2 Max;        // Top-right corner
    public ushort BlockId;
}

public struct RoadSplit
{
    public bool IsHorizontal;
    public int Position;
    public int Start, End;
    public int Width;
    public byte Depth;      // For hierarchy
}

public static class BSPCityGenerator
{
    private struct BSPNode
    {
        public int2 Min;
        public int2 Max;
        public byte Depth;
    }

    public static void GenerateBlocks(
        int numTilesX,
        int numTilesY,
        int borderSize,
        int minBlockSize,
        int maxBlockSize,
        float splitVariance,
        ref Random rng,
        ref NativeList<CityBlock> cityBlocks,
        ref NativeList<RoadSplit> roadSplits)
    {
        var nodeStack = new NativeList<BSPNode>(64, Allocator.Temp);

        // Start with the entire interior (minus border)
        nodeStack.Add(new BSPNode
        {
            Min = new int2(borderSize, borderSize),
            Max = new int2(numTilesX - borderSize - 1, numTilesY - borderSize - 1),
            Depth = 0
        });

        ushort blockId = 0;

        while (nodeStack.Length > 0)
        {
            var node = nodeStack[nodeStack.Length - 1];
            nodeStack.RemoveAt(nodeStack.Length - 1);

            int width = node.Max.x - node.Min.x + 1;
            int height = node.Max.y - node.Min.y + 1;

            // Calculate road width for this depth
            int roadWidth = CalculateRoadWidth(node.Depth);

            // Check if we should split
            bool canSplitHorizontally = height >= minBlockSize * 2 + roadWidth;
            bool canSplitVertically = width >= minBlockSize * 2 + roadWidth;

            bool shouldSplit = width > maxBlockSize || height > maxBlockSize;

            if (!shouldSplit && !canSplitHorizontally && !canSplitVertically)
            {
                // This is a leaf node - create a city block
                cityBlocks.Add(new CityBlock
                {
                    Min = node.Min,
                    Max = node.Max,
                    BlockId = blockId++
                });
                continue;
            }

            // Determine split direction
            bool splitHorizontal;
            if (canSplitHorizontally && canSplitVertically)
            {
                // Prefer splitting the longer dimension
                if (width > height * 1.2f)
                    splitHorizontal = false;
                else if (height > width * 1.2f)
                    splitHorizontal = true;
                else
                    splitHorizontal = rng.NextBool();
            }
            else if (canSplitHorizontally)
            {
                splitHorizontal = true;
            }
            else if (canSplitVertically)
            {
                splitHorizontal = false;
            }
            else
            {
                // Cannot split, create leaf block
                cityBlocks.Add(new CityBlock
                {
                    Min = node.Min,
                    Max = node.Max,
                    BlockId = blockId++
                });
                continue;
            }

            // Calculate split position with variance
            int dimension = splitHorizontal ? height : width;
            int midPoint = dimension / 2;
            int variance = (int)(midPoint * splitVariance);
            int splitOffset = rng.NextInt(-variance, variance + 1);
            int splitPos = midPoint + splitOffset;

            // Ensure minimum block sizes are respected
            splitPos = math.clamp(splitPos, minBlockSize, dimension - minBlockSize - roadWidth);

            if (splitHorizontal)
            {
                int splitY = node.Min.y + splitPos;

                // Add the road split
                roadSplits.Add(new RoadSplit
                {
                    IsHorizontal = true,
                    Position = splitY,
                    Start = node.Min.x,
                    End = node.Max.x,
                    Width = roadWidth,
                    Depth = node.Depth
                });

                // Create two child nodes
                nodeStack.Add(new BSPNode
                {
                    Min = node.Min,
                    Max = new int2(node.Max.x, splitY - 1),
                    Depth = (byte)(node.Depth + 1)
                });
                nodeStack.Add(new BSPNode
                {
                    Min = new int2(node.Min.x, splitY + roadWidth),
                    Max = node.Max,
                    Depth = (byte)(node.Depth + 1)
                });
            }
            else
            {
                int splitX = node.Min.x + splitPos;

                // Add the road split
                roadSplits.Add(new RoadSplit
                {
                    IsHorizontal = false,
                    Position = splitX,
                    Start = node.Min.y,
                    End = node.Max.y,
                    Width = roadWidth,
                    Depth = node.Depth
                });

                // Create two child nodes
                nodeStack.Add(new BSPNode
                {
                    Min = node.Min,
                    Max = new int2(splitX - 1, node.Max.y),
                    Depth = (byte)(node.Depth + 1)
                });
                nodeStack.Add(new BSPNode
                {
                    Min = new int2(splitX + roadWidth, node.Min.y),
                    Max = node.Max,
                    Depth = (byte)(node.Depth + 1)
                });
            }
        }

        nodeStack.Dispose();
    }

    public static void ApplyRoadsToGrid(
        ref NativeArray<bool> tileExists,
        int numTilesX,
        int numTilesY,
        ref NativeList<RoadSplit> roadSplits)
    {
        for (int i = 0; i < roadSplits.Length; i++)
        {
            var split = roadSplits[i];

            if (split.IsHorizontal)
            {
                for (int w = 0; w < split.Width; w++)
                {
                    int y = split.Position + w;
                    if (y < 1 || y >= numTilesY - 1) continue;

                    for (int x = split.Start; x <= split.End; x++)
                    {
                        if (x < 1 || x >= numTilesX - 1) continue;
                        tileExists[y * numTilesX + x] = false;
                    }
                }
            }
            else
            {
                for (int w = 0; w < split.Width; w++)
                {
                    int x = split.Position + w;
                    if (x < 1 || x >= numTilesX - 1) continue;

                    for (int y = split.Start; y <= split.End; y++)
                    {
                        if (y < 1 || y >= numTilesY - 1) continue;
                        tileExists[y * numTilesX + x] = false;
                    }
                }
            }
        }
    }

    public static void ApplyRoadsToGridWithHierarchy(
        ref NativeArray<bool> tileExists,
        ref NativeArray<byte> roadHierarchy,
        int numTilesX,
        int numTilesY,
        ref NativeList<RoadSplit> roadSplits)
    {
        for (int i = 0; i < roadSplits.Length; i++)
        {
            var split = roadSplits[i];
            byte hierarchy = GetRoadHierarchyLevel(split.Depth);

            if (split.IsHorizontal)
            {
                for (int w = 0; w < split.Width; w++)
                {
                    int y = split.Position + w;
                    if (y < 1 || y >= numTilesY - 1) continue;

                    for (int x = split.Start; x <= split.End; x++)
                    {
                        if (x < 1 || x >= numTilesX - 1) continue;
                        int idx = y * numTilesX + x;
                        tileExists[idx] = false;
                        // Keep the higher hierarchy (lower value = more important)
                        if (roadHierarchy[idx] == 0 || hierarchy < roadHierarchy[idx])
                            roadHierarchy[idx] = hierarchy;
                    }
                }
            }
            else
            {
                for (int w = 0; w < split.Width; w++)
                {
                    int x = split.Position + w;
                    if (x < 1 || x >= numTilesX - 1) continue;

                    for (int y = split.Start; y <= split.End; y++)
                    {
                        if (y < 1 || y >= numTilesY - 1) continue;
                        int idx = y * numTilesX + x;
                        tileExists[idx] = false;
                        // Keep the higher hierarchy (lower value = more important)
                        if (roadHierarchy[idx] == 0 || hierarchy < roadHierarchy[idx])
                            roadHierarchy[idx] = hierarchy;
                    }
                }
            }
        }
    }

    private static int CalculateRoadWidth(int depth)
    {
        // Minimum width of 3 to allow at least 3 units to pass
        return depth switch
        {
            0 => 5,
            1 => 4,
            2 => 4,
            3 => 3,
            _ => 3
        };
    }

    private static byte GetRoadHierarchyLevel(int depth)
    {
        return depth switch
        {
            0 or 1 => 1,  // Arterial
            2 or 3 => 2,  // Secondary
            4 or 5 => 3,  // Tertiary
            _ => 4        // Alley
        };
    }
}
