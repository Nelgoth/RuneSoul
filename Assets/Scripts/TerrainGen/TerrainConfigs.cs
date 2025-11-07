// REPLACE ENTIRE FILE
using UnityEngine;

[CreateAssetMenu(fileName = "TerrainConfigs", menuName = "Voxel/TerrainConfigs")]
public class TerrainConfigs : ScriptableObject
{
    [Header("Core Settings")]
    [Tooltip("Size of each chunk in voxels")]
    public int chunkSize = 16;
    public float voxelSize = 1.0f;
    public float surfaceLevel = 0;

    [Header("Memory Management")]
    [Tooltip("Initial mesh pool size")]
    public int initialMeshPoolSize = 50;
    [Tooltip("Base vertex buffer size per mesh")]
    public int meshVertexBufferSize = 10000;
    [Tooltip("Maximum memory for mesh cache (MB)")]
    public long MaxMeshCacheSize = 256L * 1024L * 1024L;
    [Tooltip("Memory pressure threshold for cleanup")]
    [Range(0.5f, 0.95f)]
    public float memoryPressureThreshold = 0.8f;
    [Tooltip("Target memory usage after cleanup (% of max)")]
    [Range(0.3f, 0.7f)]
    public float targetMemoryUsage = 0.5f;
    
    [Header("Chunk Loading")]
    public int LoadRadius = 5;
    public int UnloadRadius = 7;
    public int VerticalLoadRadius = 2;
    public int VerticalUnloadRadius = 3;
    [Tooltip("Dynamic chunks processed per frame")]
    public int ChunksPerFrame = 1;
    public int ChunksPerFrameUnloading = 1;

    [Header("Generation")]
    [Tooltip("Base terrain height")]
    public float maxHeight = 64f;
    public int noiseSeed = 1234;
    public float noiseScale = 0.015f;
    public float frequency = 0.015f;
    [Tooltip("Max retries before quarantine")]
    public int MaxJobRetries = 3;

    [Header("Terrain Modification")]
    public float densityInfluenceRadius = 3.5f;
    public float baseDensityStrength = 3.0f;
    public float minDensityChangeThreshold = 0.01f;
    [Tooltip("Interval between updates")]
    public float updateInterval = 0.1f;
    public float voxelEpsilon = 0.01f;
    public float densityFalloffCutoff = 0.1f;
    [Tooltip("Maximum range at which players can modify terrain")]
    public float modificationRange = 10f;

    [Header("Mesh Generation")]
    [Range(0f, 180f)]
    public float normalSmoothingAngle = 120f;
    [Range(1f, 4f)] 
    public float normalSmoothingFactor = 2.5f;
    [Tooltip("Minimum area for valid triangle")]
    public float minTriangleArea = 1e-6f;
    [Tooltip("Maximum mesh bounds magnitude")]
    public float maxMeshBoundsMagnitude = 10000f;

    [Header("Performance")]
    [Tooltip("Maximum concurrent jobs")]
    public int MaxConcurrentJobs = 8;
    [Tooltip("Mesh generation batch size")]
    public int MeshBatchSize = 64;
    [Tooltip("Target FPS")]
    [Range(30, 120)]
    public int TargetFPS = 60;
    [Tooltip("Initial vertex buffer size per mesh")]
    

    [Header("Visual Settings")]
    public Material VoxelMaterial;
    public float triplanarScale = 1f;
    public float triplanarBlend = 1f;

    [Header("Terrain Analysis Settings")]
    public float AnalysisCacheExpirationDays = 30f; // Default to 30 days
    public bool PermanentlyStoreEmptyChunks = true; // Never expire empty chunk data
      
    private void OnValidate()
    {
        // Ensure reasonable minimums
        meshVertexBufferSize = Mathf.Max(1000, meshVertexBufferSize);
        ChunksPerFrame = Mathf.Max(1, ChunksPerFrame);
        
        // Scale vertex buffer with chunk size
        int expectedVertices = chunkSize * chunkSize * chunkSize / 4;
        meshVertexBufferSize = Mathf.Max(meshVertexBufferSize, expectedVertices);
    }
}