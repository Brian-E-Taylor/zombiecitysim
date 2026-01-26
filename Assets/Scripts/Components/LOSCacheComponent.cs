using Unity.Collections;
using Unity.Entities;

/// <summary>
/// Singleton component that holds the Line-of-Sight cache.
/// The cache stores LOS results keyed by (source_position, target_position) pairs.
/// Cache is invalidated when static collidables change.
/// </summary>
public struct LOSCacheComponent : IComponentData
{
    /// <summary>
    /// Cache of LOS results. Key is packed (source, target) positions, value is 1 for visible, 0 for blocked.
    /// </summary>
    public NativeParallelHashMap<ulong, byte> Cache;

    /// <summary>
    /// Tracks whether the cache is valid. Set to false when static collidables change.
    /// </summary>
    public bool IsValid;
}
