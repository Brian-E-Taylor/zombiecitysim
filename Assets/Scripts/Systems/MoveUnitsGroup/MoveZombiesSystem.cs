using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;

[BurstCompile]
public partial struct MoveZombiesJob : IJobEntity
{
    public EntityCommandBuffer.ParallelWriter Ecb;

    public int HearingDistance;
    [ReadOnly] public NativeParallelHashMap<uint, int> ZombieHearingHashMap;
    [ReadOnly] public NativeArray<int2> HearingOffsets;
    public int VisionDistance;
    [ReadOnly] public NativeParallelHashMap<uint, int> ZombieVisionHashMap;
    [ReadOnly] public NativeArray<int2> VisionOffsets;

    [ReadOnly] public NativeParallelHashMap<uint, int> HumanHashMap;
    [ReadOnly] public NativeParallelMultiHashMap<uint, int3> AudibleHashMap;
    [ReadOnly] public NativeParallelHashMap<uint, int> StaticCollidablesHashMap;
    [ReadOnly] public NativeParallelHashMap<uint, int> DynamicCollidablesHashMap;

    public void Execute([EntityIndexInQuery] int entityIndexInQuery, ref DesiredNextGridPosition desiredNextGridPosition, ref RandomGenerator random, [ReadOnly] in GridPosition gridPosition, [ReadOnly] in TurnActive turnActive, [ReadOnly] in Zombie zombie)
    {
        var zombieHearingHashMapCellSize = HearingDistance * 2 + 1;
        var zombieVisionHashMapCellSize = VisionDistance * 2 + 1;

        var myGridPositionValue = gridPosition.Value;
        var nearestTarget = myGridPositionValue;
        var moved = false;
        var foundTarget = false;
        // Broadphase early-rejection: check the four corner cells of the vision bounding box.
        // If no human occupies any of those coarse cells, skip the expensive per-tile ring scan.
        var foundBySight = ZombieVisionHashMap.TryGetValue(math.hash(new int3(myGridPositionValue.x - VisionDistance, myGridPositionValue.y, myGridPositionValue.z - VisionDistance) / zombieVisionHashMapCellSize), out _) ||
                           ZombieVisionHashMap.TryGetValue(math.hash(new int3(myGridPositionValue.x + VisionDistance, myGridPositionValue.y, myGridPositionValue.z - VisionDistance) / zombieVisionHashMapCellSize), out _) ||
                           ZombieVisionHashMap.TryGetValue(math.hash(new int3(myGridPositionValue.x - VisionDistance, myGridPositionValue.y, myGridPositionValue.z + VisionDistance) / zombieVisionHashMapCellSize), out _) ||
                           ZombieVisionHashMap.TryGetValue(math.hash(new int3(myGridPositionValue.x + VisionDistance, myGridPositionValue.y, myGridPositionValue.z + VisionDistance) / zombieVisionHashMapCellSize), out _);

        if (foundBySight)
        {
            foundBySight = false;

            // Walk the precomputed circular offset table (sorted by squared distance).
            // First valid hit is the geometrically nearest visible human.
            for (var i = 0; i < VisionOffsets.Length; i++)
            {
                var off = VisionOffsets[i];
                var targetGridPosition = new int3(myGridPositionValue.x + off.x, myGridPositionValue.y, myGridPositionValue.z + off.y);
                var targetKey = GridPositionHash.GetKey(targetGridPosition.x, targetGridPosition.z);

                if (!HumanHashMap.TryGetValue(targetKey, out _))
                    continue;

                if (!LineOfSightUtilities.InLineOfSightUpdated(myGridPositionValue, targetGridPosition, StaticCollidablesHashMap))
                    continue;

                nearestTarget = targetGridPosition;
                foundBySight = true;
                foundTarget = true;
                break;
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
                // Walk the precomputed circular offset table (sorted by squared distance).
                // First audible cell encountered is the geometrically nearest sound source.
                for (var i = 0; i < HearingOffsets.Length; i++)
                {
                    var off = HearingOffsets[i];
                    var targetGridPosition = new int3(myGridPositionValue.x + off.x, myGridPositionValue.y, myGridPositionValue.z + off.y);
                    var targetKey = GridPositionHash.GetKey(targetGridPosition.x, targetGridPosition.z);

                    if (!AudibleHashMap.TryGetFirstValue(targetKey, out var audibleTarget, out _))
                        continue;

                    nearestTarget = audibleTarget;
                    foundTarget = true;
                    break;
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
            // Only sight-confirmed targets propagate sound events. Hearing-triggered movement
            // does not create Audible entities, preventing cascading second-order sound chains.
            var audibleEntity = Ecb.CreateEntity(entityIndexInQuery);
            Ecb.AddComponent(entityIndexInQuery, audibleEntity, new Audible { GridPositionValue = myGridPositionValue, Target = nearestTarget, Age = 0 });
        }

        desiredNextGridPosition = new DesiredNextGridPosition { Value = myGridPositionValue };
    }
}

[UpdateInGroup(typeof(MoveUnitsGroup))]
[RequireMatchingQueriesForUpdate]
public partial struct MoveZombiesSystem : ISystem
{
    private EntityQuery _humanQuery;
    private EntityQuery _audibleQuery;

    // Pooled hash maps for vision/hearing cell lookups and audible data
    private NativeParallelHashMap<uint, int> _zombieVisionHashMap;
    private NativeParallelMultiHashMap<uint, int3> _audibleHashMap;
    private NativeParallelHashMap<uint, int> _zombieHearingHashMap;

    // Circular offset tables (sorted by squared distance) for the per-tile vision/hearing scans.
    // Rebuilt lazily when the active radius changes.
    private NativeArray<int2> _visionOffsets;
    private int _visionOffsetsRadius;
    private NativeArray<int2> _hearingOffsets;
    private int _hearingOffsetsRadius;

    private const int InitialPoolCapacity = 256;

    public void OnCreate(ref SystemState state)
    {
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
        state.RequireForUpdate<HashHumanPositionsComponent>();
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

        EnsureOffsets(ref _visionOffsets, ref _visionOffsetsRadius, gameControllerComponent.zombieVisionDistance);
        EnsureOffsets(ref _hearingOffsets, ref _hearingOffsetsRadius, gameControllerComponent.zombieHearingDistance);

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

        state.Dependency = new MoveZombiesJob
        {
            Ecb = SystemAPI.GetSingleton<BeginInitializationEntityCommandBufferSystem.Singleton>()
                .CreateCommandBuffer(state.WorldUnmanaged).AsParallelWriter(),

            HearingDistance = gameControllerComponent.zombieHearingDistance,
            ZombieHearingHashMap = _zombieHearingHashMap,
            HearingOffsets = _hearingOffsets,
            VisionDistance = gameControllerComponent.zombieVisionDistance,
            ZombieVisionHashMap = _zombieVisionHashMap,
            VisionOffsets = _visionOffsets,

            HumanHashMap = humanPositionsComponent.HashMap,
            AudibleHashMap = _audibleHashMap,
            StaticCollidablesHashMap = staticCollidableComponent.HashMap,
            DynamicCollidablesHashMap = dynamicCollidableComponent.HashMap,
        }.ScheduleParallel(state.Dependency);
    }

    public void OnDestroy(ref SystemState state)
    {
        if (_zombieVisionHashMap.IsCreated) _zombieVisionHashMap.Dispose();
        if (_audibleHashMap.IsCreated) _audibleHashMap.Dispose();
        if (_zombieHearingHashMap.IsCreated) _zombieHearingHashMap.Dispose();
        if (_visionOffsets.IsCreated) _visionOffsets.Dispose();
        if (_hearingOffsets.IsCreated) _hearingOffsets.Dispose();
    }

    private static void EnsureOffsets(ref NativeArray<int2> offsets, ref int cachedRadius, int currentRadius)
    {
        if (offsets.IsCreated && cachedRadius == currentRadius)
            return;
        if (offsets.IsCreated) offsets.Dispose();
        offsets = VisionOffsets.Build(currentRadius, Allocator.Persistent);
        cachedRadius = currentRadius;
    }
}
