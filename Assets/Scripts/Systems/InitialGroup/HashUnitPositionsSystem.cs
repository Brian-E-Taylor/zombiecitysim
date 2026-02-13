using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;

public struct HashHumanPositionsComponent : IComponentData
{
    public JobHandle Handle;
    public NativeParallelHashMap<uint, int> HashMap;
}

public struct HashZombiePositionsComponent : IComponentData
{
    public JobHandle Handle;
    public NativeParallelHashMap<uint, int> HashMap;
}

[UpdateInGroup(typeof(InitialGroup))]
[RequireMatchingQueriesForUpdate]
public partial struct HashUnitPositionsSystem : ISystem
{
    private EntityQuery _humanQuery;
    private EntityQuery _zombieQuery;

    // Pooled hash maps
    private NativeParallelHashMap<uint, int> _humanHashMap;
    private NativeParallelHashMap<uint, int> _zombieHashMap;

    private const int InitialPoolCapacity = 256;

    public void OnCreate(ref SystemState state)
    {
        _humanQuery = state.GetEntityQuery(new EntityQueryBuilder(Allocator.Temp)
            .WithAll<Human, GridPosition>());
        _zombieQuery = state.GetEntityQuery(new EntityQueryBuilder(Allocator.Temp)
            .WithAll<Zombie, GridPosition>());

        _humanHashMap = new NativeParallelHashMap<uint, int>(InitialPoolCapacity, Allocator.Persistent);
        _zombieHashMap = new NativeParallelHashMap<uint, int>(InitialPoolCapacity, Allocator.Persistent);

        state.RequireForUpdate<RunWorld>();
        state.RequireForUpdate<HashHumanPositionsComponent>();
        state.RequireForUpdate<HashZombiePositionsComponent>();
        state.RequireAnyForUpdate(_humanQuery, _zombieQuery);
    }

    [BurstCompile]
    public void OnUpdate(ref SystemState state)
    {
        var humanComponent = SystemAPI.GetSingletonRW<HashHumanPositionsComponent>();
        var zombieComponent = SystemAPI.GetSingletonRW<HashZombiePositionsComponent>();

        humanComponent.ValueRW.Handle = state.Dependency;
        zombieComponent.ValueRW.Handle = state.Dependency;

        // Hash human positions
        var humanCount = _humanQuery.CalculateEntityCount();
        _humanHashMap.Clear();
        if (_humanHashMap.Capacity < humanCount)
            _humanHashMap.Capacity = (int)(humanCount * 1.2f);

        if (humanCount > 0)
        {
            humanComponent.ValueRW.Handle = new HashGridPositionsJob
            {
                ParallelWriter = _humanHashMap.AsParallelWriter()
            }.ScheduleParallel(_humanQuery, state.Dependency);
        }

        humanComponent.ValueRW.HashMap = _humanHashMap;

        // Hash zombie positions
        var zombieCount = _zombieQuery.CalculateEntityCount();
        _zombieHashMap.Clear();
        if (_zombieHashMap.Capacity < zombieCount)
            _zombieHashMap.Capacity = (int)(zombieCount * 1.2f);

        if (zombieCount > 0)
        {
            zombieComponent.ValueRW.Handle = new HashGridPositionsJob
            {
                ParallelWriter = _zombieHashMap.AsParallelWriter()
            }.ScheduleParallel(_zombieQuery, state.Dependency);
        }

        zombieComponent.ValueRW.HashMap = _zombieHashMap;

        state.Dependency = JobHandle.CombineDependencies(humanComponent.ValueRO.Handle, zombieComponent.ValueRO.Handle);
    }

    public void OnDestroy(ref SystemState state)
    {
        if (_humanHashMap.IsCreated) _humanHashMap.Dispose();
        if (_zombieHashMap.IsCreated) _zombieHashMap.Dispose();
    }
}
