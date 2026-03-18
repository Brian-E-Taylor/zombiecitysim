using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;
using Unity.Burst;
using Unity.Jobs;
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
public struct SpawnJob : IJobParallelFor
{
    public int HumanTurnDelay;
    public int ZombieTurnDelay;
    public Entity RoadTile_Prefab;
    public Entity HumanUnit_Prefab;
    public Entity ZombieUnit_Prefab;

    public EntityCommandBuffer.ParallelWriter Ecb;

    [ReadOnly] public NativeArray<int3> TileUnitPositionsArray;
    [ReadOnly] public NativeArray<TileUnitKinds> TileUnitKindsArray;
    [ReadOnly] public NativeArray<int> TileUnitHealthArray;
    [ReadOnly] public NativeArray<int> TileUnitDamageArray;
    [ReadOnly] public NativeArray<byte> TileUnitRoadHierarchyArray;

    public void Execute(int i)
    {
        Entity instance;
        switch (TileUnitKindsArray[i])
        {
            case TileUnitKinds.BuildingTile:
                // Create collision-only entity (no visual components)
                // Visual rendering handled by ProceduralCityMeshGenerator
                instance = Ecb.CreateEntity(i);
                Ecb.AddComponent(i, instance, new GridPosition { Value = new int3(TileUnitPositionsArray[i]) });
                Ecb.AddComponent(i, instance, new StaticCollidable());
                break;
            case TileUnitKinds.RoadTile:
                instance = Ecb.Instantiate(i, RoadTile_Prefab);
                Ecb.SetComponent(i, instance, LocalTransform.FromPositionRotationScale(
                    new float3(TileUnitPositionsArray[i].x / 2.0f, 0.5f, TileUnitPositionsArray[i].z / 2.0f),
                    Quaternion.identity,
                    (TileUnitPositionsArray[i].x >= TileUnitPositionsArray[i].z ? TileUnitPositionsArray[i].x : TileUnitPositionsArray[i].z) / 10.0f - 0.1f
                ));
                // Road hierarchy-based color gradient
                var brightness = CityGridHelper.GetRoadBrightness(TileUnitRoadHierarchyArray[i]);
                Ecb.AddComponent(i, instance, new URPMaterialPropertyBaseColor { Value = new float4(brightness, brightness, brightness, 1.0f) });
                Ecb.AddComponent(i, instance, new RoadSurface());
                break;
            case TileUnitKinds.HumanUnit:
                var turnsUntilActive = i % HumanTurnDelay + 1;
                HumanCreator.CreateHuman(
                    ref Ecb,
                    i,
                    HumanUnit_Prefab,
                    TileUnitPositionsArray[i],
                    TileUnitHealthArray[i],
                    TileUnitDamageArray[i],
                    turnsUntilActive,
                    i == 0 ? 1 : (uint)i
                );
                break;
            case TileUnitKinds.ZombieUnit:
                turnsUntilActive = i % ZombieTurnDelay + 1;
                ZombieCreator.CreateZombie(
                    ref Ecb,
                    i,
                    ZombieUnit_Prefab,
                    TileUnitPositionsArray[i],
                    TileUnitHealthArray[i],
                    TileUnitDamageArray[i],
                    turnsUntilActive,
                    i == 0 ? 1 : (uint)i
                );
                break;
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
        var tileUnitSpawner = SystemAPI.GetSingleton<TileUnitSpawner_Data>();

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

        // Phase 1: Generate city layout (L-system arterials + BSP + regions + templates + alleys)
        GenerateCity(ref gameControllerComponent, ref rng, ref tileExists, ref roadHierarchy, ref regionIds, ref buildingIds, ref buildingHeights);

        // Phase 2: Collect building tiles into spawn lists
        CollectBuildingTiles(ref gameControllerComponent, ref tileExists, ref buildingHeights, ref buildingIds,
            ref tileUnitPositions, ref tileUnitKinds, ref tileUnitHealth, ref tileUnitDamage,
            ref tileUnitRoadHierarchy, ref tileUnitHeights, ref tileUnitBuildingIds);

        // Phase 3: Shuffle open positions and place human/zombie units
        SpawnUnits(ref gameControllerComponent, ref rng, ref tileExists,
            ref tileUnitPositions, ref tileUnitKinds, ref tileUnitHealth, ref tileUnitDamage,
            ref tileUnitRoadHierarchy, ref tileUnitHeights, ref tileUnitBuildingIds);

        // Phase 4: Collect building mesh data for procedural generation
        var buildingMeshDataNativeList = CollectBuildingMeshData(ref tileUnitPositions, ref tileUnitKinds, ref tileUnitHeights, ref tileUnitBuildingIds);

        tileExists.Dispose();
        roadHierarchy.Dispose();
        regionIds.Dispose();
        buildingIds.Dispose();
        buildingHeights.Dispose();

        // Dispose mesh-only lists now that we've collected the data
        tileUnitHeights.Dispose();
        tileUnitBuildingIds.Dispose();

        var spawnJobHandle = new SpawnJob
        {
            HumanTurnDelay = gameControllerComponent.humanTurnDelay,
            ZombieTurnDelay = gameControllerComponent.zombieTurnDelay,
            RoadTile_Prefab = tileUnitSpawner.RoadTile_Prefab,
            HumanUnit_Prefab = tileUnitSpawner.HumanUnit_Prefab,
            ZombieUnit_Prefab = tileUnitSpawner.ZombieUnit_Prefab,

            Ecb = SystemAPI.GetSingleton<BeginInitializationEntityCommandBufferSystem.Singleton>()
                .CreateCommandBuffer(state.WorldUnmanaged).AsParallelWriter(),

            TileUnitPositionsArray = tileUnitPositions.AsArray(),
            TileUnitKindsArray = tileUnitKinds.AsArray(),
            TileUnitHealthArray = tileUnitHealth.AsArray(),
            TileUnitDamageArray = tileUnitDamage.AsArray(),
            TileUnitRoadHierarchyArray = tileUnitRoadHierarchy.AsArray()
        }.Schedule(tileUnitPositions.Length, 64, state.Dependency);

        tileUnitPositions.Dispose(spawnJobHandle);
        tileUnitKinds.Dispose(spawnJobHandle);
        tileUnitHealth.Dispose(spawnJobHandle);
        tileUnitDamage.Dispose(spawnJobHandle);
        tileUnitRoadHierarchy.Dispose(spawnJobHandle);

        state.Dependency = spawnJobHandle;

        // Generate procedural city mesh (buildings rendered via MonoBehaviour, not ECS)
        if (ProceduralCityMeshGenerator.Instance != null)
        {
            ProceduralCityMeshGenerator.Instance.GenerateCityMesh(
                buildingMeshDataNativeList,
                gameControllerComponent.numTilesX,
                gameControllerComponent.numTilesY
            );
        }

        buildingMeshDataNativeList.Dispose();

        state.EntityManager.CreateSingleton<RunWorld>();
    }

    /// <summary>
    /// Runs the procedural city generation pipeline: L-system arterials, BSP block subdivision,
    /// region detection, building template application, and alley generation.
    /// </summary>
    private static void GenerateCity(
        ref GameControllerComponent gameControllerComponent,
        ref Unity.Mathematics.Random rng,
        ref NativeArray<bool> tileExists,
        ref NativeArray<byte> roadHierarchy,
        ref NativeArray<ushort> regionIds,
        ref NativeArray<ushort> buildingIds,
        ref NativeArray<byte> buildingHeights)
    {
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
    }

    /// <summary>
    /// Iterates the building grid and populates the tile spawn lists with building tiles
    /// and the road floor plane.
    /// </summary>
    private static void CollectBuildingTiles(
        ref GameControllerComponent gameControllerComponent,
        ref NativeArray<bool> tileExists,
        ref NativeArray<byte> buildingHeights,
        ref NativeArray<ushort> buildingIds,
        ref NativeList<int3> tileUnitPositions,
        ref NativeList<TileUnitKinds> tileUnitKinds,
        ref NativeList<int> tileUnitHealth,
        ref NativeList<int> tileUnitDamage,
        ref NativeList<byte> tileUnitRoadHierarchy,
        ref NativeList<byte> tileUnitHeights,
        ref NativeList<ushort> tileUnitBuildingIds)
    {
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
    }

    /// <summary>
    /// Collects open interior positions, performs a Fisher-Yates shuffle, then places
    /// human and zombie units into the spawn lists.
    /// </summary>
    private static void SpawnUnits(
        ref GameControllerComponent gameControllerComponent,
        ref Unity.Mathematics.Random rng,
        ref NativeArray<bool> tileExists,
        ref NativeList<int3> tileUnitPositions,
        ref NativeList<TileUnitKinds> tileUnitKinds,
        ref NativeList<int> tileUnitHealth,
        ref NativeList<int> tileUnitDamage,
        ref NativeList<byte> tileUnitRoadHierarchy,
        ref NativeList<byte> tileUnitHeights,
        ref NativeList<ushort> tileUnitBuildingIds)
    {
        // Collect open interior positions for unit spawning
        var openPositions = new NativeList<int2>(Allocator.Temp);
        for (var y = 1; y < gameControllerComponent.numTilesY - 1; y++)
        {
            for (var x = 1; x < gameControllerComponent.numTilesX - 1; x++)
            {
                if (!tileExists[y * gameControllerComponent.numTilesX + x])
                    openPositions.Add(new int2(x, y));
            }
        }

        // Fisher-Yates shuffle
        for (var i = openPositions.Length - 1; i > 0; i--)
        {
            var j = rng.NextInt(0, i + 1);
            (openPositions[i], openPositions[j]) = (openPositions[j], openPositions[i]);
        }

        var spawnIndex = 0;
        var availablePositions = openPositions.Length;
        var actualHumans = math.min(gameControllerComponent.numHumans, availablePositions);
        var actualZombies = math.min(gameControllerComponent.numZombies, math.max(0, availablePositions - actualHumans));

        // Human Units
        for (var i = 0; i < actualHumans; i++)
        {
            var pos = openPositions[spawnIndex++];
            tileExists[pos.y * gameControllerComponent.numTilesX + pos.x] = true;
            tileUnitKinds.Add(TileUnitKinds.HumanUnit);
            tileUnitPositions.Add(new int3(pos.x, 1, pos.y));
            tileUnitHealth.Add(gameControllerComponent.humanStartingHealth);
            tileUnitDamage.Add(gameControllerComponent.humanDamage);
            tileUnitRoadHierarchy.Add(0); // Not a road
            tileUnitHeights.Add(0);
            tileUnitBuildingIds.Add(0);
        }

        // Zombie Units
        for (var i = 0; i < actualZombies; i++)
        {
            var pos = openPositions[spawnIndex++];
            tileExists[pos.y * gameControllerComponent.numTilesX + pos.x] = true;
            tileUnitKinds.Add(TileUnitKinds.ZombieUnit);
            tileUnitPositions.Add(new int3(pos.x, 1, pos.y));
            tileUnitHealth.Add(gameControllerComponent.zombieStartingHealth);
            tileUnitDamage.Add(gameControllerComponent.zombieDamage);
            tileUnitRoadHierarchy.Add(0); // Not a road
            tileUnitHeights.Add(0);
            tileUnitBuildingIds.Add(0);
        }

        openPositions.Dispose();
    }

    /// <summary>
    /// Builds the list of BuildingMeshData from the tile spawn lists for procedural mesh generation.
    /// </summary>
    private static NativeList<BuildingMeshData> CollectBuildingMeshData(
        ref NativeList<int3> tileUnitPositions,
        ref NativeList<TileUnitKinds> tileUnitKinds,
        ref NativeList<byte> tileUnitHeights,
        ref NativeList<ushort> tileUnitBuildingIds)
    {
        // Collect building mesh data for procedural generation using NativeList to avoid GC
        var buildingMeshDataList = new NativeList<BuildingMeshData>(Allocator.Temp);
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

        return buildingMeshDataList;
    }
}
