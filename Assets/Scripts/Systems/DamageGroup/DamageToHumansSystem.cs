using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using Unity.Rendering;

[UpdateInGroup(typeof(DamageGroup))]
[RequireMatchingQueriesForUpdate]
public partial struct DamageToHumansSystem : ISystem
{
    private EntityQuery _humansQuery;
    private EntityQuery _zombiesQuery;
    private float4 _humanFullHealthColor;

    // Pooled hash map for damage accumulation (human positions come from shared HashHumanPositionsComponent)
    private NativeParallelMultiHashMap<uint, int> _damageHashMap;

    private const int InitialPoolCapacity = 256;

    [BurstCompile]
    public void OnCreate(ref SystemState state)
    {
        state.RequireForUpdate<RunWorld>();
        state.RequireForUpdate<HashHumanPositionsComponent>();

        _humansQuery = state.GetEntityQuery(new EntityQueryBuilder(Allocator.Temp)
            .WithAll<Human, MaxHealth, GridPosition>()
            .WithAllRW<Health, URPMaterialPropertyBaseColor>()
        );
        _zombiesQuery = state.GetEntityQuery(new EntityQueryBuilder(Allocator.Temp)
            .WithAll<Zombie, GridPosition, Damage, TurnActive>()
        );
        _humanFullHealthColor = new float4();
        HumanCreator.FillFullHealthColor(ref _humanFullHealthColor);

        _damageHashMap = new NativeParallelMultiHashMap<uint, int>(InitialPoolCapacity, Allocator.Persistent);
    }

    [BurstCompile]
    public void OnUpdate(ref SystemState state)
    {
        var humanCount = _humansQuery.CalculateEntityCount();
        var zombieCount = _zombiesQuery.CalculateEntityCount();

        if (humanCount == 0 || zombieCount == 0)
            return;

        var humanPositionsComponent = SystemAPI.GetSingleton<HashHumanPositionsComponent>();
        state.Dependency = JobHandle.CombineDependencies(state.Dependency, humanPositionsComponent.Handle);

        var damageCapacity = (zombieCount < humanCount ? zombieCount : humanCount) * 8;
        _damageHashMap.Clear();
        if (_damageHashMap.Capacity < damageCapacity)
            _damageHashMap.Capacity = (int)(damageCapacity * 1.2f);

        state.Dependency = new CalculateDamageJob
        {
            DamageTakingHashMap = humanPositionsComponent.HashMap,
            DamageAmountHashMapParallelWriter = _damageHashMap.AsParallelWriter()
        }.ScheduleParallel(_zombiesQuery, state.Dependency);

        state.Dependency = new DealDamageJob { FullHealthColor = _humanFullHealthColor, DamageAmountHashMap = _damageHashMap }.ScheduleParallel(_humansQuery, state.Dependency);
    }

    public void OnDestroy(ref SystemState state)
    {
        if (_damageHashMap.IsCreated) _damageHashMap.Dispose();
    }
}
