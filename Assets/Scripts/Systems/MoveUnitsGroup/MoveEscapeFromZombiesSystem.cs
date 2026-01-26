using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;

[BurstCompile]
public partial struct MoveEscapeFromZombiesJob : IJobEntity
{
    public int VisionDistance;
    [ReadOnly] public NativeParallelHashMap<uint, int> HumanVisionHashMap;

    [ReadOnly] public NativeParallelHashMap<uint, int> ZombieHashMap;
    [ReadOnly] public NativeParallelHashMap<uint, int> StaticCollidablesHashMap;
    [ReadOnly] public NativeParallelHashMap<uint, int> DynamicCollidablesHashMap;

    // LOS cache for avoiding redundant line-of-sight calculations
    // NativeDisableContainerSafetyRestriction is safe here because:
    // - TryGetValue is a read-only operation
    // - TryAdd uses atomic operations and is thread-safe for concurrent writes
    [ReadOnly] public NativeParallelHashMap<ulong, byte> LOSCacheRead;
    [NativeDisableContainerSafetyRestriction]
    public NativeParallelHashMap<ulong, byte>.ParallelWriter LOSCacheWriter;

    public void Execute(ref DesiredNextGridPosition desiredNextGridPosition, [ReadOnly] in GridPosition gridPosition, [ReadOnly] in TurnActive turnActive)
    {
        var humanVisionHashMapCellSize = VisionDistance * 2 + 1;

        var myGridPositionValue = gridPosition.Value;
        var averageTarget = new float3(0, 0, 0);
        var targetCount = 0;
        var moved = false;

        var foundTarget = HumanVisionHashMap.TryGetValue(math.hash(new int3(myGridPositionValue.x - VisionDistance, myGridPositionValue.y, myGridPositionValue.z - VisionDistance) / humanVisionHashMapCellSize), out _) ||
                          HumanVisionHashMap.TryGetValue(math.hash(new int3(myGridPositionValue.x + VisionDistance, myGridPositionValue.y, myGridPositionValue.z - VisionDistance) / humanVisionHashMapCellSize), out _) ||
                          HumanVisionHashMap.TryGetValue(math.hash(new int3(myGridPositionValue.x - VisionDistance, myGridPositionValue.y, myGridPositionValue.z + VisionDistance) / humanVisionHashMapCellSize), out _) ||
                          HumanVisionHashMap.TryGetValue(math.hash(new int3(myGridPositionValue.x + VisionDistance, myGridPositionValue.y, myGridPositionValue.z + VisionDistance) / humanVisionHashMapCellSize), out _);

        if (foundTarget)
        {
            foundTarget = false;

            for (var checkDist = 1; checkDist <= VisionDistance && !foundTarget; checkDist++)
            {
                for (var z = -checkDist; z <= checkDist; z++)
                {
                    for (var x = -checkDist; x <= checkDist; x++)
                    {
                        if (math.abs(x) != checkDist && math.abs(z) != checkDist)
                            continue;

                        var targetGridPosition = new int3(myGridPositionValue.x + x, myGridPositionValue.y, myGridPositionValue.z + z);
                        var targetKey = GridPositionHash.GetKey(targetGridPosition.x, targetGridPosition.z);

                        if (!ZombieHashMap.TryGetValue(targetKey, out _))
                            continue;

                        // Check LOS cache first, compute and cache if miss
                        var losKey = GridPositionHash.GetLOSKey(myGridPositionValue.x, myGridPositionValue.z, targetGridPosition.x, targetGridPosition.z);
                        bool hasLOS;
                        if (LOSCacheRead.TryGetValue(losKey, out var cachedLOS))
                        {
                            hasLOS = cachedLOS == 1;
                        }
                        else
                        {
                            hasLOS = LineOfSightUtilities.InLineOfSightUpdated(myGridPositionValue, targetGridPosition, StaticCollidablesHashMap);
                            // Cache the result (TryAdd is safe from parallel writes - duplicates are ignored)
                            LOSCacheWriter.TryAdd(losKey, hasLOS ? (byte)1 : (byte)0);
                        }

                        if (!hasLOS)
                            continue;

                        averageTarget += new float3(x, 0, z);
                        targetCount++;

                        foundTarget = true;
                    }
                }
            }
        }

        if (foundTarget)
        {
            averageTarget /= targetCount;
            var direction = new int3((int)-averageTarget.x, (int)averageTarget.y, (int)-averageTarget.z);

            // Check if space is already occupied
            var moveLeftKey = GridPositionHash.GetKey(myGridPositionValue.x - 1, myGridPositionValue.z);
            var moveRightKey = GridPositionHash.GetKey(myGridPositionValue.x + 1, myGridPositionValue.z);
            var moveDownKey = GridPositionHash.GetKey(myGridPositionValue.x, myGridPositionValue.z - 1);
            var moveUpKey = GridPositionHash.GetKey(myGridPositionValue.x, myGridPositionValue.z + 1);
            if (math.abs(direction.x) >= math.abs(direction.z))
            {
                // Move horizontally
                if (direction.x < 0)
                {
                    if (!StaticCollidablesHashMap.TryGetValue(moveLeftKey, out _) &&
                        !DynamicCollidablesHashMap.TryGetValue(moveLeftKey, out _))
                    {
                        myGridPositionValue.x--;
                        moved = true;
                    }
                }
                else
                {
                    if (!StaticCollidablesHashMap.TryGetValue(moveRightKey, out _) &&
                        !DynamicCollidablesHashMap.TryGetValue(moveRightKey, out _))
                    {
                        myGridPositionValue.x++;
                        moved = true;
                    }
                }
            }
            // Unit maybe wanted to move horizontally but couldn't, so check if it wants to move vertically
            if (!moved)
            {
                // Move vertically
                if (direction.z < 0)
                {
                    if (!StaticCollidablesHashMap.TryGetValue(moveDownKey, out _) &&
                        !DynamicCollidablesHashMap.TryGetValue(moveDownKey, out _))
                    {
                        myGridPositionValue.z--;
                    }
                }
                else
                {
                    if (!StaticCollidablesHashMap.TryGetValue(moveUpKey, out _) &&
                        !DynamicCollidablesHashMap.TryGetValue(moveUpKey, out _))
                    {
                        myGridPositionValue.z++;
                    }
                }
            }
        }

        desiredNextGridPosition = new DesiredNextGridPosition { Value = myGridPositionValue };
    }
}

