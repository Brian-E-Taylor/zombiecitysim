using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;

[BurstCompile]
public partial struct MoveTowardsHumansJob : IJobEntity
{
    public EntityCommandBuffer.ParallelWriter Ecb;

    public int HearingDistance;
    [ReadOnly] public NativeParallelHashMap<uint, int> ZombieHearingHashMap;
    public int VisionDistance;
    [ReadOnly] public NativeParallelHashMap<uint, int> ZombieVisionHashMap;

    [ReadOnly] public NativeParallelHashMap<uint, int> HumanHashMap;
    [ReadOnly] public NativeParallelMultiHashMap<uint, int3> AudibleHashMap;
    [ReadOnly] public NativeParallelHashMap<uint, int> StaticCollidablesHashMap;
    [ReadOnly] public NativeParallelHashMap<uint, int> DynamicCollidablesHashMap;

    // LOS cache for avoiding redundant line-of-sight calculations
    // NativeDisableContainerSafetyRestriction is safe here because:
    // - TryGetValue is a read-only operation
    // - TryAdd uses atomic operations and is thread-safe for concurrent writes
    [ReadOnly] public NativeParallelHashMap<ulong, byte> LOSCacheRead;
    [NativeDisableContainerSafetyRestriction]
    public NativeParallelHashMap<ulong, byte>.ParallelWriter LOSCacheWriter;

