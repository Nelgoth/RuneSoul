using UnityEngine;
using Unity.Mathematics;
using System;

public class MeshProcessor : MonoBehaviour
{
    private static MeshProcessor instance;
    public static MeshProcessor Instance
    {
        get
        {
            if (instance == null)
            {
                var go = new GameObject("MeshProcessor");
                instance = go.AddComponent<MeshProcessor>();
                DontDestroyOnLoad(go);
                instance.Initialize();
            }
            return instance;
        }
    }

    private ComputeShader meshProcessingShader;
    private const int THREAD_GROUP_SIZE = 128;
    private bool isInitialized = false;

    private struct VertexData
    {
        public Vector3 position;
        public Vector3 normal;
        public Vector2 uv;
    }

    private ComputeBuffer verticesBuffer;
    private ComputeBuffer trianglesBuffer;
    private ComputeBuffer processedVerticesBuffer;

    private void Initialize()
    {
        if (isInitialized) return;

        try
        {
            meshProcessingShader = Resources.Load<ComputeShader>("MeshProcessing");
            if (!ValidateComputeShader())
            {
                Debug.LogError("Failed to initialize MeshProcessor - compute shader validation failed");
                return;
            }

            isInitialized = true;
            Debug.Log("MeshProcessor initialized successfully");
        }
        catch (Exception e)
        {
            Debug.LogError($"Error during MeshProcessor initialization: {e.Message}\n{e.StackTrace}");
        }
    }

    private bool ValidateComputeShader()
    {
        if (meshProcessingShader == null)
        {
            Debug.LogError("MeshProcessing compute shader not found in Resources folder!");
            return false;
        }

        try
        {
            int kernel = meshProcessingShader.FindKernel("ProcessMesh");
            uint x, y, z;
            meshProcessingShader.GetKernelThreadGroupSizes(kernel, out x, out y, out z);
            Debug.Log($"Compute shader kernel thread group sizes: {x}, {y}, {z}");
            return true;
        }
        catch (Exception e)
        {
            Debug.LogError($"Compute shader validation failed: {e.Message}");
            return false;
        }
    }

    private void OnDestroy()
    {
        ReleaseBuffers();
    }

    private void ReleaseBuffers()
    {
        if (verticesBuffer != null) verticesBuffer.Release();
        if (trianglesBuffer != null) trianglesBuffer.Release();
        if (processedVerticesBuffer != null) processedVerticesBuffer.Release();

        verticesBuffer = null;
        trianglesBuffer = null;
        processedVerticesBuffer = null;
    }

