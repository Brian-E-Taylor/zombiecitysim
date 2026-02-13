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

    /// <summary>
    /// Encodes a source-target position pair into a unique ulong key for LOS caching.
    /// The key is symmetric: GetLOSKey(A, B) == GetLOSKey(B, A), since line-of-sight
    /// traverses the same cells in both directions. The smaller key is always placed
    /// in the lower 32 bits to ensure this symmetry.
    /// </summary>
    [BurstCompile]
    public static ulong GetLOSKey(int sourceX, int sourceZ, int targetX, int targetZ)
    {
        var keyA = GetKey(sourceX, sourceZ);
        var keyB = GetKey(targetX, targetZ);
        // Normalize ordering so (A→B) and (B→A) produce the same key
        var lo = math.min(keyA, keyB);
        var hi = math.max(keyA, keyB);
        return (ulong)lo | ((ulong)hi << 32);
    }
}
