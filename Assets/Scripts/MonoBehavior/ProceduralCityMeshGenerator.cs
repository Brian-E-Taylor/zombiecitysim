using System.Collections.Generic;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;
using Unity.Mathematics;

public struct BuildingMeshData
{
    public int X;
    public int Z;
    public byte Height;
    public ushort BuildingId;
}

public class ProceduralCityMeshGenerator : MonoBehaviour
{
    public static ProceduralCityMeshGenerator Instance { get; private set; }

    [SerializeField] private Material buildingMaterial;
    [SerializeField] private Material borderMaterial;

    // Spatial grid cell size for frustum culling optimization
    // Each cell becomes a separate mesh with tight bounds
    private const int SpatialCellSize = 16;

    private readonly List<GameObject> _generatedObjects = new();

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }
        Instance = this;
    }

    private void ClearCity()
    {
        foreach (var obj in _generatedObjects)
        {
            if (obj != null)
                Destroy(obj);
        }
        _generatedObjects.Clear();
    }

    public void GenerateCityMesh(NativeList<BuildingMeshData> buildings, int numTilesX, int numTilesY)
    {
        ClearCity();

        if (!buildings.IsCreated || buildings.Length == 0)
            return;

        // Group buildings by spatial grid cells for efficient frustum culling
        // Each cell gets its own mesh with tight bounds
        var cellsX = (numTilesX + SpatialCellSize - 1) / SpatialCellSize;
        var cellsZ = (numTilesY + SpatialCellSize - 1) / SpatialCellSize;
        var spatialCells = new Dictionary<int, List<BuildingMeshData>>();
        var borderBuildings = new List<BuildingMeshData>();

        foreach (var building in buildings)
        {
            // Border buildings (edges of map) go in a separate group
            if (building.X == 0 || building.X == numTilesX - 1 ||
                building.Z == 0 || building.Z == numTilesY - 1)
            {
                borderBuildings.Add(building);
            }
            else
            {
                // Assign to spatial cell based on position
                var cellX = building.X / SpatialCellSize;
                var cellZ = building.Z / SpatialCellSize;
                var cellKey = cellZ * cellsX + cellX;

                if (!spatialCells.TryGetValue(cellKey, out var cellList))
                {
                    cellList = new List<BuildingMeshData>();
                    spatialCells[cellKey] = cellList;
                }
                cellList.Add(building);
            }
        }

        // Generate mesh for border (wall)
        if (borderBuildings.Count > 0)
        {
            var borderMesh = GenerateBatchedMesh(borderBuildings, 0, borderBuildings.Count, 2.0f); // Border is taller
            var borderObj = CreateMeshObject("CityBorder", borderMesh, borderMaterial ?? buildingMaterial);
            _generatedObjects.Add(borderObj);
        }

        // Generate meshes for each spatial cell
        // This enables efficient frustum culling as each mesh has tight bounds
        const int maxVerticesPerMesh = 60000; // Stay under 65535 limit
        const int verticesPerCube = 20; // 5 faces (no bottom)
        const int maxBuildingsPerBatch = maxVerticesPerMesh / verticesPerCube;

        foreach (var (cellKey, cellBuildings) in spatialCells)
        {
            var cellX = cellKey % cellsX;
            var cellZ = cellKey / cellsX;

            // Split large cells into multiple meshes if needed
            var batchIndex = 0;
            for (var i = 0; i < cellBuildings.Count; i += maxBuildingsPerBatch)
            {
                var count = Mathf.Min(maxBuildingsPerBatch, cellBuildings.Count - i);
                var mesh = GenerateBatchedMesh(cellBuildings, i, count, 1.0f);
                var objName = batchIndex == 0
                    ? $"CityCell_{cellX}_{cellZ}"
                    : $"CityCell_{cellX}_{cellZ}_{batchIndex}";
                var obj = CreateMeshObject(objName, mesh, buildingMaterial);
                _generatedObjects.Add(obj);
                batchIndex++;
            }
        }
    }

    private struct MergedRun
    {
        public int StartX;
        public int Z;
        public int Width;
        public int Depth;
        public float Height;
        public Color32 Color;
    }

    private List<MergedRun> MergeBuildings(List<BuildingMeshData> buildings, int startIndex, int count, float baseHeight)
    {
        // Build grid lookup: key = (x, z) packed as long, value = index in buildings list
        var grid = new Dictionary<long, int>(count);
        for (var i = startIndex; i < startIndex + count; i++)
        {
            var b = buildings[i];
            grid[(long)b.X << 32 | (uint)b.Z] = i;
        }

        // Track which buildings have been merged
        var merged = new HashSet<long>(count);
        var runs = new List<MergedRun>();

        // Sort by Z then X for row-by-row scanning
        var sorted = new List<int>(count);
        for (var i = startIndex; i < startIndex + count; i++)
            sorted.Add(i);
        sorted.Sort((a, b) =>
        {
            var cmp = buildings[a].Z.CompareTo(buildings[b].Z);
            return cmp != 0 ? cmp : buildings[a].X.CompareTo(buildings[b].X);
        });

        foreach (var idx in sorted)
        {
            var b = buildings[idx];
            var key = (long)b.X << 32 | (uint)b.Z;
            if (merged.Contains(key))
                continue;

            // Start a new run along X
            var width = 1;
            merged.Add(key);

            // Extend right along X while same height and same BuildingId
            while (true)
            {
                var nextX = b.X + width;
                var nextKey = (long)nextX << 32 | (uint)b.Z;
                if (!grid.TryGetValue(nextKey, out var nextIdx))
                    break;
                var nextB = buildings[nextIdx];
                if (nextB.Height != b.Height || nextB.BuildingId != b.BuildingId)
                    break;
                if (!merged.Add(nextKey))
                    break;
                width++;
            }

            // 2D greedy meshing: try to extend the run in the +Z direction
            var depth = 1;
            while (true)
            {
                var nextZ = b.Z + depth;
                var canExtend = true;

                // Check if an identical run exists at nextZ: same startX, same width,
                // all cells have same Height and BuildingId, and none already merged
                for (var dx = 0; dx < width; dx++)
                {
                    var checkKey = (long)(b.X + dx) << 32 | (uint)nextZ;
                    if (!grid.TryGetValue(checkKey, out var checkIdx))
                    {
                        canExtend = false;
                        break;
                    }
                    var checkB = buildings[checkIdx];
                    if (checkB.Height != b.Height || checkB.BuildingId != b.BuildingId)
                    {
                        canExtend = false;
                        break;
                    }
                    if (merged.Contains(checkKey))
                    {
                        canExtend = false;
                        break;
                    }
                }

                if (!canExtend)
                    break;

                // Mark all cells in this Z row as merged
                for (var dx = 0; dx < width; dx++)
                {
                    var markKey = (long)(b.X + dx) << 32 | (uint)nextZ;
                    merged.Add(markKey);
                }
                depth++;
            }

            var height = baseHeight + b.Height * 1.0f;
            var colorValue = (byte)math.clamp(60 + b.Height * 20, 60, 200);

            runs.Add(new MergedRun
            {
                StartX = b.X,
                Z = b.Z,
                Width = width,
                Depth = depth,
                Height = height,
                Color = new Color32(colorValue, colorValue, colorValue, 255)
            });
        }

        return runs;
    }

    private Mesh GenerateBatchedMesh(List<BuildingMeshData> buildings, int startIndex, int count, float baseHeight)
    {
        var runs = MergeBuildings(buildings, startIndex, count, baseHeight);
        var runCount = runs.Count;
        var vertexCount = runCount * 20; // 5 faces * 4 vertices (no bottom)
        var triangleCount = runCount * 30; // 5 faces * 2 triangles * 3 indices (no bottom)

        var vertices = new Vector3[vertexCount];
        var normals = new Vector3[vertexCount];
        var colors = new Color32[vertexCount];
        var triangles = new int[triangleCount];

        var vertexIndex = 0;
        var triangleIndex = 0;

        foreach (var run in runs)
        {
            AddMergedCube(vertices, normals, colors, triangles,
                ref vertexIndex, ref triangleIndex,
                run.StartX, run.Z, run.Width, run.Depth, run.Height, run.Color);
        }

        var mesh = new Mesh
        {
            indexFormat = vertexCount > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16,
            vertices = vertices,
            normals = normals,
            colors32 = colors,
            triangles = triangles
        };
        mesh.RecalculateBounds();

        return mesh;
    }

    private void AddMergedCube(Vector3[] vertices, Vector3[] normals, Color32[] colors, int[] triangles,
        ref int vertexIndex, ref int triangleIndex,
        float x, float z, int width, int depth, float height, Color32 color)
    {
        var y = height / 2f + 0.5f; // Center the cube vertically, offset by 0.5 to sit on ground
        var xMin = x - 0.5f;
        var xMax = x + width - 0.5f;
        var halfWidthZ = depth / 2.0f;
        var zCenter = z + (depth - 1) / 2.0f;
        var halfHeight = height / 2f;

        // Front face (Z+)
        vertices[vertexIndex] = new Vector3(xMin, y - halfHeight, zCenter + halfWidthZ);
        vertices[vertexIndex + 1] = new Vector3(xMax, y - halfHeight, zCenter + halfWidthZ);
        vertices[vertexIndex + 2] = new Vector3(xMax, y + halfHeight, zCenter + halfWidthZ);
        vertices[vertexIndex + 3] = new Vector3(xMin, y + halfHeight, zCenter + halfWidthZ);
        normals[vertexIndex] = normals[vertexIndex + 1] = normals[vertexIndex + 2] = normals[vertexIndex + 3] = Vector3.forward;
        colors[vertexIndex] = colors[vertexIndex + 1] = colors[vertexIndex + 2] = colors[vertexIndex + 3] = color;
        AddQuadTriangles(triangles, ref triangleIndex, vertexIndex);
        vertexIndex += 4;

        // Back face (Z-)
        vertices[vertexIndex] = new Vector3(xMax, y - halfHeight, zCenter - halfWidthZ);
        vertices[vertexIndex + 1] = new Vector3(xMin, y - halfHeight, zCenter - halfWidthZ);
        vertices[vertexIndex + 2] = new Vector3(xMin, y + halfHeight, zCenter - halfWidthZ);
        vertices[vertexIndex + 3] = new Vector3(xMax, y + halfHeight, zCenter - halfWidthZ);
        normals[vertexIndex] = normals[vertexIndex + 1] = normals[vertexIndex + 2] = normals[vertexIndex + 3] = Vector3.back;
        colors[vertexIndex] = colors[vertexIndex + 1] = colors[vertexIndex + 2] = colors[vertexIndex + 3] = color;
        AddQuadTriangles(triangles, ref triangleIndex, vertexIndex);
        vertexIndex += 4;

        // Right face (X+)
        vertices[vertexIndex] = new Vector3(xMax, y - halfHeight, zCenter + halfWidthZ);
        vertices[vertexIndex + 1] = new Vector3(xMax, y - halfHeight, zCenter - halfWidthZ);
        vertices[vertexIndex + 2] = new Vector3(xMax, y + halfHeight, zCenter - halfWidthZ);
        vertices[vertexIndex + 3] = new Vector3(xMax, y + halfHeight, zCenter + halfWidthZ);
        normals[vertexIndex] = normals[vertexIndex + 1] = normals[vertexIndex + 2] = normals[vertexIndex + 3] = Vector3.right;
        colors[vertexIndex] = colors[vertexIndex + 1] = colors[vertexIndex + 2] = colors[vertexIndex + 3] = color;
        AddQuadTriangles(triangles, ref triangleIndex, vertexIndex);
        vertexIndex += 4;

        // Left face (X-)
        vertices[vertexIndex] = new Vector3(xMin, y - halfHeight, zCenter - halfWidthZ);
        vertices[vertexIndex + 1] = new Vector3(xMin, y - halfHeight, zCenter + halfWidthZ);
        vertices[vertexIndex + 2] = new Vector3(xMin, y + halfHeight, zCenter + halfWidthZ);
        vertices[vertexIndex + 3] = new Vector3(xMin, y + halfHeight, zCenter - halfWidthZ);
        normals[vertexIndex] = normals[vertexIndex + 1] = normals[vertexIndex + 2] = normals[vertexIndex + 3] = Vector3.left;
        colors[vertexIndex] = colors[vertexIndex + 1] = colors[vertexIndex + 2] = colors[vertexIndex + 3] = color;
        AddQuadTriangles(triangles, ref triangleIndex, vertexIndex);
        vertexIndex += 4;

        // Top face (Y+)
        vertices[vertexIndex] = new Vector3(xMin, y + halfHeight, zCenter + halfWidthZ);
        vertices[vertexIndex + 1] = new Vector3(xMax, y + halfHeight, zCenter + halfWidthZ);
        vertices[vertexIndex + 2] = new Vector3(xMax, y + halfHeight, zCenter - halfWidthZ);
        vertices[vertexIndex + 3] = new Vector3(xMin, y + halfHeight, zCenter - halfWidthZ);
        normals[vertexIndex] = normals[vertexIndex + 1] = normals[vertexIndex + 2] = normals[vertexIndex + 3] = Vector3.up;
        colors[vertexIndex] = colors[vertexIndex + 1] = colors[vertexIndex + 2] = colors[vertexIndex + 3] = color;
        AddQuadTriangles(triangles, ref triangleIndex, vertexIndex);
        vertexIndex += 4;
    }

    private void AddQuadTriangles(int[] triangles, ref int triangleIndex, int vertexStart)
    {
        // First triangle (clockwise winding for front face)
        triangles[triangleIndex++] = vertexStart;
        triangles[triangleIndex++] = vertexStart + 1;
        triangles[triangleIndex++] = vertexStart + 2;
        // Second triangle
        triangles[triangleIndex++] = vertexStart;
        triangles[triangleIndex++] = vertexStart + 2;
        triangles[triangleIndex++] = vertexStart + 3;
    }

    private GameObject CreateMeshObject(string objName, Mesh mesh, Material material)
    {
        var obj = new GameObject(objName);
        obj.transform.SetParent(transform);

        var meshFilter = obj.AddComponent<MeshFilter>();
        meshFilter.mesh = mesh;

        var meshRenderer = obj.AddComponent<MeshRenderer>();
        meshRenderer.sharedMaterial = material;
        meshRenderer.shadowCastingMode = ShadowCastingMode.On;
        meshRenderer.receiveShadows = true;

        return obj;
    }

    private void OnDestroy()
    {
        ClearCity();
        if (Instance == this)
            Instance = null;
    }
}
