//v1.0.0
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Mathematics;

[BurstCompile(FloatPrecision.Low, FloatMode.Fast)]
public unsafe struct MarchingCubesJob : IJobParallelFor
{
    [ReadOnly] public NativeArray<DensityPoint> densityPoints;
    [NativeDisableParallelForRestriction] public NativeArray<float3> vertexBuffer;
    [NativeDisableParallelForRestriction] public NativeArray<int> triangleBuffer;
    [NativeDisableParallelForRestriction] public NativeArray<int> vertexCount;
    [NativeDisableParallelForRestriction] public NativeArray<int> triangleCount;
    [NativeDisableParallelForRestriction] public NativeArray<Chunk.Voxel> voxelArray;
    public int chunkSize;
    public float surfaceLevel;

    private const int BATCH_SIZE = 64;

    private static readonly float3 INVALID_VERTEX = new float3(-1, -1, -1);

    [BurstCompile]
    public void Execute(int index)
    {
        int x = index % chunkSize;
        int y = (index / chunkSize) % chunkSize;
        int z = index / (chunkSize * chunkSize);

        MarchCube(x, y, z, index);
    }

    [BurstCompile]
    private unsafe void MarchCube(int x, int y, int z, int index)
    {
        var vertexCountPtr = (int*)NativeArrayUnsafeUtility.GetUnsafePtr(vertexCount);
        var triangleCountPtr = (int*)NativeArrayUnsafeUtility.GetUnsafePtr(triangleCount);

        float3* cornerPositions = stackalloc float3[8];
        float* cornerDensities = stackalloc float[8];
        int* edgeVertexIndices = stackalloc int[12];
        bool* processedEdges = stackalloc bool[12];

        for (int i = 0; i < 12; i++)
        {
            edgeVertexIndices[i] = -1;
            processedEdges[i] = false;
        }

        GetCornerPositionsAndDensities(x, y, z, cornerPositions, cornerDensities);
        int cubeIndex = GetCubeIndex(cornerDensities);
        
        int voxelIndex = x + chunkSize * (y + chunkSize * z);
        bool isUnderground = IsUnderground(cornerDensities);

        voxelArray[voxelIndex] = new Chunk.Voxel(
            (cubeIndex != 0 || isUnderground) ? Chunk.VOXEL_ACTIVE : Chunk.VOXEL_INACTIVE,
            (cubeIndex != 0 || isUnderground) ? 3 : 0
        );

        if (cubeIndex == 0 || MarchingCubesLookupTables.edgeTable[cubeIndex] == 0)
            return;

        ProcessEdgesAndGenerateVertices(cubeIndex, cornerPositions, cornerDensities, edgeVertexIndices, processedEdges, vertexCountPtr);
        GenerateTriangles(cubeIndex, edgeVertexIndices, triangleCountPtr);
    }

    [BurstCompile]
    private void GetCornerPositionsAndDensities(int x, int y, int z, float3* cornerPositions, float* cornerDensities)
    {
        for (int i = 0; i < 8; i++)
        {
            cornerPositions[i] = GetCornerPosition(x, y, z, i);
            cornerDensities[i] = GetDensityAtPosition(x, y, z, i);
        }
    }

    private bool IsUnderground(float* cornerDensities)
    {
        for (int i = 0; i < 8; i++)
        {
            if (cornerDensities[i] >= surfaceLevel)
                return false;
        }
        return true;
    }

    [BurstCompile]
    private void ProcessEdgesAndGenerateVertices(int cubeIndex, float3* cornerPositions, float* cornerDensities, 
        int* edgeVertexIndices, bool* processedEdges, int* vertexCountPtr)
    {
        int edgeFlags = MarchingCubesLookupTables.edgeTable[cubeIndex];
        float edgeEpsilon = 1e-5f; // Tolerance for edge matching
        
        for (int i = 0; i < 12; i++)
        {
            if ((edgeFlags & (1 << i)) == 0) continue;
            if (processedEdges[i]) continue;

            int cornerA = MarchingCubesLookupTables.cornerIndexAFromEdge[i];
            int cornerB = MarchingCubesLookupTables.cornerIndexBFromEdge[i];

            // More precise edge cases
            float densityDelta = math.abs(cornerDensities[cornerB] - cornerDensities[cornerA]);
            if (densityDelta < edgeEpsilon)
            {
                float3 vertexEdge = (cornerPositions[cornerA] + cornerPositions[cornerB]) * 0.5f;
                if (!math.all(vertexEdge == INVALID_VERTEX))
                {
                    int newVertexIndex = System.Threading.Interlocked.Increment(ref *vertexCountPtr) - 1;
                    edgeVertexIndices[i] = newVertexIndex;
                    if (newVertexIndex < vertexBuffer.Length)
                    {
                        vertexBuffer[newVertexIndex] = vertexEdge;
                    }
                }
                continue;
            }

            float3 vertex = InterpolateVertex(
                cornerPositions[cornerA], 
                cornerPositions[cornerB],
                cornerDensities[cornerA],
                cornerDensities[cornerB]
            );

            if (!math.all(vertex == INVALID_VERTEX))
            {
                int newVertexIndex = System.Threading.Interlocked.Increment(ref *vertexCountPtr) - 1;
                edgeVertexIndices[i] = newVertexIndex;

                if (newVertexIndex < vertexBuffer.Length)
                {
                    vertexBuffer[newVertexIndex] = vertex;
                }
            }

            processedEdges[i] = true;
        }
    }