    public void Execute([EntityIndexInQuery] int entityIndexInQuery, ref DesiredNextGridPosition desiredNextGridPosition, ref RandomGenerator random, [ReadOnly] in GridPosition gridPosition)
    {
        var zombieHearingHashMapCellSize = HearingDistance * 2 + 1;
        var zombieVisionHashMapCellSize = VisionDistance * 2 + 1;

        var myGridPositionValue = gridPosition.Value;
        var nearestTarget = myGridPositionValue;
        var moved = false;
        var foundTarget = false;
        var foundBySight = ZombieVisionHashMap.TryGetValue(math.hash(new int3(myGridPositionValue.x - VisionDistance, myGridPositionValue.y, myGridPositionValue.z - VisionDistance) / zombieVisionHashMapCellSize), out _) ||
                           ZombieVisionHashMap.TryGetValue(math.hash(new int3(myGridPositionValue.x + VisionDistance, myGridPositionValue.y, myGridPositionValue.z - VisionDistance) / zombieVisionHashMapCellSize), out _) ||
                           ZombieVisionHashMap.TryGetValue(math.hash(new int3(myGridPositionValue.x - VisionDistance, myGridPositionValue.y, myGridPositionValue.z + VisionDistance) / zombieVisionHashMapCellSize), out _) ||
                           ZombieVisionHashMap.TryGetValue(math.hash(new int3(myGridPositionValue.x + VisionDistance, myGridPositionValue.y, myGridPositionValue.z + VisionDistance) / zombieVisionHashMapCellSize), out _);

        if (foundBySight)
        {
            foundBySight = false;

            // Get nearest target
            // Check all grid positions that are checkDist away in the x or y direction
            for (var checkDist = 1; checkDist <= VisionDistance && !foundTarget; checkDist++)
            {
                float nearestDistance = (checkDist + 2) * (checkDist + 2);
                for (var z = -checkDist; z <= checkDist; z++)
                {
                    for (var x = -checkDist; x <= checkDist; x++)
                    {
                        if (math.abs(x) != checkDist && math.abs(z) != checkDist)
                            continue;

                        var targetGridPosition = new int3(myGridPositionValue.x + x, myGridPositionValue.y, myGridPositionValue.z + z);
                        var targetKey = GridPositionHash.GetKey(targetGridPosition.x, targetGridPosition.z);

                        if (checkDist > VisionDistance || !HumanHashMap.TryGetValue(targetKey, out _))
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

                        var distance = math.lengthsq(new float3(myGridPositionValue) - new float3(targetGridPosition));
                        var nearest = distance < nearestDistance;

                        nearestDistance = math.select(nearestDistance, distance, nearest);
                        nearestTarget = math.select(nearestTarget, targetGridPosition, nearest);

                        foundBySight = true;
                        foundTarget = true;
                    }
                }
            }
        }

        if (!foundBySight)
        {
            var foundByHearing = ZombieHearingHashMap.TryGetValue(math.hash(new int3(myGridPositionValue.x - HearingDistance, myGridPositionValue.y, myGridPositionValue.z - HearingDistance) / zombieHearingHashMapCellSize), out _) ||
                                 ZombieHearingHashMap.TryGetValue(math.hash(new int3(myGridPositionValue.x + HearingDistance, myGridPositionValue.y, myGridPositionValue.z - HearingDistance) / zombieHearingHashMapCellSize), out _) ||
                                 ZombieHearingHashMap.TryGetValue(math.hash(new int3(myGridPositionValue.x - HearingDistance, myGridPositionValue.y, myGridPositionValue.z + HearingDistance) / zombieHearingHashMapCellSize), out _) ||
                                 ZombieHearingHashMap.TryGetValue(math.hash(new int3(myGridPositionValue.x + HearingDistance, myGridPositionValue.y, myGridPositionValue.z + HearingDistance) / zombieHearingHashMapCellSize), out _);

            if (foundByHearing)
            {
                // Get nearest target
                // Check all grid positions that are checkDist away in the x or y direction
                for (var checkDist = 1; checkDist <= HearingDistance && !foundTarget; checkDist++)
                {
                    float nearestDistance = (checkDist + 2) * (checkDist + 2);
                    for (var z = -checkDist; z <= checkDist; z++)
                    {
                        for (var x = -checkDist; x <= checkDist; x++)
                        {
                            if (math.abs(x) != checkDist && math.abs(z) != checkDist)
                                continue;

                            var targetGridPosition = new int3(myGridPositionValue.x + x, myGridPositionValue.y, myGridPositionValue.z + z);
                            var targetKey = GridPositionHash.GetKey(targetGridPosition.x, targetGridPosition.z);

                            if (checkDist > HearingDistance || !AudibleHashMap.TryGetFirstValue(targetKey, out var audibleTarget, out _))
                                continue;

                            var distance = math.lengthsq(new float3(myGridPositionValue) - new float3(targetGridPosition));
                            var nearest = distance < nearestDistance;

                            nearestDistance = math.select(nearestDistance, distance, nearest);
                            nearestTarget = math.select(nearestTarget, audibleTarget, nearest);

                            foundTarget = true;
                        }
                    }
                }
            }
        }

        var upAvail = true;    var upChecked = false;
        var rightAvail = true;  var rightChecked = false;
        var downAvail = true;   var downChecked = false;
        var leftAvail = true;   var leftChecked = false;

        MovementResolution.ComputeDirectionKeys(myGridPositionValue, out var moveUpKey, out var moveRightKey, out var moveDownKey, out var moveLeftKey);

        if (foundTarget)
        {
            var direction = nearestTarget - myGridPositionValue;
            moved = MovementResolution.TryMoveTowardsTarget(ref myGridPositionValue, direction,
                moveUpKey, moveRightKey, moveDownKey, moveLeftKey,
                ref upAvail, ref rightAvail, ref downAvail, ref leftAvail,
                ref upChecked, ref rightChecked, ref downChecked, ref leftChecked,
                StaticCollidablesHashMap, DynamicCollidablesHashMap,
                out var adjacentToTarget) || adjacentToTarget;
        }

        if (!moved)
        {
            var rng = random.Value;
            MovementResolution.MoveRandomlyLazy(ref myGridPositionValue, ref rng,
                moveUpKey, moveRightKey, moveDownKey, moveLeftKey,
                ref upAvail, ref rightAvail, ref downAvail, ref leftAvail,
                ref upChecked, ref rightChecked, ref downChecked, ref leftChecked,
                StaticCollidablesHashMap, DynamicCollidablesHashMap);
            random.Value = rng;
        }

        if (foundBySight)
        {
            var audibleEntity = Ecb.CreateEntity(entityIndexInQuery);
            Ecb.AddComponent(entityIndexInQuery, audibleEntity, new Audible { GridPositionValue = myGridPositionValue, Target = nearestTarget, Age = 0 });
        }

        desiredNextGridPosition = new DesiredNextGridPosition { Value = myGridPositionValue };
    }
}

