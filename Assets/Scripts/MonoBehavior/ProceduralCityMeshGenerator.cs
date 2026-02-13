using System.Collections.Generic;
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

    public void GenerateCityMesh(BuildingMeshData[] buildings, int numTilesX, int numTilesY)
    {
        ClearCity();

        if (buildings == null || buildings.Length == 0)
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

                if (!spatialCells.ContainsKey(cellKey))
                    spatialCells[cellKey] = new List<BuildingMeshData>();
                spatialCells[cellKey].Add(building);
            }
        }

        // Generate mesh for border (wall)
        if (borderBuildings.Count > 0)
        {
            var borderMesh = GenerateBatchedMesh(borderBuildings, 2.0f); // Border is taller
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
                var batch = cellBuildings.GetRange(i, count);
                var mesh = GenerateBatchedMesh(batch, 1.0f);
                var objName = batchIndex == 0
                    ? $"CityCell_{cellX}_{cellZ}"
                    : $"CityCell_{cellX}_{cellZ}_{batchIndex}";
                var obj = CreateMeshObject(objName, mesh, buildingMaterial);
                _generatedObjects.Add(obj);
                batchIndex++;
            }
        }
    }

    private Mesh GenerateBatchedMesh(List<BuildingMeshData> buildings, float baseHeight)
    {
        var buildingCount = buildings.Count;
        var vertexCount = buildingCount * 20; // 5 faces * 4 vertices (no bottom)
        var triangleCount = buildingCount * 30; // 5 faces * 2 triangles * 3 indices (no bottom)

        var vertices = new Vector3[vertexCount];
        var normals = new Vector3[vertexCount];
        var colors = new Color32[vertexCount];
        var triangles = new int[triangleCount];

        var vertexIndex = 0;
        var triangleIndex = 0;

        foreach (var building in buildings)
        {
            // Height multiplier of 1.0 makes each height level add 1 unit of height
            var height = baseHeight + building.Height * 1.0f;
            float x = building.X;
            float z = building.Z;

            // Calculate color based on height (lighter = taller for better visibility from above)
            var colorValue = (byte)math.clamp(60 + building.Height * 20, 60, 200);
            var color = new Color32(colorValue, colorValue, colorValue, 255);

            // Generate cube vertices
            AddCube(vertices, normals, colors, triangles,
                ref vertexIndex, ref triangleIndex,
                x, z, height, color);
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

    private void AddCube(Vector3[] vertices, Vector3[] normals, Color32[] colors, int[] triangles,
        ref int vertexIndex, ref int triangleIndex,
        float x, float z, float height, Color32 color)
    {
        var y = height / 2f + 0.5f; // Center the cube vertically, offset by 0.5 to sit on ground
        const float halfWidth = 0.5f;
        var halfHeight = height / 2f;

        // Front face (Z+)
        vertices[vertexIndex] = new Vector3(x - halfWidth, y - halfHeight, z + halfWidth);
        vertices[vertexIndex + 1] = new Vector3(x + halfWidth, y - halfHeight, z + halfWidth);
        vertices[vertexIndex + 2] = new Vector3(x + halfWidth, y + halfHeight, z + halfWidth);
        vertices[vertexIndex + 3] = new Vector3(x - halfWidth, y + halfHeight, z + halfWidth);
        normals[vertexIndex] = normals[vertexIndex + 1] = normals[vertexIndex + 2] = normals[vertexIndex + 3] = Vector3.forward;
        colors[vertexIndex] = colors[vertexIndex + 1] = colors[vertexIndex + 2] = colors[vertexIndex + 3] = color;
        AddQuadTriangles(triangles, ref triangleIndex, vertexIndex);
        vertexIndex += 4;

        // Back face (Z-)
        vertices[vertexIndex] = new Vector3(x + halfWidth, y - halfHeight, z - halfWidth);
        vertices[vertexIndex + 1] = new Vector3(x - halfWidth, y - halfHeight, z - halfWidth);
        vertices[vertexIndex + 2] = new Vector3(x - halfWidth, y + halfHeight, z - halfWidth);
        vertices[vertexIndex + 3] = new Vector3(x + halfWidth, y + halfHeight, z - halfWidth);
        normals[vertexIndex] = normals[vertexIndex + 1] = normals[vertexIndex + 2] = normals[vertexIndex + 3] = Vector3.back;
        colors[vertexIndex] = colors[vertexIndex + 1] = colors[vertexIndex + 2] = colors[vertexIndex + 3] = color;
        AddQuadTriangles(triangles, ref triangleIndex, vertexIndex);
        vertexIndex += 4;

        // Right face (X+)
        vertices[vertexIndex] = new Vector3(x + halfWidth, y - halfHeight, z + halfWidth);
        vertices[vertexIndex + 1] = new Vector3(x + halfWidth, y - halfHeight, z - halfWidth);
        vertices[vertexIndex + 2] = new Vector3(x + halfWidth, y + halfHeight, z - halfWidth);
        vertices[vertexIndex + 3] = new Vector3(x + halfWidth, y + halfHeight, z + halfWidth);
        normals[vertexIndex] = normals[vertexIndex + 1] = normals[vertexIndex + 2] = normals[vertexIndex + 3] = Vector3.right;
        colors[vertexIndex] = colors[vertexIndex + 1] = colors[vertexIndex + 2] = colors[vertexIndex + 3] = color;
        AddQuadTriangles(triangles, ref triangleIndex, vertexIndex);
        vertexIndex += 4;

        // Left face (X-)
        vertices[vertexIndex] = new Vector3(x - halfWidth, y - halfHeight, z - halfWidth);
        vertices[vertexIndex + 1] = new Vector3(x - halfWidth, y - halfHeight, z + halfWidth);
        vertices[vertexIndex + 2] = new Vector3(x - halfWidth, y + halfHeight, z + halfWidth);
        vertices[vertexIndex + 3] = new Vector3(x - halfWidth, y + halfHeight, z - halfWidth);
        normals[vertexIndex] = normals[vertexIndex + 1] = normals[vertexIndex + 2] = normals[vertexIndex + 3] = Vector3.left;
        colors[vertexIndex] = colors[vertexIndex + 1] = colors[vertexIndex + 2] = colors[vertexIndex + 3] = color;
        AddQuadTriangles(triangles, ref triangleIndex, vertexIndex);
        vertexIndex += 4;

        // Top face (Y+)
        vertices[vertexIndex] = new Vector3(x - halfWidth, y + halfHeight, z + halfWidth);
        vertices[vertexIndex + 1] = new Vector3(x + halfWidth, y + halfHeight, z + halfWidth);
        vertices[vertexIndex + 2] = new Vector3(x + halfWidth, y + halfHeight, z - halfWidth);
        vertices[vertexIndex + 3] = new Vector3(x - halfWidth, y + halfHeight, z - halfWidth);
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
        meshRenderer.material = material;
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
