using UnityEngine;
using System.Collections.Generic;
using System.Text;
using NelsUtils;

/// <summary>
/// Debugging utilities for coordinate-specific chunk issues
/// Helps investigate the stubborn chunk loading bug
/// </summary>
public static class CoordinateDebugger
{
    // Track coordinates that have been problematic
    private static HashSet<Vector3Int> problematicCoords = new HashSet<Vector3Int>();
    private static Dictionary<Vector3Int, List<string>> coordinateEvents = new Dictionary<Vector3Int, List<string>>();
    private static Dictionary<Vector3Int, int> hashCollisions = new Dictionary<Vector3Int, int>();
    
    // Known problematic coordinates from user's memory
    private static readonly Vector3Int[] knownProblematicCoords = new Vector3Int[]
    {
        new Vector3Int(-5, -1, -1),
        // Add more as discovered
    };
    
    static CoordinateDebugger()
    {
        // Initialize with known problematic coordinates
        foreach (var coord in knownProblematicCoords)
        {
            problematicCoords.Add(coord);
            coordinateEvents[coord] = new List<string>();
        }
    }
    
    /// <summary>
    /// Logs an event for a specific coordinate
    /// </summary>
    public static void LogEvent(Vector3Int coord, string eventType, string details = "")
    {
        if (!coordinateEvents.ContainsKey(coord))
        {
            coordinateEvents[coord] = new List<string>();
        }
        
        string timestamp = Time.time.ToString("F2");
        string message = $"[{timestamp}] {eventType}: {details}";
        coordinateEvents[coord].Add(message);
        
        // Also log to Unity console if this is a problematic coordinate
        if (IsProblematicCoordinate(coord))
        {
            Debug.Log($"[CoordDebug:{coord}] {message}");
        }
    }
    
    /// <summary>
    /// Checks if a coordinate is known to be problematic
    /// </summary>
    public static bool IsProblematicCoordinate(Vector3Int coord)
    {
        return problematicCoords.Contains(coord);
    }
    
    /// <summary>
    /// Marks a coordinate as problematic
    /// </summary>
    public static void MarkAsProblematic(Vector3Int coord, string reason)
    {
        if (!problematicCoords.Contains(coord))
        {
            problematicCoords.Add(coord);
            coordinateEvents[coord] = new List<string>();
            Debug.LogWarning($"[CoordDebug] Marking {coord} as problematic: {reason}");
        }
    }
    
    /// <summary>
    /// Verifies coordinate transformation for a world position
    /// </summary>
    public static void VerifyCoordinateTransform(Vector3 worldPos, int chunkSize, float voxelSize)
    {
        Vector3Int calculated = Coord.WorldToChunkCoord(worldPos, chunkSize, voxelSize);
        Vector3 backToWorld = Coord.GetWorldPosition(calculated, chunkSize, voxelSize);
        
        float distance = Vector3.Distance(worldPos, backToWorld);
        
        if (distance > chunkSize * voxelSize * 1.5f)
        {
            Debug.LogWarning($"[CoordDebug] Large transformation error:\n" +
                $"  World: {worldPos}\n" +
                $"  Coord: {calculated}\n" +
                $"  BackToWorld: {backToWorld}\n" +
                $"  Distance: {distance}");
        }
    }
    
    /// <summary>
    /// Tests for hash collisions in Vector3Int
    /// </summary>
    public static void TestHashCollisions(Vector3Int coord)
    {
        int hash = coord.GetHashCode();
        
        if (!hashCollisions.ContainsKey(coord))
        {
            hashCollisions[coord] = hash;
        }
        
        // Check if any other coordinate has the same hash
        foreach (var entry in hashCollisions)
        {
            if (entry.Key != coord && entry.Value == hash)
            {
                Debug.LogError($"[CoordDebug] HASH COLLISION DETECTED!\n" +
                    $"  Coord1: {coord} (hash: {hash})\n" +
                    $"  Coord2: {entry.Key} (hash: {entry.Value})");
            }
        }
    }
    
