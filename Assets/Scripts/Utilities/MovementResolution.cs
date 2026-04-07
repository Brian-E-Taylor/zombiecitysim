using Unity.Burst;
using Unity.Collections;
using Unity.Mathematics;

[BurstCompile]
public static class MovementResolution
{
    [BurstCompile]
    public static bool IsBlocked(uint key,
        in NativeParallelHashMap<uint, int> staticMap,
        in NativeParallelHashMap<uint, int> dynamicMap)
    {
        return staticMap.TryGetValue(key, out _) || dynamicMap.TryGetValue(key, out _);
    }

    [BurstCompile]
    public static void ComputeDirectionKeys(in int3 pos, out uint upKey, out uint rightKey,
        out uint downKey, out uint leftKey)
    {
        upKey = GridPositionHash.GetKey(pos.x, pos.z + 1);
        rightKey = GridPositionHash.GetKey(pos.x + 1, pos.z);
        downKey = GridPositionHash.GetKey(pos.x, pos.z - 1);
        leftKey = GridPositionHash.GetKey(pos.x - 1, pos.z);
    }

    /// <summary>
    /// Attempt to move along a single axis. Returns true if moved.
    /// dirComponent: the direction value on this axis (positive or negative).
    /// isXAxis: true = X axis, false = Z axis.
    /// negKey/posKey: hash keys for the negative/positive direction on this axis.
    /// </summary>
    [BurstCompile]
    public static bool TryMoveOnAxis(ref int3 pos, int dirComponent, bool isXAxis,
        uint negKey, uint posKey,
        ref bool negAvail, ref bool posAvail,
        ref bool negChecked, ref bool posChecked,
        in NativeParallelHashMap<uint, int> staticMap,
        in NativeParallelHashMap<uint, int> dynamicMap)
    {
        switch (dirComponent)
        {
            case < 0:
            {
                if (!negChecked)
                {
                    negChecked = true;
                    negAvail = !IsBlocked(negKey, staticMap, dynamicMap);
                }
                if (negAvail)
                {
                    if (isXAxis) pos.x--;
                    else pos.z--;
                    return true;
                }

                break;
            }
            case > 0:
            {
                if (!posChecked)
                {
                    posChecked = true;
                    posAvail = !IsBlocked(posKey, staticMap, dynamicMap);
                }
                if (posAvail)
                {
                    if (isXAxis) pos.x++;
                    else pos.z++;
                    return true;
                }

                break;
            }
        }

        return false;
    }

    /// <summary>
    /// Full 3-tier resolution for targeted movement (used by MoveTowardsHumansJob):
    /// 1. Primary axis (larger component of direction)
    /// 2. Secondary axis
    /// 3. Repeated primary axis attempt (currently a no-op if tier 1 already failed with the same
    ///    direction; to truly try the opposite, negate the direction component before calling)
    /// Returns true if moved. Also sets adjacentToTarget if target is 1 tile away.
    /// </summary>
    [BurstCompile]
    public static bool TryMoveTowardsTarget(ref int3 pos, in int3 direction,
        uint upKey, uint rightKey, uint downKey, uint leftKey,
        ref bool upAvail, ref bool rightAvail, ref bool downAvail, ref bool leftAvail,
        ref bool upChecked, ref bool rightChecked, ref bool downChecked, ref bool leftChecked,
        in NativeParallelHashMap<uint, int> staticMap,
        in NativeParallelHashMap<uint, int> dynamicMap,
        out bool adjacentToTarget)
    {
        adjacentToTarget = (math.abs(direction.x) == 1 && direction.z == 0) ||
                           (direction.x == 0 && math.abs(direction.z) == 1);

        bool moved;
        if (math.abs(direction.x) >= math.abs(direction.z))
        {
            // Primary: X, Secondary: Z
            moved = TryMoveOnAxis(ref pos, direction.x, true, leftKey, rightKey,
                ref leftAvail, ref rightAvail, ref leftChecked, ref rightChecked, staticMap, dynamicMap);
            if (!moved)
            {
                moved = TryMoveOnAxis(ref pos, direction.z, false, downKey, upKey,
                    ref downAvail, ref upAvail, ref downChecked, ref upChecked, staticMap, dynamicMap);
            }
            // Tier 3: try opposite primary
            if (!moved)
            {
                moved = TryMoveOnAxis(ref pos, direction.x, true, leftKey, rightKey,
                    ref leftAvail, ref rightAvail, ref leftChecked, ref rightChecked, staticMap, dynamicMap);
            }
        }
        else
        {
            // Primary: Z, Secondary: X
            moved = TryMoveOnAxis(ref pos, direction.z, false, downKey, upKey,
                ref downAvail, ref upAvail, ref downChecked, ref upChecked, staticMap, dynamicMap);
            if (!moved)
            {
                moved = TryMoveOnAxis(ref pos, direction.x, true, leftKey, rightKey,
                    ref leftAvail, ref rightAvail, ref leftChecked, ref rightChecked, staticMap, dynamicMap);
            }
            // Tier 3: try opposite secondary
            if (!moved)
            {
                moved = TryMoveOnAxis(ref pos, direction.z, false, downKey, upKey,
                    ref downAvail, ref upAvail, ref downChecked, ref upChecked, staticMap, dynamicMap);
            }
        }

        return moved;
    }

