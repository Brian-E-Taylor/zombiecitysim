using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;

[BurstCompile]
public partial struct MoveHumansJob : IJobEntity
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

    public void Execute(ref DesiredNextGridPosition desiredNextGridPosition, [ReadOnly] in GridPosition gridPosition, [ReadOnly] in TurnActive turnActive, [ReadOnly] in Human human)
    {
        var humanVisionHashMapCellSize = VisionDistance * 2 + 1;

        var myGridPositionValue = gridPosition.Value;
        var averageTarget = new float3(0, 0, 0);
        var targetCount = 0;
        var moved = false;

        // Broadphase early-rejection: check the four corner cells of the vision bounding box.
        // If no zombie occupies any of those coarse cells, skip the expensive per-tile ring scan.
        var foundTarget = HumanVisionHashMap.TryGetValue(math.hash(new int3(myGridPositionValue.x - VisionDistance, myGridPositionValue.y, myGridPositionValue.z - VisionDistance) / humanVisionHashMapCellSize), out _) ||
                          HumanVisionHashMap.TryGetValue(math.hash(new int3(myGridPositionValue.x + VisionDistance, myGridPositionValue.y, myGridPositionValue.z - VisionDistance) / humanVisionHashMapCellSize), out _) ||
                          HumanVisionHashMap.TryGetValue(math.hash(new int3(myGridPositionValue.x - VisionDistance, myGridPositionValue.y, myGridPositionValue.z + VisionDistance) / humanVisionHashMapCellSize), out _) ||
                          HumanVisionHashMap.TryGetValue(math.hash(new int3(myGridPositionValue.x + VisionDistance, myGridPositionValue.y, myGridPositionValue.z + VisionDistance) / humanVisionHashMapCellSize), out _);

        if (foundTarget)
        {
            foundTarget = false;

            for (var checkDist = 1; checkDist <= VisionDistance; checkDist++)
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
            var direction = new int3((int)math.sign(-averageTarget.x), 0, (int)math.sign(-averageTarget.z));

            MovementResolution.ComputeDirectionKeys(myGridPositionValue, out var moveUpKey, out var moveRightKey, out var moveDownKey, out var moveLeftKey);

            var upAvail = true;    var upChecked = false;
            var rightAvail = true;  var rightChecked = false;
            var downAvail = true;   var downChecked = false;
            var leftAvail = true;   var leftChecked = false;

            // Try primary axis (X only when it's the dominant direction)
            if (math.abs(direction.x) >= math.abs(direction.z))
            {
                moved = MovementResolution.TryMoveOnAxis(ref myGridPositionValue, direction.x, true,
                    moveLeftKey, moveRightKey,
                    ref leftAvail, ref rightAvail, ref leftChecked, ref rightChecked,
                    StaticCollidablesHashMap, DynamicCollidablesHashMap);
            }

            // Try secondary axis (Z)
            if (!moved)
            {
                MovementResolution.TryMoveOnAxis(ref myGridPositionValue, direction.z, false,
                    moveDownKey, moveUpKey,
                    ref downAvail, ref upAvail, ref downChecked, ref upChecked,
                    StaticCollidablesHashMap, DynamicCollidablesHashMap);
            }
        }

        desiredNextGridPosition = new DesiredNextGridPosition { Value = myGridPositionValue };
    }
}

[UpdateInGroup(typeof(MoveUnitsGroup))]
[UpdateBefore(typeof(MoveZombiesSystem))]
[RequireMatchingQueriesForUpdate]
public partial struct MoveHumansSystem : ISystem
{
    private EntityQuery _zombieQuery;

    // Pooled hash map for zombie coarse-cell vision lookups, built each frame from _zombieQuery.
    // Exact zombie positions for per-tile checks come from HashZombiePositionsComponent (ZombieHashMap).
    private NativeParallelHashMap<uint, int> _humanVisionHashMap;

    private const int InitialPoolCapacity = 256;

    public void OnCreate(ref SystemState state)
    {
        _zombieQuery = state.GetEntityQuery(new EntityQueryBuilder(Allocator.Temp).WithAll<Zombie, GridPosition>());

        _humanVisionHashMap = new NativeParallelHashMap<uint, int>(InitialPoolCapacity, Allocator.Persistent);

        state.RequireForUpdate<RunWorld>();
        state.RequireForUpdate<HashStaticCollidableSystemComponent>();
        state.RequireForUpdate<HashDynamicCollidableSystemComponent>();
        state.RequireForUpdate<GameControllerComponent>();
        state.RequireForUpdate<LOSCacheComponent>();
        state.RequireForUpdate<HashZombiePositionsComponent>();
    }

    [BurstCompile]
    public void OnUpdate(ref SystemState state)
    {
        var staticCollidableComponent = SystemAPI.GetSingleton<HashStaticCollidableSystemComponent>();
        var dynamicCollidableComponent = SystemAPI.GetSingleton<HashDynamicCollidableSystemComponent>();
        var gameControllerComponent = SystemAPI.GetSingleton<GameControllerComponent>();
        var zombiePositionsComponent = SystemAPI.GetSingleton<HashZombiePositionsComponent>();

        state.Dependency = JobHandle.CombineDependencies(
            state.Dependency,
            staticCollidableComponent.Handle,
            dynamicCollidableComponent.Handle
        );
        state.Dependency = JobHandle.CombineDependencies(state.Dependency, zombiePositionsComponent.Handle);

        var staticCollidableHashMap = staticCollidableComponent.HashMap;
        var dynamicCollidableHashMap = dynamicCollidableComponent.HashMap;

        if (!staticCollidableHashMap.IsCreated || !dynamicCollidableHashMap.IsCreated)
            return;

        var zombieCount = _zombieQuery.CalculateEntityCount();

        var cellSize = gameControllerComponent.humanVisionDistance * 2 + 1;
        var cellCount = math.asint(math.ceil((float)gameControllerComponent.numTilesX / cellSize * gameControllerComponent.numTilesY / cellSize));
        var visionMapCapacity = cellCount < zombieCount ? cellCount : zombieCount;

        _humanVisionHashMap.Clear();
        if (_humanVisionHashMap.Capacity < visionMapCapacity)
            _humanVisionHashMap.Capacity = (int)(visionMapCapacity * 1.2f);

        var humansVisionHandle = new HashGridPositionsCellJob
        {
            CellSize = cellSize,
            ParallelWriter = _humanVisionHashMap.AsParallelWriter()
        }.ScheduleParallel(_zombieQuery, state.Dependency);

        state.Dependency = JobHandle.CombineDependencies(
            state.Dependency,
            humansVisionHandle
        );

        // Get LOS cache for this frame
        var losCacheComponent = SystemAPI.GetSingleton<LOSCacheComponent>();

        state.Dependency = new MoveHumansJob
        {
            VisionDistance = gameControllerComponent.humanVisionDistance,
            HumanVisionHashMap = _humanVisionHashMap,

            ZombieHashMap = zombiePositionsComponent.HashMap,
            StaticCollidablesHashMap = staticCollidableHashMap,
            DynamicCollidablesHashMap = dynamicCollidableHashMap,

            // LOS cache - read from existing cache, write new entries via parallel writer
            LOSCacheRead = losCacheComponent.Cache,
            LOSCacheWriter = losCacheComponent.Cache.AsParallelWriter(),
        }.ScheduleParallel(state.Dependency);
    }

    public void OnDestroy(ref SystemState state)
    {
        if (_humanVisionHashMap.IsCreated) _humanVisionHashMap.Dispose();
    }
}
