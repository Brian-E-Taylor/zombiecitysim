using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

/// <summary>
/// System that manages the Line-of-Sight cache lifecycle.
/// Creates the cache on first run, clears it when invalidated, and manages memory.
/// Runs before other systems that need to use the cache.
///
/// The cache is cleared each frame to prevent unbounded growth and ensure
/// the capacity is always sufficient for the current frame's LOS checks.
/// The main benefit of caching is within a single frame where multiple units
/// may check LOS to the same positions.
/// </summary>
[UpdateInGroup(typeof(InitialGroup))]
[UpdateAfter(typeof(HashCollidablesSystem))]
public partial struct LOSCacheSystem : ISystem
{
    private const int MinCacheCapacity = 8192;

    private EntityQuery _zombieQuery;
    private EntityQuery _humanQuery;

    public void OnCreate(ref SystemState state)
    {
        _zombieQuery = state.GetEntityQuery(new EntityQueryBuilder(Allocator.Temp)
            .WithAll<Zombie, GridPosition>());
        _humanQuery = state.GetEntityQuery(new EntityQueryBuilder(Allocator.Temp)
            .WithAll<Human, GridPosition>());

        state.RequireForUpdate<RunWorld>();
        state.RequireForUpdate<GameControllerComponent>();
    }

    [BurstCompile]
    public void OnUpdate(ref SystemState state)
    {
        if (!SystemAPI.HasSingleton<LOSCacheComponent>())
            return;

        var losCacheComponent = SystemAPI.GetSingletonRW<LOSCacheComponent>();
        var gameController = SystemAPI.GetSingleton<GameControllerComponent>();

        // Estimate required capacity for *new* entries this frame
        var zombieCount = _zombieQuery.CalculateEntityCount();
        var humanCount = _humanQuery.CalculateEntityCount();
        var zombieVisionChecks = zombieCount * gameController.zombieVisionDistance * 2;
        var humanVisionChecks = humanCount * gameController.humanVisionDistance * 2;
        var expectedNewEntries = (zombieVisionChecks + humanVisionChecks) * 2;

        // Create cache if it doesn't exist
        if (!losCacheComponent.ValueRO.Cache.IsCreated)
        {
            var initialCapacity = math.max(MinCacheCapacity, expectedNewEntries);
            losCacheComponent.ValueRW.Cache = new NativeParallelHashMap<ulong, byte>(initialCapacity, Allocator.Persistent);
            losCacheComponent.ValueRW.IsValid = true;
            return;
        }

        // If static collidables changed, clear the cache
        if (!losCacheComponent.ValueRO.IsValid)
        {
            losCacheComponent.ValueRW.Cache.Clear();
            losCacheComponent.ValueRW.IsValid = true;
        }

        var cache = losCacheComponent.ValueRO.Cache;
        var currentCount = cache.Count();
        var requiredCapacity = currentCount + expectedNewEntries;

        // Only resize and copy if we actually need more capacity
        if (cache.Capacity < requiredCapacity)
        {
            // Exponential growth or what's required, whichever is larger
            var newCapacity = math.max(cache.Capacity * 2, requiredCapacity);
            var newCache = new NativeParallelHashMap<ulong, byte>(newCapacity, Allocator.Persistent);

            // Copy existing data to the new larger cache
            foreach (var entry in cache)
            {
                newCache.TryAdd(entry.Key, entry.Value);
            }

            losCacheComponent.ValueRW.Cache.Dispose();
            losCacheComponent.ValueRW.Cache = newCache;
        }
    }

    public void OnDestroy(ref SystemState state)
    {
        if (SystemAPI.HasSingleton<LOSCacheComponent>())
        {
            var cache = SystemAPI.GetSingleton<LOSCacheComponent>().Cache;
            if (cache.IsCreated)
                cache.Dispose();
        }
    }
}
