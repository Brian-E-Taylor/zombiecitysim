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
[UpdateBefore(typeof(TileUnitSpawner_System))]
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
        uint seed = GameController.Instance.citySeed;
        if (seed == 0)
            seed = (uint)System.DateTime.Now.Ticks;
        gameControllerComponent.ValueRW.citySeed = seed;

        gameControllerComponent.ValueRW.minBlockSize = GameController.Instance.minBlockSize;
        gameControllerComponent.ValueRW.maxBlockSize = GameController.Instance.maxBlockSize;
        gameControllerComponent.ValueRW.splitVariance = GameController.Instance.splitVariance;

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