    public void ProcessMesh(Vector3[] vertices, int[] triangles, Vector3 worldOffset, float uvScale,
        float smoothingAngle, float smoothingFactor, out Vector3[] normals, out Vector2[] uvs)
    {
        // Get arrays from pool
        normals = MeshArrayPool.GetNormalArray(vertices.Length);
        uvs = MeshArrayPool.GetUVArray(vertices.Length);

        if (!isInitialized || meshProcessingShader == null)
        {
            FallbackCPUProcessing(vertices, triangles, worldOffset, uvScale, ref normals, ref uvs);
            return;
        }

        // Parameter validation and adjustment
        smoothingAngle = Mathf.Clamp(smoothingAngle, 0f, 180f);
        smoothingFactor = Mathf.Max(1f, smoothingFactor); // Ensure minimum of 1
        uvScale = Mathf.Max(0.0001f, uvScale); // Prevent zero scale

        try
        {
            int kernel = meshProcessingShader.FindKernel("ProcessMesh");
            int vertexCount = vertices.Length;
            
            // Validate sizes before proceeding
            if (vertexCount == 0 || triangles.Length == 0)
            {
                return;
            }

            // Create or resize buffers only if needed
            bool needNewBuffers = false;
            if (verticesBuffer == null || verticesBuffer.count < vertexCount)
            {
                if (verticesBuffer != null) verticesBuffer.Release();
                needNewBuffers = true;
            }
            if (trianglesBuffer == null || trianglesBuffer.count < triangles.Length)
            {
                if (trianglesBuffer != null) trianglesBuffer.Release();
                needNewBuffers = true;
            }
            if (processedVerticesBuffer == null || processedVerticesBuffer.count < vertexCount)
            {
                if (processedVerticesBuffer != null) processedVerticesBuffer.Release();
                needNewBuffers = true;
            }

            if (needNewBuffers)
            {
                verticesBuffer = new ComputeBuffer(vertexCount, sizeof(float) * 3);
                trianglesBuffer = new ComputeBuffer(triangles.Length, sizeof(int));
                processedVerticesBuffer = new ComputeBuffer(vertexCount, sizeof(float) * 8); // pos + normal + uv
            }

            // Convert vertices to float array and check for invalid values
            float[] vertexData = new float[vertexCount * 3];
            bool hasInvalidVertices = false;
            for (int i = 0; i < vertexCount; i++)
            {
                Vector3 vertex = vertices[i];
                if (float.IsNaN(vertex.x) || float.IsNaN(vertex.y) || float.IsNaN(vertex.z))
                {
                    vertex = Vector3.zero;
                    hasInvalidVertices = true;
                }
                int baseIndex = i * 3;
                vertexData[baseIndex] = vertex.x;
                vertexData[baseIndex + 1] = vertex.y;
                vertexData[baseIndex + 2] = vertex.z;
            }

            if (hasInvalidVertices)
            {
                Debug.LogWarning("Invalid vertices found and replaced with zero vectors");
            }

            // Set buffer data
            verticesBuffer.SetData(vertexData);
            trianglesBuffer.SetData(triangles);

            // Set compute shader parameters
            meshProcessingShader.SetBuffer(kernel, "Vertices", verticesBuffer);
            meshProcessingShader.SetBuffer(kernel, "Triangles", trianglesBuffer);
            meshProcessingShader.SetBuffer(kernel, "ProcessedVertices", processedVerticesBuffer);
            meshProcessingShader.SetInt("VertexCount", vertexCount);
            meshProcessingShader.SetInt("TriangleCount", triangles.Length);
            meshProcessingShader.SetVector("WorldOffset", worldOffset);
            meshProcessingShader.SetFloat("UVScale", uvScale);
            meshProcessingShader.SetFloat("NormalSmoothingAngle", smoothingAngle);
            meshProcessingShader.SetFloat("NormalSmoothingFactor", smoothingFactor);

            // Dispatch compute shader with optimal thread group size
            int threadGroupSize = THREAD_GROUP_SIZE;
            int numThreadGroups = Mathf.CeilToInt((float)vertexCount / threadGroupSize);
            meshProcessingShader.Dispatch(kernel, numThreadGroups, 1, 1);

            // Get processed data
            VertexData[] processedVertices = new VertexData[vertexCount];
            processedVerticesBuffer.GetData(processedVertices);

            // Process and validate results
            int invalidNormalCount = 0;
            for (int i = 0; i < vertexCount; i++)
            {
                Vector3 normal = processedVertices[i].normal;
                Vector2 uv = processedVertices[i].uv;

                // Validate normal
                float normalMagnitude = normal.magnitude;
                if (normalMagnitude < 0.1f || float.IsNaN(normalMagnitude))
                {
                    invalidNormalCount++;
                    // Calculate fallback normal from triangle
                    int triIndex = (i / 3) * 3;
                    if (triIndex + 2 < triangles.Length)
                    {
                        Vector3 v0 = vertices[triangles[triIndex]];
                        Vector3 v1 = vertices[triangles[triIndex + 1]];
                        Vector3 v2 = vertices[triangles[triIndex + 2]];
                        normal = Vector3.Cross(v1 - v0, v2 - v0).normalized;
                    }
                    else
                    {
                        normal = Vector3.up;
                    }
                }
                else
                {
                    normal = normal.normalized;
                }

                // Validate UV
                if (float.IsNaN(uv.x) || float.IsNaN(uv.y))
                {
                    uv = Vector2.zero;
                }

                normals[i] = normal;
                uvs[i] = uv;
            }

            if (invalidNormalCount > 0)
            {
                Debug.LogWarning($"Found {invalidNormalCount} invalid normals that were corrected");
            }
        }
        catch (Exception e)
        {
            Debug.LogError($"Error in ProcessMesh: {e.Message}\n{e.StackTrace}");
            FallbackCPUProcessing(vertices, triangles, worldOffset, uvScale, ref normals, ref uvs);
        }
    }

    private void FallbackCPUProcessing(Vector3[] vertices, int[] triangles, Vector3 worldOffset, 
        float uvScale, ref Vector3[] normals, ref Vector2[] uvs)
    {
        Debug.LogWarning("Using CPU fallback for mesh processing");
        
        // Get mesh from pool instead of creating new
        Mesh tempMesh = MeshDataPool.Instance.GetMesh();
        tempMesh.vertices = vertices;
        tempMesh.triangles = triangles;
        tempMesh.RecalculateNormals();

        normals = tempMesh.normals;
        uvs = new Vector2[vertices.Length];

        // Basic triplanar UVs
        Vector3[] norms = tempMesh.normals;
        for (int i = 0; i < vertices.Length; i++)
        {
            Vector3 worldPos = vertices[i] + worldOffset;
            Vector3 normal = norms[i];
            float scale = uvScale;

            uvs[i] = new Vector2(
                (worldPos.x * normal.x + worldPos.z * normal.z) * scale,
                (worldPos.y * normal.y + worldPos.z * normal.z) * scale
            );
        }

        // Return mesh to pool
        MeshDataPool.Instance.ReturnMesh(tempMesh);
    }
}