[UpdateInGroup(typeof(MoveUnitsGroup))]
[RequireMatchingQueriesForUpdate]
public partial struct MoveTowardsHumansSystem : ISystem
{
    private EntityQuery _moveTowardsHumanQuery;
    private EntityQuery _humanQuery;
    private EntityQuery _audibleQuery;

    // Pooled hash maps for vision/hearing cell lookups and audible data
    private NativeParallelHashMap<uint, int> _zombieVisionHashMap;
    private NativeParallelMultiHashMap<uint, int3> _audibleHashMap;
    private NativeParallelHashMap<uint, int> _zombieHearingHashMap;

    private const int InitialPoolCapacity = 256;

    public void OnCreate(ref SystemState state)
    {
        _moveTowardsHumanQuery = state.GetEntityQuery(new EntityQueryBuilder(Allocator.Temp)
            .WithAll<GridPosition, MoveTowardsHuman, TurnActive>()
            .WithAllRW<DesiredNextGridPosition, RandomGenerator>()
        );
        _humanQuery = state.GetEntityQuery(new EntityQueryBuilder(Allocator.Temp).WithAll<Human, GridPosition>());
        _audibleQuery = state.GetEntityQuery(new EntityQueryBuilder(Allocator.Temp).WithAll<Audible>());

        // Initialize pooled hash maps (human positions come from shared HashHumanPositionsComponent)
        _zombieVisionHashMap = new NativeParallelHashMap<uint, int>(InitialPoolCapacity, Allocator.Persistent);
        _audibleHashMap = new NativeParallelMultiHashMap<uint, int3>(InitialPoolCapacity, Allocator.Persistent);
        _zombieHearingHashMap = new NativeParallelHashMap<uint, int>(InitialPoolCapacity, Allocator.Persistent);

        state.RequireForUpdate<RunWorld>();
        state.RequireForUpdate<BeginInitializationEntityCommandBufferSystem.Singleton>();
        state.RequireForUpdate<HashStaticCollidableSystemComponent>();
        state.RequireForUpdate<HashDynamicCollidableSystemComponent>();
        state.RequireForUpdate<GameControllerComponent>();
        state.RequireForUpdate<LOSCacheComponent>();
        state.RequireForUpdate<HashHumanPositionsComponent>();
        state.RequireForUpdate(_moveTowardsHumanQuery);
        state.RequireAnyForUpdate(_humanQuery, _audibleQuery);
    }

