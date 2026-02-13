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
                    // Create collision-only entity (no visual components)
                    // Visual rendering handled by ProceduralCityMeshGenerator
                    instance = Ecb.CreateEntity(entityIndexInQuery);
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
                    var brightness = CityGridHelper.GetRoadBrightness(TileUnitRoadHierarchyNativeList[i]);
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
public partial struct TileUnitSpawnerSystem : ISystem
{
    private EntityQuery _regenerateComponentsQuery;

    [BurstCompile]
    public void OnCreate(ref SystemState state)
    {
        _regenerateComponentsQuery = state.GetEntityQuery(new EntityQueryBuilder(Allocator.Temp)
            .WithAny<GridPosition, RoadSurface, HashDynamicCollidableSystemComponent, HashStaticCollidableSystemComponent, LOSCacheComponent, HashHumanPositionsComponent, HashZombiePositionsComponent>());

        state.RequireForUpdate<SpawnWorld>();
        state.RequireForUpdate<TileUnitSpawner_Data>();
        state.RequireForUpdate<GameControllerComponent>();
        state.RequireForUpdate<BeginInitializationEntityCommandBufferSystem.Singleton>();
    }

    // Note: Not Burst-compiled because we need to call managed code (ProceduralCityMeshGenerator)
    public void OnUpdate(ref SystemState state)
    {
        // Clean up RunWorld from previous run if it exists (e.g., when regenerating city)
        if (SystemAPI.TryGetSingletonEntity<RunWorld>(out var runWorldEntity))
            state.EntityManager.DestroyEntity(runWorldEntity);

        state.EntityManager.DestroyEntity(SystemAPI.GetSingletonEntity<SpawnWorld>());
        state.EntityManager.DestroyEntity(_regenerateComponentsQuery);

        var staticComponentEntity = state.EntityManager.CreateEntity();
        state.EntityManager.AddComponent(staticComponentEntity, ComponentType.ReadOnly<HashStaticCollidableSystemComponent>());

        var dynamicComponentEntity = state.EntityManager.CreateEntity();
        state.EntityManager.AddComponent(dynamicComponentEntity, ComponentType.ReadOnly<HashDynamicCollidableSystemComponent>());

        // Create LOS cache singleton for caching line-of-sight calculations
        var losCacheEntity = state.EntityManager.CreateEntity();
        state.EntityManager.AddComponent(losCacheEntity, ComponentType.ReadWrite<LOSCacheComponent>());

        // Create shared unit position hash map singletons
        var humanHashEntity = state.EntityManager.CreateEntity();
        state.EntityManager.AddComponent(humanHashEntity, ComponentType.ReadWrite<HashHumanPositionsComponent>());

        var zombieHashEntity = state.EntityManager.CreateEntity();
        state.EntityManager.AddComponent(zombieHashEntity, ComponentType.ReadWrite<HashZombiePositionsComponent>());

        var gameControllerComponent = SystemAPI.GetSingleton<GameControllerComponent>();

        // Initialize seeded RNG (seed is guaranteed non-zero from UpdateGameControllerComponentSystem)
        var rng = new Unity.Mathematics.Random(gameControllerComponent.citySeed);

        var tileUnitPositions = new NativeList<int3>(Allocator.TempJob);
        var tileUnitKinds = new NativeList<TileUnitKinds>(Allocator.TempJob);
        var tileUnitHealth = new NativeList<int>(Allocator.TempJob);
        var tileUnitDamage = new NativeList<int>(Allocator.TempJob);
        var tileUnitRoadHierarchy = new NativeList<byte>(Allocator.TempJob);
        // These are only used for mesh generation, not in jobs, so use Temp allocator
        var tileUnitHeights = new NativeList<byte>(Allocator.Temp);
        var tileUnitBuildingIds = new NativeList<ushort>(Allocator.Temp);

        var gridSize = gameControllerComponent.numTilesY * gameControllerComponent.numTilesX;
        var tileExists = new NativeArray<bool>(gridSize, Allocator.Temp);
        var roadHierarchy = new NativeArray<byte>(gridSize, Allocator.Temp);
        var regionIds = new NativeArray<ushort>(gridSize, Allocator.Temp);
        var buildingIds = new NativeArray<ushort>(gridSize, Allocator.Temp);
        var buildingHeights = new NativeArray<byte>(gridSize, Allocator.Temp);

        for (var y = 0; y < gameControllerComponent.numTilesY; y++)
            for (var x = 0; x < gameControllerComponent.numTilesX; x++)
                tileExists[y * gameControllerComponent.numTilesX + x] = true;

        // L-System arterial generation (before BSP)
        if (gameControllerComponent.enableLSystemArterials)
        {
            LSystemArterialGenerator.GenerateArterials(
                ref tileExists,
                ref roadHierarchy,
                gameControllerComponent.numTilesX,
                gameControllerComponent.numTilesY,
                gameControllerComponent.lSystemIterations,
                gameControllerComponent.lSystemBranchAngle,
                gameControllerComponent.lSystemSegmentLength,
                gameControllerComponent.lSystemRoadWidth,
                gameControllerComponent.lSystemNumSeeds,
                ref rng);
        }

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
            ref roadSplits,
            ref tileExists,
            ref roadHierarchy,
            gameControllerComponent.enableLSystemArterials
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

        // Temporarily mark border tiles as false so they're excluded from region detection
        // This prevents templates from carving into the border walls
        for (var y = 0; y < gameControllerComponent.numTilesY; y++)
        {
            tileExists[y * gameControllerComponent.numTilesX] = false; // Left edge
            tileExists[y * gameControllerComponent.numTilesX + gameControllerComponent.numTilesX - 1] = false; // Right edge
        }
        for (var x = 0; x < gameControllerComponent.numTilesX; x++)
        {
            tileExists[x] = false; // Bottom edge
            tileExists[(gameControllerComponent.numTilesY - 1) * gameControllerComponent.numTilesX + x] = false; // Top edge
        }

        // Detect contiguous building regions (border tiles excluded)
        var regions = new NativeList<BuildingRegion>(128, Allocator.Temp);
        BuildingRegionDetector.DetectRegions(
            ref tileExists,
            gameControllerComponent.numTilesX,
            gameControllerComponent.numTilesY,
            ref regionIds,
            ref regions
        );

        // Apply building templates to regions (assigns buildingIds and heights, may carve courtyards)
        BuildingTemplates.ApplyTemplatesToRegions(
            ref tileExists,
            ref regionIds,
            ref buildingIds,
            ref buildingHeights,
            gameControllerComponent.numTilesX,
            gameControllerComponent.numTilesY,
            ref regions,
            ref rng
        );

        // Alley generation (after templates, before disposal)
        if (gameControllerComponent.enableAlleys)
        {
            AlleyGenerator.GenerateAlleys(
                ref tileExists,
                ref roadHierarchy,
                gameControllerComponent.numTilesX,
                gameControllerComponent.numTilesY,
                ref regions,
                ref regionIds,
                gameControllerComponent.alleyMinRegionSize,
                gameControllerComponent.alleyDeadEndProbability,
                gameControllerComponent.alleyMaxLength,
                ref rng);
        }

        regions.Dispose();

        // Now add border walls - they're separate from building regions
        for (var y = 0; y < gameControllerComponent.numTilesY; y++)
        {
            var leftIdx = y * gameControllerComponent.numTilesX;
            var rightIdx = y * gameControllerComponent.numTilesX + gameControllerComponent.numTilesX - 1;
            tileExists[leftIdx] = true;
            tileExists[rightIdx] = true;
            buildingHeights[leftIdx] = 3; // Border walls are taller
            buildingHeights[rightIdx] = 3;
        }
        for (var x = 0; x < gameControllerComponent.numTilesX; x++)
        {
            var bottomIdx = x;
            var topIdx = (gameControllerComponent.numTilesY - 1) * gameControllerComponent.numTilesX + x;
            tileExists[bottomIdx] = true;
            tileExists[topIdx] = true;
            buildingHeights[bottomIdx] = 3;
            buildingHeights[topIdx] = 3;
        }

        // Fill in buildings with their assigned heights
        for (var y = 0; y < gameControllerComponent.numTilesY; y++)
        {
            for (var x = 0; x < gameControllerComponent.numTilesX; x++)
            {
                var idx = y * gameControllerComponent.numTilesX + x;
                if (!tileExists[idx])
                    continue;

                tileUnitKinds.Add(TileUnitKinds.BuildingTile);
                tileUnitPositions.Add(new int3(x, 1, y));
                tileUnitHealth.Add(0);
                tileUnitDamage.Add(0);
                tileUnitRoadHierarchy.Add(0); // 0 = not a road
                tileUnitHeights.Add(buildingHeights[idx]);
                tileUnitBuildingIds.Add(buildingIds[idx]);
            }
        }

        // Road Floor Plane
        tileUnitKinds.Add(TileUnitKinds.RoadTile);
        tileUnitPositions.Add(new int3(gameControllerComponent.numTilesX, 0, gameControllerComponent.numTilesY));
        tileUnitHealth.Add(0);
        tileUnitDamage.Add(0);
        tileUnitRoadHierarchy.Add((byte)RoadHierarchyLevel.Arterial); // Default for floor plane
        tileUnitHeights.Add(0);
        tileUnitBuildingIds.Add(0);

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
            tileUnitHeights.Add(0);
            tileUnitBuildingIds.Add(0);
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
            tileUnitHeights.Add(0);
            tileUnitBuildingIds.Add(0);
        }

