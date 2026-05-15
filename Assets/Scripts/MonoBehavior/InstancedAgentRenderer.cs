using System;
using System.Runtime.InteropServices;
using Unity.Burst;
using Unity.Burst.Intrinsics;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using Unity.Rendering;
using Unity.Transforms;
using UnityEngine;
using UnityEngine.Rendering;

[StructLayout(LayoutKind.Sequential)]
public struct AgentInstance
{
    public float3 Position;
    public float Pad0;
    public float4 Color;
}

[BurstCompile]
public struct CollectAgentInstancesJob : IJobChunk
{
    [ReadOnly] public ComponentTypeHandle<LocalTransform> TransformHandle;
    [ReadOnly] public ComponentTypeHandle<URPMaterialPropertyBaseColor> ColorHandle;
    [ReadOnly] public NativeArray<int> ChunkBaseEntityIndices;
    [NativeDisableParallelForRestriction] public NativeArray<AgentInstance> Instances;

    public void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 chunkEnabledMask)
    {
        var transforms = chunk.GetNativeArray(ref TransformHandle);
        var colors = chunk.GetNativeArray(ref ColorHandle);
        var baseIndex = ChunkBaseEntityIndices[unfilteredChunkIndex];
        var count = chunk.Count;
        for (var i = 0; i < count; i++)
        {
            Instances[baseIndex + i] = new AgentInstance
            {
                Position = transforms[i].Position,
                Pad0 = 0f,
                Color = colors[i].Value,
            };
        }
    }
}

// Counting-sort agents into spatial cells. Single-threaded but Burst-compiled and trivial work
// per agent — sub-millisecond at 30k agents. Output: Sorted is laid out cell-by-cell, with the
// start of cell c at CellOffsets[c] and length CellOffsets[c+1] - CellOffsets[c].
[BurstCompile]
public struct BucketAgentsJob : IJob
{
    [ReadOnly] public NativeArray<AgentInstance> Unsorted;
    public NativeArray<AgentInstance> Sorted;
    public NativeArray<int> CellOffsets;
    public NativeArray<int> CellCounts;
    public int CellsX;
    public int CellsZ;
    public int CellSize;

    public void Execute()
    {
        var cellTotal = CellsX * CellsZ;
        for (var c = 0; c < cellTotal; c++) CellCounts[c] = 0;

        // Pass 1: count per cell.
        for (var i = 0; i < Unsorted.Length; i++)
        {
            var p = Unsorted[i].Position;
            var cell = ToCell(p);
            CellCounts[cell]++;
        }

        // Pass 2: prefix sum into CellOffsets, zero CellCounts so it can act as a write cursor.
        var sum = 0;
        for (var c = 0; c < cellTotal; c++)
        {
            CellOffsets[c] = sum;
            sum += CellCounts[c];
            CellCounts[c] = 0;
        }
        CellOffsets[cellTotal] = sum;

        // Pass 3: scatter into sorted positions. CellCounts ends back at the per-cell totals.
        for (var i = 0; i < Unsorted.Length; i++)
        {
            var inst = Unsorted[i];
            var cell = ToCell(inst.Position);
            var slot = CellOffsets[cell] + CellCounts[cell]++;
            Sorted[slot] = inst;
        }
    }

    private int ToCell(float3 p)
    {
        var cx = math.clamp((int)p.x / CellSize, 0, CellsX - 1);
        var cz = math.clamp((int)p.z / CellSize, 0, CellsZ - 1);
        return cz * CellsX + cx;
    }
}

public class InstancedAgentRenderer : MonoBehaviour
{
    public static InstancedAgentRenderer Instance { get; private set; }

    [SerializeField] private Material agentMaterial;

    private const int CellSize = 16;

    private Mesh _cubeMesh;
    private EntityQuery _humanQuery;
    private EntityQuery _zombieQuery;
    private EntityQuery _gameControllerQuery;
    private bool _queriesInitialized;

    private readonly KindBuffers _human = new();
    private readonly KindBuffers _zombie = new();

    private int _cellsX;
    private int _cellsZ;
    private Camera _mainCameraCache;
    private readonly Plane[] _frustumPlanes = new Plane[6];

    private static readonly int InstancesId = Shader.PropertyToID("_Instances");
    private static readonly int CellStartId = Shader.PropertyToID("_CellStart");

    private class KindBuffers
    {
        public GraphicsBuffer Instances;
        public GraphicsBuffer Args;
        public MaterialPropertyBlock Props;
        public int InstanceCapacity;
        public int ArgsCapacity;
        public NativeArray<int> CellOffsets;
        public NativeArray<int> CellCounts;
        public GraphicsBuffer.IndirectDrawIndexedArgs[] ArgsScratch = Array.Empty<GraphicsBuffer.IndirectDrawIndexedArgs>();
        public int[] VisibleCells = Array.Empty<int>();

