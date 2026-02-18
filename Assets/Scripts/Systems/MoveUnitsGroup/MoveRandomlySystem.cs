using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;

[BurstCompile]
public partial struct MoveRandomlyJob : IJobEntity
{
    [ReadOnly] public NativeParallelHashMap<uint, int> StaticCollidableHashMap;
    [ReadOnly] public NativeParallelHashMap<uint, int> DynamicCollidableHashMap;

    public void Execute(ref DesiredNextGridPosition desiredNextGridPosition, ref RandomGenerator random, [ReadOnly] in GridPosition gridPosition)
    {
        var rng = random.Value;
        var pos = gridPosition.Value;
        MovementResolution.MoveRandomly(ref pos, ref rng, StaticCollidableHashMap, DynamicCollidableHashMap);
        desiredNextGridPosition = new DesiredNextGridPosition { Value = pos };
        random.Value = rng;
    }
}

[UpdateInGroup(typeof(MoveUnitsGroup))]
[UpdateBefore(typeof(MoveTowardsHumansSystem))]
[RequireMatchingQueriesForUpdate]
public partial struct MoveRandomlySystem : ISystem
{
    private EntityQuery _moveRandomlyQuery;

    [BurstCompile]
    public void OnCreate(ref SystemState state)
    {
        _moveRandomlyQuery = state.GetEntityQuery(new EntityQueryBuilder(Allocator.Temp).WithAll<MoveRandomly, TurnActive>());

        state.RequireForUpdate<RunWorld>();
        state.RequireForUpdate<HashStaticCollidableSystemComponent>();
        state.RequireForUpdate<HashDynamicCollidableSystemComponent>();
        state.RequireForUpdate(_moveRandomlyQuery);
    }

    [BurstCompile]
    public void OnUpdate(ref SystemState state)
    {
        var staticCollidableComponent = SystemAPI.GetSingleton<HashStaticCollidableSystemComponent>();
        var dynamicCollidableComponent = SystemAPI.GetSingleton<HashDynamicCollidableSystemComponent>();

        state.Dependency = JobHandle.CombineDependencies(state.Dependency, staticCollidableComponent.Handle, dynamicCollidableComponent.Handle);

        var staticCollidableHashMap = staticCollidableComponent.HashMap;
        var dynamicCollidableHashMap = dynamicCollidableComponent.HashMap;

        if (!staticCollidableHashMap.IsCreated || !dynamicCollidableHashMap.IsCreated)
            return;

        state.Dependency = new MoveRandomlyJob
        {
            StaticCollidableHashMap = staticCollidableHashMap,
            DynamicCollidableHashMap = dynamicCollidableHashMap
        }.ScheduleParallel(_moveRandomlyQuery, state.Dependency);
    }
}