    /// <summary>
    /// Pre-checks all 4 directions then picks a random available one.
    /// Used by MoveRandomlyJob.
    /// </summary>
    [BurstCompile]
    public static void MoveRandomly(ref int3 pos, ref Random rng,
        in NativeParallelHashMap<uint, int> staticMap,
        in NativeParallelHashMap<uint, int> dynamicMap)
    {
        ComputeDirectionKeys(in pos, out var upKey, out var rightKey, out var downKey, out var leftKey);

        var upAvail = !IsBlocked(upKey, staticMap, dynamicMap);
        var rightAvail = !IsBlocked(rightKey, staticMap, dynamicMap);
        var downAvail = !IsBlocked(downKey, staticMap, dynamicMap);
        var leftAvail = !IsBlocked(leftKey, staticMap, dynamicMap);

        var randomDirIndex = rng.NextInt(0, 4);
        for (var i = 0; i < 4; i++)
        {
            switch ((randomDirIndex + i) % 4)
            {
                case 0: if (upAvail)    { pos.z += 1; return; } break;
                case 1: if (rightAvail) { pos.x += 1; return; } break;
                case 2: if (downAvail)  { pos.z -= 1; return; } break;
                case 3: if (leftAvail)  { pos.x -= 1; return; } break;
            }
        }
    }

    /// <summary>
    /// Random walk that reuses already-checked directions from a prior TryMoveTowardsTarget call.
    /// Used by MoveTowardsHumansJob as fallback when targeted movement fails.
    /// </summary>
    [BurstCompile]
    public static bool MoveRandomlyLazy(ref int3 pos, ref Random rng,
        uint upKey, uint rightKey, uint downKey, uint leftKey,
        ref bool upAvail, ref bool rightAvail, ref bool downAvail, ref bool leftAvail,
        ref bool upChecked, ref bool rightChecked, ref bool downChecked, ref bool leftChecked,
        in NativeParallelHashMap<uint, int> staticMap,
        in NativeParallelHashMap<uint, int> dynamicMap)
    {
        // Lazily check any unchecked directions
        if (!upChecked)    { upChecked = true;    upAvail = !IsBlocked(upKey, staticMap, dynamicMap); }
        if (!rightChecked) { rightChecked = true;  rightAvail = !IsBlocked(rightKey, staticMap, dynamicMap); }
        if (!downChecked)  { downChecked = true;   downAvail = !IsBlocked(downKey, staticMap, dynamicMap); }
        if (!leftChecked)  { leftChecked = true;   leftAvail = !IsBlocked(leftKey, staticMap, dynamicMap); }

        var randomDirIndex = rng.NextInt(0, 4);
        for (var i = 0; i < 4; i++)
        {
            switch ((randomDirIndex + i) % 4)
            {
                case 0: if (upAvail)    { pos.z += 1; return true; } break;
                case 1: if (rightAvail) { pos.x += 1; return true; } break;
                case 2: if (downAvail)  { pos.z -= 1; return true; } break;
                case 3: if (leftAvail)  { pos.x -= 1; return true; } break;
            }
        }
        return false;
    }
}