        public void EnsureCellArrays(int cellsX, int cellsZ)
        {
            var cellTotal = cellsX * cellsZ;
            if (CellOffsets.IsCreated && CellOffsets.Length == cellTotal + 1)
                return;

            if (CellOffsets.IsCreated) CellOffsets.Dispose();
            if (CellCounts.IsCreated) CellCounts.Dispose();
            CellOffsets = new NativeArray<int>(cellTotal + 1, Allocator.Persistent);
            CellCounts = new NativeArray<int>(cellTotal, Allocator.Persistent);
            if (ArgsScratch.Length < cellTotal) Array.Resize(ref ArgsScratch, cellTotal);
            if (VisibleCells.Length < cellTotal) Array.Resize(ref VisibleCells, cellTotal);
        }

        public void Dispose()
        {
            Instances?.Dispose();
            Args?.Dispose();
            if (CellOffsets.IsCreated) CellOffsets.Dispose();
            if (CellCounts.IsCreated) CellCounts.Dispose();
        }
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetStatics()
    {
        Instance = null;
    }

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }
        Instance = this;

        _cubeMesh = BuildCubeMesh();
        _human.Props = new MaterialPropertyBlock();
        _zombie.Props = new MaterialPropertyBlock();
    }

    private void LateUpdate()
    {
        if (agentMaterial == null)
            return;

        var world = World.DefaultGameObjectInjectionWorld;
        if (world == null || !world.IsCreated)
            return;

        var em = world.EntityManager;
        if (!_queriesInitialized)
            InitQueries(em);

        if (!TryGetGridSize(out var gridX, out var gridZ))
            return;

        _cellsX = (gridX + CellSize - 1) / CellSize;
        _cellsZ = (gridZ + CellSize - 1) / CellSize;
        _human.EnsureCellArrays(_cellsX, _cellsZ);
        _zombie.EnsureCellArrays(_cellsX, _cellsZ);

        em.CompleteDependencyBeforeRO<LocalTransform>();
        em.CompleteDependencyBeforeRO<URPMaterialPropertyBaseColor>();

        var planes = TryComputeFrustumPlanes() ? _frustumPlanes : null;

        DrawKind(em, _humanQuery, _human, planes);
        DrawKind(em, _zombieQuery, _zombie, planes);
    }

    private void InitQueries(EntityManager em)
    {
        _humanQuery = em.CreateEntityQuery(
            ComponentType.ReadOnly<Human>(),
            ComponentType.ReadOnly<LocalTransform>(),
            ComponentType.ReadOnly<URPMaterialPropertyBaseColor>());
        _zombieQuery = em.CreateEntityQuery(
            ComponentType.ReadOnly<Zombie>(),
            ComponentType.ReadOnly<LocalTransform>(),
            ComponentType.ReadOnly<URPMaterialPropertyBaseColor>());
        _gameControllerQuery = em.CreateEntityQuery(ComponentType.ReadOnly<GameControllerComponent>());
        _queriesInitialized = true;
    }

    private bool TryGetGridSize(out int gridX, out int gridZ)
    {
        if (_gameControllerQuery.CalculateEntityCount() == 0)
        {
            gridX = gridZ = 0;
            return false;
        }
        var gc = _gameControllerQuery.GetSingleton<GameControllerComponent>();
        gridX = gc.numTilesX;
        gridZ = gc.numTilesY;
        return true;
    }

    private bool TryComputeFrustumPlanes()
    {
        if (_mainCameraCache == null) _mainCameraCache = Camera.main;
        if (_mainCameraCache == null) return false;
        GeometryUtility.CalculateFrustumPlanes(_mainCameraCache, _frustumPlanes);
        return true;
    }

    private void DrawKind(EntityManager em, EntityQuery query, KindBuffers kb, Plane[] frustumPlanes)
    {
        var count = query.CalculateEntityCount();
        if (count <= 0)
            return;

        if (kb.InstanceCapacity < count)
        {
            kb.Instances?.Dispose();
            kb.InstanceCapacity = Mathf.CeilToInt(count * 1.2f);
            kb.Instances = new GraphicsBuffer(GraphicsBuffer.Target.Structured, kb.InstanceCapacity, Marshal.SizeOf<AgentInstance>());
        }

        var cellTotal = _cellsX * _cellsZ;
        if (kb.ArgsCapacity < cellTotal)
        {
            kb.Args?.Dispose();
            kb.ArgsCapacity = cellTotal;
            kb.Args = new GraphicsBuffer(GraphicsBuffer.Target.IndirectArguments, cellTotal, GraphicsBuffer.IndirectDrawIndexedArgs.size);
        }

        var unsorted = new NativeArray<AgentInstance>(count, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);
        var sorted = new NativeArray<AgentInstance>(count, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);
        var baseIndices = query.CalculateBaseEntityIndexArray(Allocator.TempJob);

        var collectHandle = new CollectAgentInstancesJob
        {
            TransformHandle = em.GetComponentTypeHandle<LocalTransform>(true),
            ColorHandle = em.GetComponentTypeHandle<URPMaterialPropertyBaseColor>(true),
            ChunkBaseEntityIndices = baseIndices,
            Instances = unsorted,
        }.ScheduleParallel(query, default);

        new BucketAgentsJob
        {
            Unsorted = unsorted,
            Sorted = sorted,
            CellOffsets = kb.CellOffsets,
            CellCounts = kb.CellCounts,
            CellsX = _cellsX,
            CellsZ = _cellsZ,
            CellSize = CellSize,
        }.Schedule(collectHandle).Complete();

        kb.Instances.SetData(sorted, 0, 0, count);
        unsorted.Dispose();
        sorted.Dispose();
        baseIndices.Dispose();

        var indexCount = _cubeMesh.GetIndexCount(0);
        var startIdx = _cubeMesh.GetIndexStart(0);
        var baseVtx = _cubeMesh.GetBaseVertex(0);

        // Build args entries for non-empty, frustum-visible cells. CPU cull skips RenderMeshIndirect
        // dispatch entirely for off-screen cells; URP also gets the tight bounds in case it culls again.
        var drawCount = 0;
        for (var c = 0; c < cellTotal; c++)
        {
            var cnt = kb.CellCounts[c];
            if (cnt == 0)
                continue;

            if (frustumPlanes != null)
            {
                var cellBounds = CellBounds(c);
                if (!GeometryUtility.TestPlanesAABB(frustumPlanes, cellBounds))
                    continue;
            }

            kb.ArgsScratch[drawCount] = new GraphicsBuffer.IndirectDrawIndexedArgs
            {
                indexCountPerInstance = indexCount,
                instanceCount = (uint)cnt,
                startIndex = startIdx,
                baseVertexIndex = baseVtx,
                startInstance = 0,
            };
            kb.VisibleCells[drawCount] = c;
            drawCount++;
        }

        if (drawCount == 0)
            return;

        kb.Args.SetData(kb.ArgsScratch, 0, 0, drawCount);
        kb.Props.SetBuffer(InstancesId, kb.Instances);

        var rp = new RenderParams(agentMaterial)
        {
            shadowCastingMode = ShadowCastingMode.On,
            receiveShadows = true,
            matProps = kb.Props,
        };

        for (var k = 0; k < drawCount; k++)
        {
            var c = kb.VisibleCells[k];
            rp.worldBounds = CellBounds(c);
            kb.Props.SetInteger(CellStartId, kb.CellOffsets[c]);
            Graphics.RenderMeshIndirect(rp, _cubeMesh, kb.Args, 1, k);
        }
    }

    private Bounds CellBounds(int cellIndex)
    {
        var cx = cellIndex % _cellsX;
        var cz = cellIndex / _cellsX;
        const float halfCell = CellSize * 0.5f;
        return new Bounds(
            new Vector3(cx * CellSize + halfCell, 1f, cz * CellSize + halfCell),
            new Vector3(CellSize, 4f, CellSize));
    }

    private static Mesh BuildCubeMesh()
    {
        // 5 faces (no bottom): 20 verts, 30 indices. Cube spans [-0.5, 0.5] on each axis,
        // so a per-instance position of (x, 1, z) places the cube on top of the y=0.5 road
        // floor — same vertical placement as the previous Unity built-in cube prefab.
        const float h = 0.5f;
        var v = new Vector3[20];
        var n = new Vector3[20];
        var t = new int[30];
        var vi = 0;
        var ti = 0;

        void AddFace(Vector3 a, Vector3 b, Vector3 c, Vector3 d, Vector3 normal)
        {
            v[vi] = a; v[vi + 1] = b; v[vi + 2] = c; v[vi + 3] = d;
            n[vi] = n[vi + 1] = n[vi + 2] = n[vi + 3] = normal;
            t[ti++] = vi;     t[ti++] = vi + 1; t[ti++] = vi + 2;
            t[ti++] = vi;     t[ti++] = vi + 2; t[ti++] = vi + 3;
            vi += 4;
        }

        AddFace(new Vector3(-h, -h,  h), new Vector3( h, -h,  h), new Vector3( h,  h,  h), new Vector3(-h,  h,  h), Vector3.forward);
        AddFace(new Vector3( h, -h, -h), new Vector3(-h, -h, -h), new Vector3(-h,  h, -h), new Vector3( h,  h, -h), Vector3.back);
        AddFace(new Vector3( h, -h,  h), new Vector3( h, -h, -h), new Vector3( h,  h, -h), new Vector3( h,  h,  h), Vector3.right);
        AddFace(new Vector3(-h, -h, -h), new Vector3(-h, -h,  h), new Vector3(-h,  h,  h), new Vector3(-h,  h, -h), Vector3.left);
        AddFace(new Vector3(-h,  h,  h), new Vector3( h,  h,  h), new Vector3( h,  h, -h), new Vector3(-h,  h, -h), Vector3.up);

        var mesh = new Mesh { name = "AgentCube" };
        mesh.vertices = v;
        mesh.normals = n;
        mesh.triangles = t;
        mesh.RecalculateBounds();
        return mesh;
    }

    private void OnDestroy()
    {
        _human.Dispose();
        _zombie.Dispose();
        if (_cubeMesh != null)
            Destroy(_cubeMesh);
        if (Instance == this)
            Instance = null;
    }
}