    /// <summary>
    /// Verifies neighbor coordinate calculations
    /// </summary>
    public static void VerifyNeighborCalculations(Vector3Int coord)
    {
        Vector3Int[] neighbors = new Vector3Int[]
        {
            coord + Vector3Int.right,
            coord + Vector3Int.left,
            coord + Vector3Int.up,
            coord + Vector3Int.down,
            coord + Vector3Int.forward,
            coord + Vector3Int.back
        };
        
        StringBuilder sb = new StringBuilder();
        sb.AppendLine($"[CoordDebug] Neighbors of {coord}:");
        
        foreach (var neighbor in neighbors)
        {
            Vector3Int diff = neighbor - coord;
            int distance = Mathf.Abs(diff.x) + Mathf.Abs(diff.y) + Mathf.Abs(diff.z);
            
            sb.AppendLine($"  {neighbor} (distance: {distance})");
            
            if (distance != 1)
            {
                Debug.LogError($"[CoordDebug] Invalid neighbor distance: {distance} (expected 1)");
            }
        }
        
        Debug.Log(sb.ToString());
    }
    
    /// <summary>
    /// Verifies distance calculations for negative coordinates
    /// </summary>
    public static void VerifyDistanceCalculation(Vector3Int coord1, Vector3Int coord2)
    {
        float distance = Vector3Int.Distance(coord1, coord2);
        float manualDistance = Mathf.Sqrt(
            (coord1.x - coord2.x) * (coord1.x - coord2.x) +
            (coord1.y - coord2.y) * (coord1.y - coord2.y) +
            (coord1.z - coord2.z) * (coord1.z - coord2.z)
        );
        
        float diff = Mathf.Abs(distance - manualDistance);
        
        if (diff > 0.001f)
        {
            Debug.LogError($"[CoordDebug] Distance calculation mismatch:\n" +
                $"  Coord1: {coord1}\n" +
                $"  Coord2: {coord2}\n" +
                $"  Distance: {distance}\n" +
                $"  Manual: {manualDistance}\n" +
                $"  Diff: {diff}");
        }
    }
    
    /// <summary>
    /// Gets the event history for a coordinate
    /// </summary>
    public static string GetEventHistory(Vector3Int coord)
    {
        if (!coordinateEvents.ContainsKey(coord) || coordinateEvents[coord].Count == 0)
        {
            return $"No events recorded for {coord}";
        }
        
        StringBuilder sb = new StringBuilder();
        sb.AppendLine($"Event history for {coord}:");
        
        foreach (var evt in coordinateEvents[coord])
        {
            sb.AppendLine($"  {evt}");
        }
        
        return sb.ToString();
    }
    
    /// <summary>
    /// Clears event history for a coordinate
    /// </summary>
    public static void ClearEventHistory(Vector3Int coord)
    {
        if (coordinateEvents.ContainsKey(coord))
        {
            coordinateEvents[coord].Clear();
        }
    }
    
    /// <summary>
    /// Runs a comprehensive diagnostic on a coordinate
    /// </summary>
    public static void RunDiagnostic(Vector3Int coord, int chunkSize, float voxelSize)
    {
        Debug.Log($"[CoordDebug] Running diagnostic on {coord}");
        
        // Test hash
        TestHashCollisions(coord);
        
        // Test coordinate transformation
        Vector3 worldPos = Coord.GetWorldPosition(coord, chunkSize, voxelSize);
        VerifyCoordinateTransform(worldPos, chunkSize, voxelSize);
        
        // Test neighbors
        VerifyNeighborCalculations(coord);
        
        // Test distances to nearby coordinates
        for (int x = -1; x <= 1; x++)
        {
            for (int y = -1; y <= 1; y++)
            {
                for (int z = -1; z <= 1; z++)
                {
                    if (x == 0 && y == 0 && z == 0) continue;
                    
                    Vector3Int other = coord + new Vector3Int(x, y, z);
                    VerifyDistanceCalculation(coord, other);
                }
            }
        }
        
        // Print event history
        Debug.Log(GetEventHistory(coord));
    }
    
    /// <summary>
    /// Gets all problematic coordinates
    /// </summary>
    public static Vector3Int[] GetProblematicCoordinates()
    {
        Vector3Int[] result = new Vector3Int[problematicCoords.Count];
        problematicCoords.CopyTo(result);
        return result;
    }
}

