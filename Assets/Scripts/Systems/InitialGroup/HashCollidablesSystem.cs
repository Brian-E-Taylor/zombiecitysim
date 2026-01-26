using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;

public struct HashStaticCollidableSystemComponent : IComponentData
{
    public JobHandle Handle;
    public NativeParallelHashMap<uint, int> HashMap;
}

public struct HashDynamicCollidableSystemComponent : IComponentData
{
    public JobHandle Handle;
    public NativeParallelHashMap<uint, int> HashMap;
}

[UpdateInGroup(typeof(InitialGroup))]
[RequireMatchingQueriesForUpdate]
public partial struct HashCollidablesSystem : ISystem
{
    private EntityQuery _staticCollidableEntityQuery;
    private EntityQuery _staticCollidableChangedQuery;
    private EntityQuery _dynamicCollidableEntityQuery;

    public void OnCreate(ref SystemState state)
    {
        // Query without change filter - used for full count and hashing all entities
        _staticCollidableEntityQuery = state.GetEntityQuery(new EntityQueryBuilder(Allocator.Temp)
            .WithAll<StaticCollidable, GridPosition>());

        // Query with change filter - used to detect if any static collidables changed
        _staticCollidableChangedQuery = state.GetEntityQuery(new EntityQueryBuilder(Allocator.Temp)
            .WithAll<StaticCollidable, GridPosition>());
        _staticCollidableChangedQuery.AddChangedVersionFilter(typeof(GridPosition));

        _dynamicCollidableEntityQuery = state.GetEntityQuery(new EntityQueryBuilder(Allocator.Temp)
            .WithAll<DynamicCollidable, GridPosition>());

        state.RequireForUpdate<HashDynamicCollidableSystemComponent>();
        state.RequireForUpdate<HashStaticCollidableSystemComponent>();
        state.RequireAnyForUpdate(_staticCollidableEntityQuery, _dynamicCollidableEntityQuery);
    }

    [BurstCompile]
    public void OnUpdate(ref SystemState state)
    {
        var hashStaticCollidableSystemComponent = SystemAPI.GetSingletonRW<HashStaticCollidableSystemComponent>();
        var hashDynamicCollidableSystemComponent = SystemAPI.GetSingletonRW<HashDynamicCollidableSystemComponent>();

        hashStaticCollidableSystemComponent.ValueRW.Handle = state.Dependency;
        hashDynamicCollidableSystemComponent.ValueRW.Handle = state.Dependency;

        // Only rebuild static hash map if:
        // 1. It doesn't exist yet, OR
        // 2. Static collidables have changed (new entities added, positions modified, etc.)
        var existingStaticHashMap = hashStaticCollidableSystemComponent.ValueRO.HashMap;
        var staticCollidablesChanged = !_staticCollidableChangedQuery.IsEmpty;
        var needsStaticRebuild = !existingStaticHashMap.IsCreated || staticCollidablesChanged;

        if (needsStaticRebuild)
        {
            var staticCollidableCount = _staticCollidableEntityQuery.CalculateEntityCount();
            if (staticCollidableCount != 0)
            {
                if (existingStaticHashMap.IsCreated)
                    SystemAPI.GetSingletonRW<HashStaticCollidableSystemComponent>().ValueRW.HashMap.Dispose();

                var hashMap = new NativeParallelHashMap<uint, int>(staticCollidableCount, Allocator.Persistent);
                hashStaticCollidableSystemComponent.ValueRW.Handle = new HashGridPositionsJob
                {
                    ParallelWriter = hashMap.AsParallelWriter()
                }.ScheduleParallel(_staticCollidableEntityQuery, state.Dependency);

                SystemAPI.GetSingletonRW<HashStaticCollidableSystemComponent>().ValueRW.HashMap = hashMap;
            }

            // Invalidate the LOS cache since static collidables changed
            if (SystemAPI.HasSingleton<LOSCacheComponent>())
            {
                SystemAPI.GetSingletonRW<LOSCacheComponent>().ValueRW.IsValid = false;
            }
        }

        // Dynamic collidables always need rebuilding each frame since units move
        var dynamicCollidableCount = _dynamicCollidableEntityQuery.CalculateEntityCount();
        if (dynamicCollidableCount != 0)
        {
            if (SystemAPI.GetSingletonRW<HashDynamicCollidableSystemComponent>().ValueRO.HashMap.IsCreated)
                SystemAPI.GetSingletonRW<HashDynamicCollidableSystemComponent>().ValueRW.HashMap.Dispose();

            var hashMap = new NativeParallelHashMap<uint, int>(dynamicCollidableCount, Allocator.Persistent);
            hashDynamicCollidableSystemComponent.ValueRW.Handle = new HashGridPositionsJob
            {
                ParallelWriter = hashMap.AsParallelWriter()
            }.ScheduleParallel(_dynamicCollidableEntityQuery, state.Dependency);

            SystemAPI.GetSingletonRW<HashDynamicCollidableSystemComponent>().ValueRW.HashMap = hashMap;
        }

        state.Dependency = JobHandle.CombineDependencies(hashStaticCollidableSystemComponent.ValueRO.Handle, hashDynamicCollidableSystemComponent.ValueRO.Handle);
    }

    public void OnDestroy(ref SystemState state)
    {
        var staticCollidableHashMap = SystemAPI.GetSingleton<HashStaticCollidableSystemComponent>().HashMap;
        if (staticCollidableHashMap.IsCreated)
            staticCollidableHashMap.Dispose();

        var dynamicCollidableHashMap = SystemAPI.GetSingleton<HashDynamicCollidableSystemComponent>().HashMap;
        if (dynamicCollidableHashMap.IsCreated)
            dynamicCollidableHashMap.Dispose();
    }
}
