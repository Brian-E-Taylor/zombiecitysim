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

    private List<GameObject> generatedObjects = new List<GameObject>();

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }
        Instance = this;
    }

    public void ClearCity()
    {
        foreach (var obj in generatedObjects)
        {
            if (obj != null)
                Destroy(obj);
        }
        generatedObjects.Clear();
    }

    public void GenerateCityMesh(BuildingMeshData[] buildings, int numTilesX, int numTilesY)
    {
        ClearCity();

        if (buildings == null || buildings.Length == 0)
            return;

        // Group buildings by their BuildingId for potential batching
        var buildingGroups = new Dictionary<ushort, List<BuildingMeshData>>();
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
                if (!buildingGroups.ContainsKey(building.BuildingId))
                    buildingGroups[building.BuildingId] = new List<BuildingMeshData>();
                buildingGroups[building.BuildingId].Add(building);
            }
        }

        // Generate mesh for border (wall)
        if (borderBuildings.Count > 0)
        {
            var borderMesh = GenerateBatchedMesh(borderBuildings, 2.0f); // Border is taller
            var borderObj = CreateMeshObject("CityBorder", borderMesh, borderMaterial ?? buildingMaterial);
            generatedObjects.Add(borderObj);
        }

        // Generate meshes for building groups
        // Batch buildings with same ID together, but limit batch size for mesh vertex limits
        const int maxVerticesPerMesh = 60000; // Stay under 65535 limit
        const int verticesPerCube = 24;
        int maxBuildingsPerBatch = maxVerticesPerMesh / verticesPerCube;

        var allBuildings = new List<BuildingMeshData>();
        foreach (var group in buildingGroups.Values)
        {
            allBuildings.AddRange(group);
        }

        // Create batched meshes
        int batchIndex = 0;
        for (int i = 0; i < allBuildings.Count; i += maxBuildingsPerBatch)
        {
            int count = Mathf.Min(maxBuildingsPerBatch, allBuildings.Count - i);
            var batch = allBuildings.GetRange(i, count);
            var mesh = GenerateBatchedMesh(batch, 1.0f);
            var obj = CreateMeshObject($"CityBuildings_{batchIndex}", mesh, buildingMaterial);
            generatedObjects.Add(obj);
            batchIndex++;
        }
    }

    private Mesh GenerateBatchedMesh(List<BuildingMeshData> buildings, float baseHeight)
    {
        int buildingCount = buildings.Count;
        int vertexCount = buildingCount * 24; // 6 faces * 4 vertices
        int triangleCount = buildingCount * 36; // 6 faces * 2 triangles * 3 indices

        var vertices = new Vector3[vertexCount];
        var normals = new Vector3[vertexCount];
        var colors = new Color32[vertexCount];
        var triangles = new int[triangleCount];

        int vertexIndex = 0;
        int triangleIndex = 0;

        foreach (var building in buildings)
        {
            // Height multiplier of 1.0 makes each height level add 1 unit of height
            float height = baseHeight + building.Height * 1.0f;
            float x = building.X;
            float z = building.Z;

            // Calculate color based on height (lighter = taller for better visibility from above)
            byte colorValue = (byte)math.clamp(60 + building.Height * 20, 60, 200);
            var color = new Color32(colorValue, colorValue, colorValue, 255);

            // Generate cube vertices
            AddCube(vertices, normals, colors, triangles,
                ref vertexIndex, ref triangleIndex,
                x, z, height, color);
        }

        var mesh = new Mesh();
        mesh.indexFormat = vertexCount > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16;
        mesh.vertices = vertices;
        mesh.normals = normals;
        mesh.colors32 = colors;
        mesh.triangles = triangles;
        mesh.RecalculateBounds();

        return mesh;
    }

    private void AddCube(Vector3[] vertices, Vector3[] normals, Color32[] colors, int[] triangles,
        ref int vertexIndex, ref int triangleIndex,
        float x, float z, float height, Color32 color)
    {
        float y = height / 2f + 0.5f; // Center the cube vertically, offset by 0.5 to sit on ground
        float halfWidth = 0.5f;
        float halfHeight = height / 2f;

        int startVertex = vertexIndex;

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

        // Bottom face (Y-)
        vertices[vertexIndex] = new Vector3(x - halfWidth, y - halfHeight, z - halfWidth);
        vertices[vertexIndex + 1] = new Vector3(x + halfWidth, y - halfHeight, z - halfWidth);
        vertices[vertexIndex + 2] = new Vector3(x + halfWidth, y - halfHeight, z + halfWidth);
        vertices[vertexIndex + 3] = new Vector3(x - halfWidth, y - halfHeight, z + halfWidth);
        normals[vertexIndex] = normals[vertexIndex + 1] = normals[vertexIndex + 2] = normals[vertexIndex + 3] = Vector3.down;
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

    private GameObject CreateMeshObject(string name, Mesh mesh, Material material)
    {
        var obj = new GameObject(name);
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
