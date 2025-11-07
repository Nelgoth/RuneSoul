using UnityEngine;
using System;

[Serializable]
public class TerrainAnalysisData
{
    public TerrainAnalysisCache.SerializableVector3Int Coordinate;
    public bool IsEmpty;
    public bool IsSolid;
    public long LastAnalyzedTicks;
    public bool WasModified;

    public TerrainAnalysisData(Vector3Int coord, bool isEmpty, bool isSolid, bool wasModified = false)
    {
        // CRITICAL FIX: Disallow invalid combinations
        if (isEmpty && isSolid)
        {
            Debug.LogWarning($"Invalid parameters: chunk cannot be both empty and solid. Setting to mixed state.");
            isEmpty = false;
            isSolid = false;
        }

        Coordinate = new TerrainAnalysisCache.SerializableVector3Int(coord);
        IsEmpty = isEmpty;
        IsSolid = isSolid;
        WasModified = wasModified;
        LastAnalyzedTicks = DateTime.UtcNow.Ticks;
    }

    public DateTime LastAnalyzed => new DateTime(LastAnalyzedTicks, DateTimeKind.Utc);
}