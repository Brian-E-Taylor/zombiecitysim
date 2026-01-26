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

        // Estimate required capacity based on unit counts and vision distances
        // Each zombie can check up to (2*visionDist+1)^2 positions for humans
        // Each human can check up to (2*visionDist+1)^2 positions for zombies
        // But many of these will be duplicates, so we use a fraction
        var zombieCount = _zombieQuery.CalculateEntityCount();
        var humanCount = _humanQuery.CalculateEntityCount();

        // Estimate: each unit might generate ~visionDistance unique LOS checks on average
        var zombieVisionChecks = zombieCount * gameController.zombieVisionDistance * 2;
        var humanVisionChecks = humanCount * gameController.humanVisionDistance * 2;
        var estimatedCapacity = math.max(MinCacheCapacity, (zombieVisionChecks + humanVisionChecks) * 2);

        // Create cache if it doesn't exist
        if (!losCacheComponent.ValueRO.Cache.IsCreated)
        {
            losCacheComponent.ValueRW.Cache = new NativeParallelHashMap<ulong, byte>(estimatedCapacity, Allocator.Persistent);
            losCacheComponent.ValueRW.IsValid = true;
            return;
        }

        var cache = losCacheComponent.ValueRO.Cache;

        // Clear cache each frame and resize if needed
        // This prevents unbounded growth and ensures sufficient capacity
        // The intra-frame caching still provides benefit when multiple units check the same positions
        if (cache.Capacity < estimatedCapacity)
        {
            // Need more capacity - dispose and recreate
            losCacheComponent.ValueRW.Cache.Dispose();
            losCacheComponent.ValueRW.Cache = new NativeParallelHashMap<ulong, byte>(estimatedCapacity, Allocator.Persistent);
        }
        else
        {
            // Clear for this frame's fresh LOS checks
            losCacheComponent.ValueRW.Cache.Clear();
        }

        losCacheComponent.ValueRW.IsValid = true;
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
