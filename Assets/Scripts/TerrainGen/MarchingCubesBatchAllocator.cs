//v1.0.2
using Unity.Collections;
using Unity.Mathematics;
using Unity.Jobs;
using UnityEngine;
using System;

public class MarchingCubesBatchAllocator
{
    private NativeArray<float3> vertexBuffer;
    private NativeArray<int> triangleBuffer;
    private JobHandle lastJobHandle;
    private int currentCapacity;

    public bool IsCreated { get; private set; }

    // Constants for buffer management
    private const int MIN_BUFFER_SIZE = 10000;  // Minimum size increased for safety
    private const float GROWTH_FACTOR = 1.5f;   // How much to grow when resizing
    private const int MAX_RESIZE_ATTEMPTS = 3;  // Maximum number of resize attempts

    public bool IsJobRunning => !lastJobHandle.Equals(default);
    
    public void Initialize()
    {
        if (!IsCreated)
        {
            try
            {
                // Get config value but ensure it's not below our minimum
                int configSize = Mathf.Max(
                    World.Instance.Config.meshVertexBufferSize,
                    MIN_BUFFER_SIZE
                );

                currentCapacity = configSize;
                CreateBuffers(currentCapacity);
                
                lastJobHandle = default;
                IsCreated = true;
            }
            catch (System.Exception e)
            {
                Debug.LogError($"Failed to initialize MarchingCubesBatchAllocator: {e.Message}");
                Dispose();
            }
        }
    }

    private void CreateBuffers(int capacity)
    {
        if (vertexBuffer.IsCreated) vertexBuffer.Dispose();
        if (triangleBuffer.IsCreated) triangleBuffer.Dispose();

        try
        {
            vertexBuffer = new NativeArray<float3>(capacity, Allocator.Persistent, NativeArrayOptions.ClearMemory);
            triangleBuffer = new NativeArray<int>(capacity * 3, Allocator.Persistent, NativeArrayOptions.ClearMemory);

            if (!vertexBuffer.IsCreated || !triangleBuffer.IsCreated)
            {
                throw new System.InvalidOperationException("Failed to create one or more buffers");
            }
        }
        catch (System.Exception e)
        {
            Debug.LogError($"Buffer creation failed for capacity {capacity}: {e.Message}");
            throw;
        }
    }

    private bool ResizeBuffers(int requiredSize)
    {
        int newCapacity = Mathf.Max(
            Mathf.CeilToInt(currentCapacity * GROWTH_FACTOR),
            requiredSize
        );

        // Don't exceed config maximum
        newCapacity = Mathf.Min(
            newCapacity, 
            World.Instance.Config.meshVertexBufferSize
        );

        if (newCapacity <= currentCapacity)
        {
            return false;
        }

        try
        {
            var newVertexBuffer = new NativeArray<float3>(newCapacity, Allocator.Persistent, NativeArrayOptions.ClearMemory);
            var newTriangleBuffer = new NativeArray<int>(newCapacity * 3, Allocator.Persistent, NativeArrayOptions.ClearMemory);

            // Copy existing data
            if (vertexBuffer.IsCreated)
            {
                NativeArray<float3>.Copy(vertexBuffer, newVertexBuffer, vertexBuffer.Length);
                vertexBuffer.Dispose();
            }

            if (triangleBuffer.IsCreated)
            {
                NativeArray<int>.Copy(triangleBuffer, newTriangleBuffer, triangleBuffer.Length);
                triangleBuffer.Dispose();
            }

            vertexBuffer = newVertexBuffer;
            triangleBuffer = newTriangleBuffer;
            currentCapacity = newCapacity;

            Debug.Log($"Successfully resized buffers to {newCapacity}");
            return true;
        }
        catch (System.Exception e)
        {
            Debug.LogError($"Failed to resize buffers: {e.Message}");
            return false;
        }
    }

    public JobHandle ScheduleJob(MarchingCubesJob job, int arrayLength, int innerloopBatchCount)
    {
        // Complete any previous job
        if (!lastJobHandle.Equals(default))
        {
            lastJobHandle.Complete();
        }

        // Check if we need larger buffers
        int requiredVertexCount = job.vertexCount[0];
        int requiredTriangleCount = job.triangleCount[0];

        // Validate required sizes
        if (requiredVertexCount < 0 || requiredTriangleCount < 0)
        {
            Debug.LogError($"Invalid buffer requirements: Vertices={requiredVertexCount}, Triangles={requiredTriangleCount}");
            return default;
        }

        // Check if resize needed
        if (requiredVertexCount >= vertexBuffer.Length || requiredTriangleCount >= triangleBuffer.Length)
        {
            int maxRequired = Mathf.Max(requiredVertexCount, requiredTriangleCount);
            
            if (!ResizeBuffers(maxRequired))
            {
                Debug.LogError($"Failed to allocate required buffer size: {maxRequired}");
                return default;
            }
        }

        // Update job buffer references after potential resize
        job.vertexBuffer = vertexBuffer;
        job.triangleBuffer = triangleBuffer;

        lastJobHandle = job.Schedule(arrayLength, innerloopBatchCount);
        return lastJobHandle;
    }

    public void CompleteCurrentJob()
    {
        try
        {
            if (!lastJobHandle.Equals(default))
            {
                lastJobHandle.Complete();
                lastJobHandle = default;
            }
        }
        catch (Exception e)
        {
            Debug.LogError($"Error completing marching cubes job: {e.Message}");
            throw;
        }
    }

    public void Dispose()
    {
        CompleteCurrentJob();

        if (vertexBuffer.IsCreated)
        {
            vertexBuffer.Dispose();
        }

        if (triangleBuffer.IsCreated)
        {
            triangleBuffer.Dispose();
        }

        IsCreated = false;
        currentCapacity = 0;
    }

    // Accessors
    public NativeArray<float3> VertexBuffer => vertexBuffer;
    public NativeArray<int> TriangleBuffer => triangleBuffer;
}