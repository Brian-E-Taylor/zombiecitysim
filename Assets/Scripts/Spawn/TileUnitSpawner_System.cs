using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;
using Unity.Burst;
using Unity.Rendering;
using UnityEngine;

public enum TileUnitKinds
{
    BuildingTile,
    RoadTile,
    HumanUnit,
    ZombieUnit
}

[BurstCompile]
public partial struct SpawnJob : IJobEntity
{
    public int HumanTurnDelay;
    public int ZombieTurnDelay;

    public EntityCommandBuffer.ParallelWriter Ecb;

    [ReadOnly] public NativeList<int3> TileUnitPositionsNativeList;
    [ReadOnly] public NativeList<TileUnitKinds> TileUnitKindsNativeList;
    [ReadOnly] public NativeList<int> TileUnitHealthNativeList;
    [ReadOnly] public NativeList<int> TileUnitDamageNativeList;
    [ReadOnly] public NativeList<byte> TileUnitRoadHierarchyNativeList;

    public void Execute([EntityIndexInQuery] int entityIndexInQuery, [ReadOnly] in TileUnitSpawner_Data tileUnitSpawner)
    {
        for (var i = 0; i < TileUnitPositionsNativeList.Length; i++)
        {
            Entity instance;
            switch (TileUnitKindsNativeList[i])
            {
                case TileUnitKinds.BuildingTile:
                    instance = Ecb.Instantiate(entityIndexInQuery, tileUnitSpawner.BuildingTile_Prefab);
                    Ecb.SetComponent(entityIndexInQuery, instance, LocalTransform.FromPosition(TileUnitPositionsNativeList[i]));
                    Ecb.AddComponent(entityIndexInQuery, instance, new URPMaterialPropertyBaseColor { Value = new float4(0.0f, 0.0f, 0.0f, 1.0f) });
                    Ecb.AddComponent(entityIndexInQuery, instance, new GridPosition { Value = new int3(TileUnitPositionsNativeList[i]) });
                    Ecb.AddComponent(entityIndexInQuery, instance, new StaticCollidable());
                    break;
                case TileUnitKinds.RoadTile:
                    instance = Ecb.Instantiate(entityIndexInQuery, tileUnitSpawner.RoadTile_Prefab);
                    Ecb.SetComponent(entityIndexInQuery, instance, LocalTransform.FromPositionRotationScale(
                        new float3(TileUnitPositionsNativeList[i].x / 2.0f, 0.5f, TileUnitPositionsNativeList[i].z / 2.0f),
                        Quaternion.identity,
                        (TileUnitPositionsNativeList[i].x >= TileUnitPositionsNativeList[i].z ? TileUnitPositionsNativeList[i].x : TileUnitPositionsNativeList[i].z) / 10.0f - 0.1f
                    ));
                    // Road hierarchy-based color gradient
                    float brightness = CityGridHelper.GetRoadBrightness(TileUnitRoadHierarchyNativeList[i]);
                    Ecb.AddComponent(entityIndexInQuery, instance, new URPMaterialPropertyBaseColor { Value = new float4(brightness, brightness, brightness, 1.0f) });
                    Ecb.AddComponent(entityIndexInQuery, instance, new RoadSurface());
                    break;
                case TileUnitKinds.HumanUnit:
                    var turnsUntilActive = i % HumanTurnDelay + 1;
                    HumanCreator.CreateHuman(
                        ref Ecb,
                        entityIndexInQuery,
                        tileUnitSpawner.HumanUnit_Prefab,
                        TileUnitPositionsNativeList[i],
                        TileUnitHealthNativeList[i],
                        TileUnitDamageNativeList[i],
                        turnsUntilActive,
                        i == 0 ? 1 : (uint)i
                    );
                    break;
                case TileUnitKinds.ZombieUnit:
                    turnsUntilActive = i % ZombieTurnDelay + 1;
                    ZombieCreator.CreateZombie(
                        ref Ecb,
                        entityIndexInQuery,
                        tileUnitSpawner.ZombieUnit_Prefab,
                        TileUnitPositionsNativeList[i],
                        TileUnitHealthNativeList[i],
                        TileUnitDamageNativeList[i],
                        turnsUntilActive,
                        i == 0 ? 1 : (uint)i
                    );
                    break;
            }
        }
    }
}

