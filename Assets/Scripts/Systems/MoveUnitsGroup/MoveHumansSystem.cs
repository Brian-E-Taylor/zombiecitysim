using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;

[BurstCompile]
public partial struct MoveHumansJob : IJobEntity
{
    // Cap on zombies sampled per human when computing the averaged flee direction.
    // The averaged vector stabilizes well before this; closer rings are visited first,
    // so the kept samples are the threats that matter most for fleeing.
    private const int MaxFleeSamples = 8;

    public int VisionDistance;
    [ReadOnly] public NativeParallelHashMap<uint, int> HumanVisionHashMap;
    [ReadOnly] public NativeArray<int2> VisionOffsets;

    [ReadOnly] public NativeParallelHashMap<uint, int> ZombieHashMap;
    [ReadOnly] public NativeParallelHashMap<uint, int> StaticCollidablesHashMap;
    [ReadOnly] public NativeParallelHashMap<uint, int> DynamicCollidablesHashMap;

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

            // Walk the precomputed circular offset table (sorted by squared distance).
            // Closest threats are visited first, so MaxFleeSamples keeps the most relevant ones.
            for (var i = 0; i < VisionOffsets.Length && targetCount < MaxFleeSamples; i++)
            {
                var off = VisionOffsets[i];
                var targetGridPosition = new int3(myGridPositionValue.x + off.x, myGridPositionValue.y, myGridPositionValue.z + off.y);
                var targetKey = GridPositionHash.GetKey(targetGridPosition.x, targetGridPosition.z);

                if (!ZombieHashMap.TryGetValue(targetKey, out _))
                    continue;

                if (!LineOfSightUtilities.InLineOfSightUpdated(myGridPositionValue, targetGridPosition, StaticCollidablesHashMap))
                    continue;

                averageTarget += new float3(off.x, 0, off.y);
                targetCount++;
                foundTarget = true;
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

    // Circular offset table (sorted by squared distance) for the per-tile vision scan.
    // Rebuilt lazily when humanVisionDistance changes.
    private NativeArray<int2> _visionOffsets;
    private int _visionOffsetsRadius;

    private const int InitialPoolCapacity = 256;

    public void OnCreate(ref SystemState state)
    {
        _zombieQuery = state.GetEntityQuery(new EntityQueryBuilder(Allocator.Temp).WithAll<Zombie, GridPosition>());

        _humanVisionHashMap = new NativeParallelHashMap<uint, int>(InitialPoolCapacity, Allocator.Persistent);

        state.RequireForUpdate<RunWorld>();
        state.RequireForUpdate<HashStaticCollidableSystemComponent>();
        state.RequireForUpdate<HashDynamicCollidableSystemComponent>();
        state.RequireForUpdate<GameControllerComponent>();
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

        EnsureOffsets(ref _visionOffsets, ref _visionOffsetsRadius, gameControllerComponent.humanVisionDistance);

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

        state.Dependency = new MoveHumansJob
        {
            VisionDistance = gameControllerComponent.humanVisionDistance,
            HumanVisionHashMap = _humanVisionHashMap,
            VisionOffsets = _visionOffsets,

            ZombieHashMap = zombiePositionsComponent.HashMap,
            StaticCollidablesHashMap = staticCollidableHashMap,
            DynamicCollidablesHashMap = dynamicCollidableHashMap,
        }.ScheduleParallel(state.Dependency);
    }

    public void OnDestroy(ref SystemState state)
    {
        if (_humanVisionHashMap.IsCreated) _humanVisionHashMap.Dispose();
        if (_visionOffsets.IsCreated) _visionOffsets.Dispose();
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
