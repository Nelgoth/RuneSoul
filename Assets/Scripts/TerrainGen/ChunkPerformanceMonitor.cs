using UnityEngine;
using System.Collections.Generic;
using System;

public class ChunkPerformanceMonitor : MonoBehaviour
{
    public static ChunkPerformanceMonitor Instance { get; private set; }
    
    private const int HISTORY_LENGTH = 100;
    private Queue<float> generationTimes = new Queue<float>();
    private Queue<int> activeChunkCounts = new Queue<int>();
    private float lastCleanupTime;
    private const float CLEANUP_INTERVAL = 60f; // 1 minute

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }
        Instance = this;
        DontDestroyOnLoad(gameObject);
    }

    private void Update()
    {
        // Periodic cleanup check
        if (Time.time - lastCleanupTime > CLEANUP_INTERVAL)
        {
            TriggerCleanup();
            lastCleanupTime = Time.time;
        }
    }

    public void RecordGenerationTime(float time, int activeChunks)
    {
        while (generationTimes.Count >= HISTORY_LENGTH)
            generationTimes.Dequeue();
        while (activeChunkCounts.Count >= HISTORY_LENGTH)
            activeChunkCounts.Dequeue();

        generationTimes.Enqueue(time);
        activeChunkCounts.Enqueue(activeChunks);

        // Log if we detect significant slowdown
        float avgTime = CalculateAverageGenerationTime();
        if (time > avgTime * 2 && generationTimes.Count > 10)
        {
            Debug.LogWarning($"Chunk generation slowdown detected! Current: {time:F2}ms, Avg: {avgTime:F2}ms");
            LogMemoryStats();
        }
    }

    private float CalculateAverageGenerationTime()
    {
        if (generationTimes.Count == 0) return 0;
        float sum = 0;
        foreach (float time in generationTimes)
            sum += time;
        return sum / generationTimes.Count;
    }

    private void TriggerCleanup()
    {
        // Force garbage collection
        System.GC.Collect();
        System.GC.WaitForPendingFinalizers();
                
        LogMemoryStats();
    }

    private void LogMemoryStats()
    {
        long totalMemory = GC.GetTotalMemory(false);
        Debug.Log($"Memory Usage: {totalMemory / 1024 / 1024}MB");
        Debug.Log($"Active Chunks: {(activeChunkCounts.Count > 0 ? activeChunkCounts.Peek() : 0)}");
        Debug.Log($"Average Generation Time: {CalculateAverageGenerationTime():F2}ms");
    }
}