    [BurstCompile]
    private void GenerateTriangles(int cubeIndex, int* edgeVertexIndices, int* triangleCountPtr)
    {
        for (int i = 0; MarchingCubesLookupTables.GetTriangleTableValue(cubeIndex, i) != -1; i += 3)
        {
            int triStart = System.Threading.Interlocked.Add(ref *triangleCountPtr, 3) - 3;
            
            // Safety check - if we're about to exceed buffer size, stop
            if (triStart + 2 >= triangleBuffer.Length)
            {
                break;
            }
            
            if (triStart + 2 < triangleBuffer.Length)
            {
                triangleBuffer[triStart] = edgeVertexIndices[MarchingCubesLookupTables.GetTriangleTableValue(cubeIndex, i + 2)];
                triangleBuffer[triStart + 1] = edgeVertexIndices[MarchingCubesLookupTables.GetTriangleTableValue(cubeIndex, i + 1)];
                triangleBuffer[triStart + 2] = edgeVertexIndices[MarchingCubesLookupTables.GetTriangleTableValue(cubeIndex, i)];
            }
        }
    }

    private unsafe float3 GetCornerPosition(int x, int y, int z, int cornerIndex)
    {
        switch (cornerIndex)
        {
            case 0: return new float3(x, y, z);
            case 1: return new float3(x + 1, y, z);
            case 2: return new float3(x + 1, y + 1, z);
            case 3: return new float3(x, y + 1, z);
            case 4: return new float3(x, y, z + 1);
            case 5: return new float3(x + 1, y, z + 1);
            case 6: return new float3(x + 1, y + 1, z + 1);
            case 7: return new float3(x, y + 1, z + 1);
            default: return INVALID_VERTEX;
        }
    }

    [BurstCompile]
    private float GetDensityAtPosition(int x, int y, int z, int cornerIndex)
    {
        int totalPointsPerAxis = chunkSize + 1;
        int xCoord = x, yCoord = y, zCoord = z;

        switch (cornerIndex)
        {
            case 1: xCoord += 1; break;
            case 2: xCoord += 1; yCoord += 1; break;
            case 3: yCoord += 1; break;
            case 4: zCoord += 1; break;
            case 5: xCoord += 1; zCoord += 1; break;
            case 6: xCoord += 1; yCoord += 1; zCoord += 1; break;
            case 7: yCoord += 1; zCoord += 1; break;
        }

        int index = xCoord + totalPointsPerAxis * (yCoord + totalPointsPerAxis * zCoord);
        return densityPoints[index].density;
    }

    [BurstCompile]
    private unsafe int GetCubeIndex(float* cornerDensities)
    {
        int cubeIndex = 0;
        float epsilon = 1e-5f; // Add small tolerance for boundary cases
        
        for (int i = 0; i < 8; i++)
        {
            if (cornerDensities[i] < surfaceLevel - epsilon)
                cubeIndex |= 1 << i;
        }
        return cubeIndex;
    }

    [BurstCompile]
    private float3 InterpolateVertex(float3 p1, float3 p2, float valp1, float valp2)
    {
        float delta = valp2 - valp1;
        if (math.abs(delta) < 1e-6f)
            return (p1 + p2) * 0.5f;

        float mu = (surfaceLevel - valp1) / delta;
        mu = math.clamp(mu, 0f, 1f);
        
        // Add small offset to avoid gaps
        float epsilon = 0.001f;
        if (mu < epsilon || mu > (1 - epsilon))
        {
            return mu < 0.5f ? p1 : p2;
        }

        return p1 + mu * (p2 - p1);
    }
}