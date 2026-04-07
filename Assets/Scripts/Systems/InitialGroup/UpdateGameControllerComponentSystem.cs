using System;
using Unity.Burst;
using Unity.Entities;

[Serializable]
public struct UpdateGameControllerComponent : IComponentData { }

[Serializable]
public struct GameControllerComponent : IComponentData
{
    public int numTilesX;
    public int numTilesY;

    public uint citySeed;  // 0 = random seed from time

    // BSP config
    public int minBlockSize;
    public int maxBlockSize;
    public float splitVariance;

    // L-System config
    public bool enableLSystemArterials;
    public int lSystemIterations;
    public float lSystemBranchAngle;
    public float lSystemSegmentLength;
    public int lSystemRoadWidth;
    public int lSystemNumSeeds;

    // Alley config
    public bool enableAlleys;
    public int alleyMinRegionSize;
    public float alleyDeadEndProbability;
    public int alleyMaxLength;

    public int numHumans;
    public int humanStartingHealth;
    public int humanDamage;
    public int humanVisionDistance;
    public int humanTurnDelay;

    public int numZombies;
    public int zombieStartingHealth;
    public int zombieDamage;
    public int zombieVisionDistance;
    public int zombieHearingDistance;
    public int zombieTurnDelay;

    public int audibleDecayTime;
    public float turnDelayTime;
}

[BurstCompile]
[UpdateInGroup(typeof(InitializationSystemGroup))]
[UpdateBefore(typeof(TileUnitSpawnerSystem))]
[RequireMatchingQueriesForUpdate]
public partial class UpdateGameControllerComponentSystem : SystemBase
{
    private EntityQuery _updateGameControllerComponentQuery;

    [BurstCompile]
    protected override void OnCreate()
    {
        World.EntityManager.CreateSingleton<GameControllerComponent>();
        _updateGameControllerComponentQuery = GetEntityQuery(ComponentType.ReadOnly<UpdateGameControllerComponent>());
        RequireForUpdate<UpdateGameControllerComponent>();
    }

    protected override void OnUpdate()
    {
        var gameControllerComponent = SystemAPI.GetSingletonRW<GameControllerComponent>();

        gameControllerComponent.ValueRW.numTilesX = GameController.Instance.numTilesX;
        gameControllerComponent.ValueRW.numTilesY = GameController.Instance.numTilesY;

        // Generate random seed if citySeed is 0 (done here since SystemBase is not Burst-compiled)
        var seed = GameController.Instance.citySeed;
        if (seed == 0)
            seed = (uint)DateTime.Now.Ticks;
        gameControllerComponent.ValueRW.citySeed = seed;

        gameControllerComponent.ValueRW.minBlockSize = GameController.Instance.minBlockSize;
        gameControllerComponent.ValueRW.maxBlockSize = GameController.Instance.maxBlockSize;
        gameControllerComponent.ValueRW.splitVariance = GameController.Instance.splitVariance;

        gameControllerComponent.ValueRW.enableLSystemArterials = GameController.Instance.enableLSystemArterials;
        gameControllerComponent.ValueRW.lSystemIterations = GameController.Instance.lSystemIterations;
        gameControllerComponent.ValueRW.lSystemBranchAngle = GameController.Instance.lSystemBranchAngle;
        gameControllerComponent.ValueRW.lSystemSegmentLength = GameController.Instance.lSystemSegmentLength;
        gameControllerComponent.ValueRW.lSystemRoadWidth = GameController.Instance.lSystemRoadWidth;
        gameControllerComponent.ValueRW.lSystemNumSeeds = GameController.Instance.lSystemNumSeeds;

        gameControllerComponent.ValueRW.enableAlleys = GameController.Instance.enableAlleys;
        gameControllerComponent.ValueRW.alleyMinRegionSize = GameController.Instance.alleyMinRegionSize;
        gameControllerComponent.ValueRW.alleyDeadEndProbability = GameController.Instance.alleyDeadEndProbability;
        gameControllerComponent.ValueRW.alleyMaxLength = GameController.Instance.alleyMaxLength;

        gameControllerComponent.ValueRW.numHumans = GameController.Instance.numHumans;
        gameControllerComponent.ValueRW.humanStartingHealth = GameController.Instance.humanStartingHealth;
        gameControllerComponent.ValueRW.humanDamage = GameController.Instance.humanDamage;
        gameControllerComponent.ValueRW.humanVisionDistance = GameController.Instance.humanVisionDistance;
        gameControllerComponent.ValueRW.humanTurnDelay = GameController.Instance.humanTurnDelay;

        gameControllerComponent.ValueRW.numZombies = GameController.Instance.numZombies;
        gameControllerComponent.ValueRW.zombieStartingHealth = GameController.Instance.zombieStartingHealth;
        gameControllerComponent.ValueRW.zombieDamage = GameController.Instance.zombieDamage;
        gameControllerComponent.ValueRW.zombieVisionDistance = GameController.Instance.zombieVisionDistance;
        gameControllerComponent.ValueRW.zombieHearingDistance = GameController.Instance.zombieHearingDistance;
        gameControllerComponent.ValueRW.zombieTurnDelay = GameController.Instance.zombieTurnDelay;

        gameControllerComponent.ValueRW.audibleDecayTime = GameController.Instance.audibleDecayTime;
        gameControllerComponent.ValueRW.turnDelayTime = GameController.Instance.turnDelayTime;

        EntityManager.DestroyEntity(_updateGameControllerComponentQuery);
    }
}