    [BurstCompile]
    public void OnUpdate(ref SystemState state)
    {
        var staticCollidableComponent = SystemAPI.GetSingleton<HashStaticCollidableSystemComponent>();
        var dynamicCollidableComponent = SystemAPI.GetSingleton<HashDynamicCollidableSystemComponent>();
        var gameControllerComponent = SystemAPI.GetSingleton<GameControllerComponent>();
        var humanPositionsComponent = SystemAPI.GetSingleton<HashHumanPositionsComponent>();

        state.Dependency = JobHandle.CombineDependencies(state.Dependency, staticCollidableComponent.Handle, dynamicCollidableComponent.Handle);
        state.Dependency = JobHandle.CombineDependencies(state.Dependency, humanPositionsComponent.Handle);

        var cellSize = gameControllerComponent.zombieVisionDistance * 2 + 1;
        var cellCount = math.asint(math.ceil((float)gameControllerComponent.numTilesX / cellSize * gameControllerComponent.numTilesY / cellSize));
        var humanCount = _humanQuery.CalculateEntityCount();

        var visionMapCapacity = cellCount < humanCount ? cellCount : humanCount;
        _zombieVisionHashMap.Clear();
        if (_zombieVisionHashMap.Capacity < visionMapCapacity)
            _zombieVisionHashMap.Capacity = (int)(visionMapCapacity * 1.2f);

        var hashFollowTargetVisionJobHandle = state.Dependency;
        if (humanCount > 0)
        {
            hashFollowTargetVisionJobHandle = new HashGridPositionsCellJob
            {
                CellSize = cellSize,
                ParallelWriter = _zombieVisionHashMap.AsParallelWriter()
            }.ScheduleParallel(_humanQuery, state.Dependency);
        }

        cellSize = gameControllerComponent.zombieHearingDistance * 2 + 1;
        cellCount = math.asint(math.ceil((float)gameControllerComponent.numTilesX / cellSize * gameControllerComponent.numTilesY / cellSize));
        var audibleCount = _audibleQuery.CalculateEntityCount();

        // Clear and resize pooled hash maps
        _audibleHashMap.Clear();
        if (_audibleHashMap.Capacity < audibleCount)
            _audibleHashMap.Capacity = (int)(audibleCount * 1.2f);

        var hearingMapCapacity = cellCount < audibleCount ? cellCount : audibleCount;
        _zombieHearingHashMap.Clear();
        if (_zombieHearingHashMap.Capacity < hearingMapCapacity)
            _zombieHearingHashMap.Capacity = (int)(hearingMapCapacity * 1.2f);

        var hashAudiblesJobHandle = state.Dependency;
        var hashHearingJobHandle = state.Dependency;
        if (audibleCount > 0)
        {
            hashAudiblesJobHandle = new HashAudiblesJob { ParallelWriter = _audibleHashMap.AsParallelWriter() }.ScheduleParallel(_audibleQuery, state.Dependency);
            hashHearingJobHandle = new HashAudiblesCellJob
            {
                CellSize = cellSize,
                ParallelWriter = _zombieHearingHashMap.AsParallelWriter()
            }.ScheduleParallel(_audibleQuery, state.Dependency);
        }

        // Combine all hashing job handles before scheduling the main job
        var hashJobHandles = new NativeArray<JobHandle>(4, Allocator.Temp);
        hashJobHandles[0] = state.Dependency;
        hashJobHandles[1] = hashFollowTargetVisionJobHandle;
        hashJobHandles[2] = hashAudiblesJobHandle;
        hashJobHandles[3] = hashHearingJobHandle;
        state.Dependency = JobHandle.CombineDependencies(hashJobHandles);
        hashJobHandles.Dispose();

        // Get LOS cache for this frame
        var losCacheComponent = SystemAPI.GetSingleton<LOSCacheComponent>();

        state.Dependency = new MoveTowardsHumansJob
        {
            Ecb = SystemAPI.GetSingleton<BeginInitializationEntityCommandBufferSystem.Singleton>()
                .CreateCommandBuffer(state.WorldUnmanaged).AsParallelWriter(),

            HearingDistance = gameControllerComponent.zombieHearingDistance,
            ZombieHearingHashMap = _zombieHearingHashMap,
            VisionDistance = gameControllerComponent.zombieVisionDistance,
            ZombieVisionHashMap = _zombieVisionHashMap,

            HumanHashMap = humanPositionsComponent.HashMap,
            AudibleHashMap = _audibleHashMap,
            StaticCollidablesHashMap = staticCollidableComponent.HashMap,
            DynamicCollidablesHashMap = dynamicCollidableComponent.HashMap,

            // LOS cache - read from existing cache, write new entries via parallel writer
            LOSCacheRead = losCacheComponent.Cache,
            LOSCacheWriter = losCacheComponent.Cache.AsParallelWriter(),
        }.ScheduleParallel(_moveTowardsHumanQuery, state.Dependency);
    }

    public void OnDestroy(ref SystemState state)
    {
        if (_zombieVisionHashMap.IsCreated) _zombieVisionHashMap.Dispose();
        if (_audibleHashMap.IsCreated) _audibleHashMap.Dispose();
        if (_zombieHearingHashMap.IsCreated) _zombieHearingHashMap.Dispose();
    }
}
