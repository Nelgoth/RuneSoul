using System;
using UnityEngine;

public class ChunkGenerationTiming
{
    private Vector3Int chunkCoord;
    private string currentPhase;
    private DateTime phaseStartTime;

    public ChunkGenerationTiming(Vector3Int coord)
    {
        chunkCoord = coord;
    }

    public void StartPhase(string phaseName)
    {
        if (EnhancedBenchmarkManager.Instance != null)
        {
            // End previous phase if exists
            if (!string.IsNullOrEmpty(currentPhase))
            {
                EndPhase(currentPhase);
            }

            currentPhase = phaseName;
            phaseStartTime = DateTime.UtcNow;
        }
    }

    public void EndPhase(string phaseName)
    {
        if (EnhancedBenchmarkManager.Instance != null && 
            phaseName == currentPhase && 
            phaseStartTime != default)
        {
            double duration = (DateTime.UtcNow - phaseStartTime).TotalMilliseconds;
            EnhancedBenchmarkManager.Instance.RecordGenerationPhase(phaseName, duration);
            
            currentPhase = null;
            phaseStartTime = default;
        }
    }

    public void EndAllPhases()
    {
        if (!string.IsNullOrEmpty(currentPhase))
        {
            EndPhase(currentPhase);
        }
    }
}