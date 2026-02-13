using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using Unity.Rendering;

[UpdateInGroup(typeof(DamageGroup))]
[RequireMatchingQueriesForUpdate]
public partial struct DamageToZombiesSystem : ISystem
{
    private EntityQuery _zombiesQuery;
    private EntityQuery _humansQuery;
    private float4 _zombieFullHealthColor;

    // Pooled hash map for damage accumulation (zombie positions come from shared HashZombiePositionsComponent)
    private NativeParallelMultiHashMap<uint, int> _damageHashMap;

    private const int InitialPoolCapacity = 256;

    [BurstCompile]
    public void OnCreate(ref SystemState state)
    {
        state.RequireForUpdate<RunWorld>();
        state.RequireForUpdate<HashZombiePositionsComponent>();

        _zombiesQuery = state.GetEntityQuery(new EntityQueryBuilder(Allocator.Temp)
            .WithAll<Zombie, MaxHealth, GridPosition>()
            .WithAllRW<Health, URPMaterialPropertyBaseColor>()
        );
        _humansQuery = state.GetEntityQuery(new EntityQueryBuilder(Allocator.Temp)
            .WithAll<Human, GridPosition, Damage, TurnActive>()
        );
        _zombieFullHealthColor = new float4();
        ZombieCreator.FillFullHealthColor(ref _zombieFullHealthColor);

        _damageHashMap = new NativeParallelMultiHashMap<uint, int>(InitialPoolCapacity, Allocator.Persistent);
    }

    [BurstCompile]
    public void OnUpdate(ref SystemState state)
    {
        var zombieCount = _zombiesQuery.CalculateEntityCount();
        var humanCount = _humansQuery.CalculateEntityCount();

        if (zombieCount == 0 || humanCount == 0)
            return;

        var zombiePositionsComponent = SystemAPI.GetSingleton<HashZombiePositionsComponent>();
        state.Dependency = JobHandle.CombineDependencies(state.Dependency, zombiePositionsComponent.Handle);

        var damageCapacity = (humanCount < zombieCount ? humanCount : zombieCount) * 8;
        _damageHashMap.Clear();
        if (_damageHashMap.Capacity < damageCapacity)
            _damageHashMap.Capacity = (int)(damageCapacity * 1.2f);

        state.Dependency = new CalculateDamageJob
        {
            DamageTakingHashMap = zombiePositionsComponent.HashMap,
            DamageAmountHashMapParallelWriter = _damageHashMap.AsParallelWriter()
        }.ScheduleParallel(_humansQuery, state.Dependency);

        state.Dependency = new DealDamageJob { DamageAmountHashMap = _damageHashMap, FullHealthColor = _zombieFullHealthColor }.ScheduleParallel(_zombiesQuery, state.Dependency);
    }

    public void OnDestroy(ref SystemState state)
    {
        if (_damageHashMap.IsCreated) _damageHashMap.Dispose();
    }
}
