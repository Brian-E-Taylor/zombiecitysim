using Unity.Burst;
using Unity.Collections;
using Unity.Mathematics;

[BurstCompile]
public static class LineOfSightUtilities
{
    [BurstCompile]
    public static bool InLineOfSight([ReadOnly] in int3 initialGridPosition, [ReadOnly] in int3 targetGridPosition, [ReadOnly] in NativeParallelHashMap<uint, int> staticCollidableHashMap)
    {
        float vx = targetGridPosition.x - initialGridPosition.x;
        float vz = targetGridPosition.z - initialGridPosition.z;
        var ox = targetGridPosition.x + 0.5f;
        var oz = targetGridPosition.z + 0.5f;
        var l = math.sqrt((vx * vx) + (vz * vz));
        vx /= l;
        vz /= l;
        for (var i = 0; i < (int)l; i++)
        {
            var gridPosition = new int3((int)math.floor(ox), initialGridPosition.y, (int)math.floor(oz));
            var key = math.hash(gridPosition);
            if (staticCollidableHashMap.TryGetValue(key, out _))
                return false;

            ox += vx;
            oz += vz;
        }

        return true;
    }

    [BurstCompile]
    public static bool InLineOfSightUpdated([ReadOnly] in int3 initialGridPosition, [ReadOnly] in int3 targetGridPosition, [ReadOnly] in NativeParallelHashMap<uint, int> staticCollidableHashMap)
    {
        var dx = math.abs(targetGridPosition.x - initialGridPosition.x);
        var dz = math.abs(targetGridPosition.z - initialGridPosition.z);
        var sx = targetGridPosition.x >= initialGridPosition.x ? 1 : -1;
        var sz = targetGridPosition.z >= initialGridPosition.z ? 1 : -1;

        var x = initialGridPosition.x;
        var z = initialGridPosition.z;

        if (dx > dz)
        {
            // X-dominant (more horizontal than vertical)
            var error = dx / 2;
            for (var i = 0; i <= dx; i++)
            {
                if (staticCollidableHashMap.TryGetValue(math.hash(new int3(x, initialGridPosition.y, z)), out _))
                    return false;

                if (i < dx)  // Don't step past target
                {
                    x += sx;
                    error -= dz;
                    if (error < 0)
                    {
                        z += sz;
                        error += dx;
                    }
                }
            }
        }
        else
        {
            // Z-dominant (more vertical than horizontal)
            var error = dz / 2;
            for (var i = 0; i <= dz; i++)
            {
                if (staticCollidableHashMap.TryGetValue(math.hash(new int3(x, initialGridPosition.y, z)), out _))
                    return false;

                if (i < dz)  // Don't step past target
                {
                    z += sz;
                    error -= dx;
                    if (error < 0)
                    {
                        x += sx;
                        error += dz;
                    }
                }
            }
        }

        return true;
    }
}