[UpdateInGroup(typeof(InitializationSystemGroup))]
[RequireMatchingQueriesForUpdate]
public partial struct TileUnitSpawner_System : ISystem
{
    private EntityQuery _regenerateComponentsQuery;

    [BurstCompile]
    public void OnCreate(ref SystemState state)
    {
        _regenerateComponentsQuery = state.GetEntityQuery(new EntityQueryBuilder(Allocator.Temp)
            .WithAny<GridPosition, RoadSurface, HashDynamicCollidableSystemComponent, HashStaticCollidableSystemComponent>());

        state.RequireForUpdate<SpawnWorld>();
        state.RequireForUpdate<TileUnitSpawner_Data>();
        state.RequireForUpdate<GameControllerComponent>();
        state.RequireForUpdate<BeginInitializationEntityCommandBufferSystem.Singleton>();
    }

    [BurstCompile]
    public void OnUpdate(ref SystemState state)
    {
        if (SystemAPI.HasSingleton<RunWorld>())
            state.EntityManager.DestroyEntity(SystemAPI.GetSingletonEntity<RunWorld>());

        state.EntityManager.DestroyEntity(SystemAPI.GetSingletonEntity<SpawnWorld>());
        state.EntityManager.DestroyEntity(_regenerateComponentsQuery);

        var staticComponentEntity = state.EntityManager.CreateEntity();
        state.EntityManager.AddComponent(staticComponentEntity, ComponentType.ReadOnly<HashStaticCollidableSystemComponent>());

        var dynamicComponentEntity = state.EntityManager.CreateEntity();
        state.EntityManager.AddComponent(dynamicComponentEntity, ComponentType.ReadOnly<HashDynamicCollidableSystemComponent>());

        var gameControllerComponent = SystemAPI.GetSingleton<GameControllerComponent>();

        // Initialize seeded RNG (seed is guaranteed non-zero from UpdateGameControllerComponentSystem)
        var rng = new Unity.Mathematics.Random(gameControllerComponent.citySeed);

        var tileUnitPositions = new NativeList<int3>(Allocator.TempJob);
        var tileUnitKinds = new NativeList<TileUnitKinds>(Allocator.TempJob);
        var tileUnitHealth = new NativeList<int>(Allocator.TempJob);
        var tileUnitDamage = new NativeList<int>(Allocator.TempJob);
        var tileUnitRoadHierarchy = new NativeList<byte>(Allocator.TempJob);

        int gridSize = gameControllerComponent.numTilesY * gameControllerComponent.numTilesX;
        var tileExists = new NativeArray<bool>(gridSize, Allocator.Temp);
        var roadHierarchy = new NativeArray<byte>(gridSize, Allocator.Temp);

        for (var y = 0; y < gameControllerComponent.numTilesY; y++)
            for (var x = 0; x < gameControllerComponent.numTilesX; x++)
                tileExists[y * gameControllerComponent.numTilesX + x] = true;

        // BSP city generation
        var cityBlocks = new NativeList<CityBlock>(128, Allocator.Temp);
        var roadSplits = new NativeList<RoadSplit>(128, Allocator.Temp);

        BSPCityGenerator.GenerateBlocks(
            gameControllerComponent.numTilesX,
            gameControllerComponent.numTilesY,
            1, // border size
            gameControllerComponent.minBlockSize,
            gameControllerComponent.maxBlockSize,
            gameControllerComponent.splitVariance,
            ref rng,
            ref cityBlocks,
            ref roadSplits
        );

        BSPCityGenerator.ApplyRoadsToGridWithHierarchy(
            ref tileExists,
            ref roadHierarchy,
            gameControllerComponent.numTilesX,
            gameControllerComponent.numTilesY,
            ref roadSplits
        );

        cityBlocks.Dispose();
        roadSplits.Dispose();

        // Road border boundary
        for (var y = 0; y < gameControllerComponent.numTilesY; y++)
        {
            tileExists[y * gameControllerComponent.numTilesX] = true;
            tileExists[y * gameControllerComponent.numTilesX + gameControllerComponent.numTilesX - 1] = true;
        }

        // Road border boundary (top and bottom rows)
        for (var x = 1; x < gameControllerComponent.numTilesX - 1; x++)
        {
            tileExists[x] = true;
            tileExists[(gameControllerComponent.numTilesY - 1) * gameControllerComponent.numTilesX + x] = true;
        }

        // Fill in buildings
        for (var y = 0; y < gameControllerComponent.numTilesY; y++)
        {
            for (var x = 0; x < gameControllerComponent.numTilesX; x++)
            {
                if (!tileExists[y * gameControllerComponent.numTilesX + x])
                    continue;

                tileUnitKinds.Add(TileUnitKinds.BuildingTile);
                tileUnitPositions.Add(new int3(x, 1, y));
                tileUnitHealth.Add(0);
                tileUnitDamage.Add(0);
                tileUnitRoadHierarchy.Add(0); // 0 = not a road
            }
        }

        // Road Floor Plane
        tileUnitKinds.Add(TileUnitKinds.RoadTile);
        tileUnitPositions.Add(new int3(gameControllerComponent.numTilesX, 0, gameControllerComponent.numTilesY));
        tileUnitHealth.Add(0);
        tileUnitDamage.Add(0);
        tileUnitRoadHierarchy.Add((byte)RoadHierarchyLevel.Arterial); // Default for floor plane

        // Human Units
        for (var i = 0; i < gameControllerComponent.numHumans; i++)
        {
            int xPos, yPos;

            do
            {
                xPos = rng.NextInt(1, gameControllerComponent.numTilesX - 1);
                yPos = rng.NextInt(1, gameControllerComponent.numTilesY - 1);
            } while (tileExists[yPos * gameControllerComponent.numTilesX + xPos]);

            tileExists[yPos * gameControllerComponent.numTilesX + xPos] = true;
            tileUnitKinds.Add(TileUnitKinds.HumanUnit);
            tileUnitPositions.Add(new int3(xPos, 1, yPos));
            tileUnitHealth.Add(gameControllerComponent.humanStartingHealth);
            tileUnitDamage.Add(gameControllerComponent.humanDamage);
            tileUnitRoadHierarchy.Add(0); // Not a road
        }

        // Zombie Units
        for (var i = 0; i < gameControllerComponent.numZombies; i++)
        {
            int xPos, yPos;

            do
            {
                xPos = rng.NextInt(1, gameControllerComponent.numTilesX - 1);
                yPos = rng.NextInt(1, gameControllerComponent.numTilesY - 1);
            } while (tileExists[yPos * gameControllerComponent.numTilesX + xPos]);

            tileExists[yPos * gameControllerComponent.numTilesX + xPos] = true;
            tileUnitKinds.Add(TileUnitKinds.ZombieUnit);
            tileUnitPositions.Add(new int3(xPos, 1, yPos));
            tileUnitHealth.Add(gameControllerComponent.zombieStartingHealth);
            tileUnitDamage.Add(gameControllerComponent.zombieDamage);
            tileUnitRoadHierarchy.Add(0); // Not a road
        }

        tileExists.Dispose();
        roadHierarchy.Dispose();

        new SpawnJob
        {
            HumanTurnDelay = gameControllerComponent.humanTurnDelay,
            ZombieTurnDelay = gameControllerComponent.zombieTurnDelay,

            Ecb = SystemAPI.GetSingleton<BeginInitializationEntityCommandBufferSystem.Singleton>()
                .CreateCommandBuffer(state.WorldUnmanaged).AsParallelWriter(),

            TileUnitPositionsNativeList = tileUnitPositions,
            TileUnitKindsNativeList = tileUnitKinds,
            TileUnitHealthNativeList = tileUnitHealth,
            TileUnitDamageNativeList = tileUnitDamage,
            TileUnitRoadHierarchyNativeList = tileUnitRoadHierarchy
        }.Run();

        tileUnitPositions.Dispose(state.Dependency);
        tileUnitKinds.Dispose(state.Dependency);
        tileUnitHealth.Dispose(state.Dependency);
        tileUnitDamage.Dispose(state.Dependency);
        tileUnitRoadHierarchy.Dispose(state.Dependency);

        state.EntityManager.CreateSingleton<RunWorld>();
    }
}
