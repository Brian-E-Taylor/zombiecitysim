using Unity.Burst;
using Unity.Mathematics;

/// <summary>
/// Provides collision-free position encoding for grid-based collision detection.
/// Uses direct bit packing instead of hashing to guarantee unique keys.
/// Supports grids up to 65536 x 65536 tiles.
/// </summary>
[BurstCompile]
public static class GridPositionHash
{
    /// <summary>
    /// Encodes a grid position into a unique uint key.
    /// Only uses x and z coordinates (y is assumed constant for 2D grid).
    /// </summary>
    [BurstCompile]
    public static uint GetKey(int x, int z)
    {
        // Pack x (lower 16 bits) and z (upper 16 bits) into uint
        // This is collision-free for grids up to 65536 x 65536
        return (uint)(x & 0xFFFF) | ((uint)(z & 0xFFFF) << 16);
    }
}