[UpdateInGroup(typeof(MoveUnitsGroup))]
[UpdateBefore(typeof(MoveTowardsHumansSystem))]
[RequireMatchingQueriesForUpdate]
public partial struct MoveEscapeFromZombiesSystem : ISystem
{
    private EntityQuery _zombieQuery;

    // Pooled hash maps to avoid per-frame allocations
    private NativeParallelHashMap<uint, int> _zombieHashMap;
    private NativeParallelHashMap<uint, int> _humanVisionHashMap;

    private const int InitialPoolCapacity = 256;

    public void OnCreate(ref SystemState state)
    {
        _zombieQuery = state.GetEntityQuery(new EntityQueryBuilder(Allocator.Temp).WithAll<Zombie, GridPosition>());

        // Initialize pooled hash maps
        _zombieHashMap = new NativeParallelHashMap<uint, int>(InitialPoolCapacity, Allocator.Persistent);
        _humanVisionHashMap = new NativeParallelHashMap<uint, int>(InitialPoolCapacity, Allocator.Persistent);

        state.RequireForUpdate<RunWorld>();
        state.RequireForUpdate<HashStaticCollidableSystemComponent>();
        state.RequireForUpdate<HashDynamicCollidableSystemComponent>();
        state.RequireForUpdate<GameControllerComponent>();
        state.RequireForUpdate<LOSCacheComponent>();
    }

    [BurstCompile]
    public void OnUpdate(ref SystemState state)
    {
        var staticCollidableComponent = SystemAPI.GetSingleton<HashStaticCollidableSystemComponent>();
        var dynamicCollidableComponent = SystemAPI.GetSingleton<HashDynamicCollidableSystemComponent>();
        var gameControllerComponent = SystemAPI.GetSingleton<GameControllerComponent>();

        state.Dependency = JobHandle.CombineDependencies(
            state.Dependency,
            SystemAPI.GetSingleton<HashStaticCollidableSystemComponent>().Handle,
            SystemAPI.GetSingleton<HashDynamicCollidableSystemComponent>().Handle
        );

        var staticCollidableHashMap = staticCollidableComponent.HashMap;
        var dynamicCollidableHashMap = dynamicCollidableComponent.HashMap;

        if (!staticCollidableHashMap.IsCreated || !dynamicCollidableHashMap.IsCreated)
            return;

        var zombieCount = _zombieQuery.CalculateEntityCount();

        // Clear and resize pooled hash maps instead of recreating
        _zombieHashMap.Clear();
        if (_zombieHashMap.Capacity < zombieCount)
            _zombieHashMap.Capacity = (int)(zombieCount * 1.2f);

        var hashMoveEscapeTargetGridPositionsJobHandle = new HashGridPositionsJob { ParallelWriter = _zombieHashMap.AsParallelWriter() }.ScheduleParallel(_zombieQuery, state.Dependency);

        var cellSize = gameControllerComponent.humanVisionDistance * 2 + 1;
        var cellCount = math.asint(math.ceil((float)gameControllerComponent.numTilesX / cellSize * gameControllerComponent.numTilesY / cellSize));
        var visionMapCapacity = cellCount < zombieCount ? cellCount : zombieCount;

        _humanVisionHashMap.Clear();
        if (_humanVisionHashMap.Capacity < visionMapCapacity)
            _humanVisionHashMap.Capacity = (int)(visionMapCapacity * 1.2f);

        var hashMoveEscapeTargetVisionJobHandle = new HashGridPositionsCellJob
        {
            CellSize = gameControllerComponent.humanVisionDistance * 2 + 1,
            ParallelWriter = _humanVisionHashMap.AsParallelWriter()
        }.ScheduleParallel(_zombieQuery, state.Dependency);

        state.Dependency = JobHandle.CombineDependencies(
            state.Dependency,
            hashMoveEscapeTargetGridPositionsJobHandle,
            hashMoveEscapeTargetVisionJobHandle
        );

        // Get LOS cache for this frame
        var losCacheComponent = SystemAPI.GetSingleton<LOSCacheComponent>();

        state.Dependency = new MoveEscapeFromZombiesJob
        {
            VisionDistance = gameControllerComponent.humanVisionDistance,
            HumanVisionHashMap = _humanVisionHashMap,

            ZombieHashMap = _zombieHashMap,
            StaticCollidablesHashMap = staticCollidableHashMap,
            DynamicCollidablesHashMap = dynamicCollidableHashMap,

            // LOS cache - read from existing cache, write new entries via parallel writer
            LOSCacheRead = losCacheComponent.Cache,
            LOSCacheWriter = losCacheComponent.Cache.AsParallelWriter(),
        }.ScheduleParallel(state.Dependency);

        // No need to dispose - using pooled hash maps
    }

    public void OnDestroy(ref SystemState state)
    {
        if (_zombieHashMap.IsCreated) _zombieHashMap.Dispose();
        if (_humanVisionHashMap.IsCreated) _humanVisionHashMap.Dispose();
    }
}
