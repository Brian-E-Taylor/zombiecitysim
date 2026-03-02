using Unity.Collections;
using Unity.Mathematics;

public static class AlleyGenerator
{
    /// <summary>
    /// Generates alleys and dead-ends within large building regions using random walk algorithm.
    /// </summary>
    public static void GenerateAlleys(
        ref NativeArray<bool> tileExists,
        ref NativeArray<byte> roadHierarchy,
        int numTilesX, int numTilesY,
        ref NativeList<BuildingRegion> regions,
        ref NativeArray<ushort> regionIds,
        int minRegionSize,
        float deadEndProbability,
        int maxLength,
        ref Random rng)
    {
        var path = new NativeList<int2>(maxLength, Allocator.Temp);

        for (var r = 0; r < regions.Length; r++)
        {
            var region = regions[r];

            // Only process large regions with sufficient cell count
            if (region.Size != RegionSize.Large || region.CellCount < minRegionSize)
                continue;

            // Determine number of alleys based on region size (1-2 alleys)
            var numAlleys = region.CellCount >= 50 ? 2 : 1;

            for (var a = 0; a < numAlleys; a++)
            {
                // Find a starting point on the region edge that borders a road
                var startPoint = FindEdgeStartPoint(
                    region,
                    ref tileExists,
                    ref regionIds,
                    numTilesX,
                    numTilesY,
                    ref rng);

                if (startPoint.x < 0)
                    continue; // No valid start point found

                // Generate alley path
                path.Clear();
                GenerateAlleyPath(
                    startPoint,
                    region,
                    deadEndProbability,
                    maxLength,
                    ref path,
                    ref tileExists,
                    ref regionIds,
                    numTilesX,
                    numTilesY,
                    ref rng);

                // Mark path cells as alleys
                for (var i = 0; i < path.Length; i++)
                {
                    var idx = path[i].y * numTilesX + path[i].x;
                    tileExists[idx] = false;
                    roadHierarchy[idx] = (byte)RoadHierarchyLevel.Alley;
                    regionIds[idx] = 0; // No longer part of building region
                }
            }
        }

        path.Dispose();
    }

    /// <summary>
    /// Finds a cell on the region edge that borders a road, suitable for starting an alley.
    /// </summary>
    private static int2 FindEdgeStartPoint(
        BuildingRegion region,
        ref NativeArray<bool> tileExists,
        ref NativeArray<ushort> regionIds,
        int numTilesX,
        int numTilesY,
        ref Random rng)
    {
        var candidates = new NativeList<int2>(32, Allocator.Temp);

        // Scan the region bounds for edge cells that border roads
        for (var y = region.BoundsMin.y; y <= region.BoundsMax.y; y++)
        {
            for (var x = region.BoundsMin.x; x <= region.BoundsMax.x; x++)
            {
                var idx = y * numTilesX + x;

                // Must be part of this region
                if (regionIds[idx] != region.RegionId)
                    continue;

                // Check if any neighbor is a road (not a building)
                var bordersRoad = false;
                var neighbors = new[]
                {
                    new int2(x - 1, y),
                    new int2(x + 1, y),
                    new int2(x, y - 1),
                    new int2(x, y + 1)
                };

                foreach (var neighbor in neighbors)
                {
                    if (neighbor.x >= 0 && neighbor.x < numTilesX &&
                        neighbor.y >= 0 && neighbor.y < numTilesY)
                    {
                        var nIdx = neighbor.y * numTilesX + neighbor.x;
                        if (!tileExists[nIdx]) // Road cell
                        {
                            bordersRoad = true;
                            break;
                        }
                    }
                }

                if (bordersRoad)
                {
                    candidates.Add(new int2(x, y));
                }
            }
        }

        var result = new int2(-1, -1);

        if (candidates.Length > 0)
        {
            result = candidates[rng.NextInt(candidates.Length)];
        }

        candidates.Dispose();
        return result;
    }

    /// <summary>
    /// Generates an alley path using random walk from start toward the region centroid.
    /// May terminate early as a dead-end based on probability.
    /// </summary>
    private static void GenerateAlleyPath(
        int2 start,
        BuildingRegion region,
        float deadEndProb,
        int maxLength,
        ref NativeList<int2> path,
        ref NativeArray<bool> tileExists,
        ref NativeArray<ushort> regionIds,
        int numTilesX,
        int numTilesY,
        ref Random rng)
    {
        var current = start;
        var target = region.Centroid;

        // Random direction bias (toward centroid with some randomness)
        var generalDirection = math.normalize(target - new float2(current.x, current.y));

        for (var step = 0; step < maxLength; step++)
        {
            path.Add(current);

            // Check for dead-end termination
            if (step > 2 && rng.NextFloat() < deadEndProb)
                break;

            // Find valid next cell (must be part of the region)
            var bestNext = new int2(-1, -1);
            var bestScore = float.MinValue;

            var directions = new[]
            {
                new int2(1, 0),
                new int2(-1, 0),
                new int2(0, 1),
                new int2(0, -1)
            };

            foreach (var dir in directions)
            {
                var next = current + dir;

                // Check bounds
                if (next.x <= 0 || next.x >= numTilesX - 1 ||
                    next.y <= 0 || next.y >= numTilesY - 1)
                    continue;

                var nextIdx = next.y * numTilesX + next.x;

                // Must be a building cell in this region (can carve through)
                if (!tileExists[nextIdx])
                    continue;

                // Prefer cells that are part of this region or adjacent regions
                if (regionIds[nextIdx] != region.RegionId)
                    continue;

                // Don't revisit cells already in path
                var alreadyInPath = false;
                for (var p = 0; p < path.Length; p++)
                {
                    if (path[p].x == next.x && path[p].y == next.y)
                    {
                        alreadyInPath = true;
                        break;
                    }
                }
                if (alreadyInPath)
                    continue;

                // Score based on direction toward centroid + randomness
                var dirFloat = new float2(dir.x, dir.y);
                var directionScore = math.dot(dirFloat, generalDirection);
                var randomScore = rng.NextFloat(-0.5f, 0.5f);
                var score = directionScore + randomScore;

                if (score > bestScore)
                {
                    bestScore = score;
                    bestNext = next;
                }
            }

            if (bestNext.x < 0)
            {
                // No valid next cell, check if we reached another road (connection)
                break;
            }

            current = bestNext;

            // Check if we've reached the opposite side (connects to a road)
            var checkNeighbors = new[]
            {
                new int2(current.x - 1, current.y),
                new int2(current.x + 1, current.y),
                new int2(current.x, current.y - 1),
                new int2(current.x, current.y + 1)
            };

            foreach (var neighbor in checkNeighbors)
            {
                if (neighbor.x > 0 && neighbor.x < numTilesX - 1 &&
                    neighbor.y > 0 && neighbor.y < numTilesY - 1)
                {
                    var nIdx = neighbor.y * numTilesX + neighbor.x;
                    // If neighbor is a road and not where we came from
                    if (!tileExists[nIdx] && (neighbor.x != start.x || neighbor.y != start.y))
                    {
                        // We've connected to another road - complete the alley
                        path.Add(current);
                        return;
                    }
                }
            }
        }
    }
}