        // Collect building mesh data for procedural generation (before disposing native arrays)
        var buildingMeshDataList = new System.Collections.Generic.List<BuildingMeshData>();
        for (var i = 0; i < tileUnitKinds.Length; i++)
        {
            if (tileUnitKinds[i] == TileUnitKinds.BuildingTile)
            {
                buildingMeshDataList.Add(new BuildingMeshData
                {
                    X = tileUnitPositions[i].x,
                    Z = tileUnitPositions[i].z,
                    Height = tileUnitHeights[i],
                    BuildingId = tileUnitBuildingIds[i]
                });
            }
        }

        tileExists.Dispose();
        roadHierarchy.Dispose();
        regionIds.Dispose();
        buildingIds.Dispose();
        buildingHeights.Dispose();

        // Dispose mesh-only lists now that we've collected the data
        tileUnitHeights.Dispose();
        tileUnitBuildingIds.Dispose();

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

        // Generate procedural city mesh (buildings rendered via MonoBehaviour, not ECS)
        if (ProceduralCityMeshGenerator.Instance != null)
        {
            ProceduralCityMeshGenerator.Instance.GenerateCityMesh(
                buildingMeshDataList.ToArray(),
                gameControllerComponent.numTilesX,
                gameControllerComponent.numTilesY
            );
        }

        state.EntityManager.CreateSingleton<RunWorld>();
    }
}
