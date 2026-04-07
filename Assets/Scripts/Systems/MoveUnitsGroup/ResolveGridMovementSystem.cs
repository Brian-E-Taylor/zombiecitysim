using Unity.Burst;
using Unity.Collections;
using Unity.Entities;

[BurstCompile]
public partial struct HashNextGridPositionsJob : IJobEntity
{
    public NativeParallelMultiHashMap<uint, int>.ParallelWriter ParallelWriter;

    public void Execute([EntityIndexInQuery] int entityIndexInQuery, [ReadOnly] in DesiredNextGridPosition desiredNextGridPosition)
    {
        var key = GridPositionHash.GetKey(desiredNextGridPosition.Value.x, desiredNextGridPosition.Value.z);
        ParallelWriter.Add(key, entityIndexInQuery);
    }
}

[BurstCompile]
public partial struct FinalizeMovementJob : IJobEntity
{
    [ReadOnly] public NativeParallelMultiHashMap<uint, int> NextGridPositionHashMap;

    public void Execute(ref DesiredNextGridPosition desiredNextGridPosition, [ReadOnly] in GridPosition gridPosition)
    {
        // Check for all units that wanted to move
        var key = GridPositionHash.GetKey(desiredNextGridPosition.Value.x, desiredNextGridPosition.Value.z);
        if (!NextGridPositionHashMap.TryGetFirstValue(key, out _, out var iter))
            return;

        // Don't allow movement if another unit has already claimed that grid space
        // (that unit is the first entry in the multi-hashmap)
        if (NextGridPositionHashMap.TryGetNextValue(out _, ref iter))
            desiredNextGridPosition.Value = gridPosition.Value;
    }
}

[UpdateInGroup(typeof(MoveUnitsGroup))]
[UpdateAfter(typeof(MoveZombiesSystem))]
[RequireMatchingQueriesForUpdate]
public partial struct ResolveGridMovementSystem : ISystem
{
    private EntityQuery _query;
    private NativeParallelMultiHashMap<uint, int> _nextGridPositionHashMap;

    private const int InitialPoolCapacity = 256;

    [BurstCompile]
    public void OnCreate(ref SystemState state)
    {
        state.RequireForUpdate<RunWorld>();

        _query = state.GetEntityQuery(new EntityQueryBuilder(Allocator.Temp)
            .WithAllRW<DesiredNextGridPosition>()
            .WithAll<GridPosition, TurnActive>());

        _nextGridPositionHashMap = new NativeParallelMultiHashMap<uint, int>(InitialPoolCapacity, Allocator.Persistent);
    }

    [BurstCompile]
    public void OnUpdate(ref SystemState state)
    {
        var unitCount = _query.CalculateEntityCount();
        var requiredCapacity = unitCount * 2;

        _nextGridPositionHashMap.Clear();
        if (_nextGridPositionHashMap.Capacity < requiredCapacity)
            _nextGridPositionHashMap.Capacity = requiredCapacity;

        state.Dependency = new HashNextGridPositionsJob { ParallelWriter = _nextGridPositionHashMap.AsParallelWriter() }.ScheduleParallel(_query, state.Dependency);
        state.Dependency = new FinalizeMovementJob { NextGridPositionHashMap = _nextGridPositionHashMap }.ScheduleParallel(_query, state.Dependency);
    }

    public void OnDestroy(ref SystemState state)
    {
        if (_nextGridPositionHashMap.IsCreated) _nextGridPositionHashMap.Dispose();
    }
}
