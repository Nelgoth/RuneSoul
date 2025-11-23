using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Object pool for terrain modification data structures to reduce GC allocations.
/// All pooled objects are reused instead of creating new instances each time.
/// </summary>
public static class ModificationDataPool
{
    // Pools for different collection types
    private static Stack<List<TerrainModificationBatch.VoxelModification>> modificationListPool = 
        new Stack<List<TerrainModificationBatch.VoxelModification>>();
    
    private static Stack<HashSet<Vector3Int>> chunkSetPool = 
        new Stack<HashSet<Vector3Int>>();
    
    private static Stack<Dictionary<Vector3Int, List<Vector3>>> densityUpdateDictPool = 
        new Stack<Dictionary<Vector3Int, List<Vector3>>>();
    
    private static Stack<List<Vector3>> worldPosListPool = 
        new Stack<List<Vector3>>();
    
    private static Stack<List<Vector3Int>> chunkListPool = 
        new Stack<List<Vector3Int>>();
    
    // Pool statistics
    private static int modificationListsCreated = 0;
    private static int chunkSetsCreated = 0;
    private static int densityDictsCreated = 0;
    private static int worldPosListsCreated = 0;
    private static int chunkListsCreated = 0;

    #region Modification List Pool
    
    public static List<TerrainModificationBatch.VoxelModification> GetModificationList()
    {
        if (modificationListPool.Count > 0)
        {
            var list = modificationListPool.Pop();
            list.Clear();
            return list;
        }
        
        modificationListsCreated++;
        return new List<TerrainModificationBatch.VoxelModification>(64);
    }
    
    public static void ReturnModificationList(List<TerrainModificationBatch.VoxelModification> list)
    {
        if (list == null) return;
        
        list.Clear();
        
        // Don't pool if we have too many
        if (modificationListPool.Count < 10)
        {
            modificationListPool.Push(list);
        }
    }
    
    #endregion

    #region Chunk Set Pool
    
    public static HashSet<Vector3Int> GetChunkSet()
    {
        if (chunkSetPool.Count > 0)
        {
            var set = chunkSetPool.Pop();
            set.Clear();
            return set;
        }
        
        chunkSetsCreated++;
        return new HashSet<Vector3Int>();
    }
    
    public static void ReturnChunkSet(HashSet<Vector3Int> set)
    {
        if (set == null) return;
        
        set.Clear();
        
        if (chunkSetPool.Count < 10)
        {
            chunkSetPool.Push(set);
        }
    }
    
    #endregion

    #region Density Update Dictionary Pool
    
    public static Dictionary<Vector3Int, List<Vector3>> GetDensityUpdateDict()
    {
        if (densityUpdateDictPool.Count > 0)
        {
            var dict = densityUpdateDictPool.Pop();
            dict.Clear();
            return dict;
        }
        
        densityDictsCreated++;
        return new Dictionary<Vector3Int, List<Vector3>>();
    }
    
    public static void ReturnDensityUpdateDict(Dictionary<Vector3Int, List<Vector3>> dict)
    {
        if (dict == null) return;
        
        // Return all lists in the dictionary first
        foreach (var list in dict.Values)
        {
            ReturnWorldPosList(list);
        }
        
        dict.Clear();
        
        if (densityUpdateDictPool.Count < 10)
        {
            densityUpdateDictPool.Push(dict);
        }
    }
    
    #endregion

    #region World Position List Pool
    
    public static List<Vector3> GetWorldPosList()
    {
        if (worldPosListPool.Count > 0)
        {
            var list = worldPosListPool.Pop();
            list.Clear();
            return list;
        }
        
        worldPosListsCreated++;
        return new List<Vector3>(32);
    }
    
    public static void ReturnWorldPosList(List<Vector3> list)
    {
        if (list == null) return;
        
        list.Clear();
        
        if (worldPosListPool.Count < 20)
        {
            worldPosListPool.Push(list);
        }
    }
    
    #endregion

    #region Chunk Coordinate List Pool
    
    public static List<Vector3Int> GetChunkList()
    {
        if (chunkListPool.Count > 0)
        {
            var list = chunkListPool.Pop();
            list.Clear();
            return list;
        }
        
        chunkListsCreated++;
        return new List<Vector3Int>(32);
    }
    
    public static void ReturnChunkList(List<Vector3Int> list)
    {
        if (list == null) return;
        
        list.Clear();
        
        if (chunkListPool.Count < 20)
        {
            chunkListPool.Push(list);
        }
    }
    
    #endregion

    #region Pool Management
    
    /// <summary>
    /// Clear all pools and reset statistics
    /// </summary>
    public static void ClearAllPools()
    {
        modificationListPool.Clear();
        chunkSetPool.Clear();
        densityUpdateDictPool.Clear();
        worldPosListPool.Clear();
        chunkListPool.Clear();
        
        modificationListsCreated = 0;
        chunkSetsCreated = 0;
        densityDictsCreated = 0;
        worldPosListsCreated = 0;
        chunkListsCreated = 0;
    }
    
    /// <summary>
    /// Get pool statistics for debugging
    /// </summary>
    public static string GetPoolStats()
    {
        return $"ModificationDataPool Stats:\n" +
               $"  Modification Lists: {modificationListPool.Count} pooled, {modificationListsCreated} created\n" +
               $"  Chunk Sets: {chunkSetPool.Count} pooled, {chunkSetsCreated} created\n" +
               $"  Density Dicts: {densityUpdateDictPool.Count} pooled, {densityDictsCreated} created\n" +
               $"  WorldPos Lists: {worldPosListPool.Count} pooled, {worldPosListsCreated} created\n" +
               $"  Chunk Lists: {chunkListPool.Count} pooled, {chunkListsCreated} created";
    }
    
    #endregion
}

