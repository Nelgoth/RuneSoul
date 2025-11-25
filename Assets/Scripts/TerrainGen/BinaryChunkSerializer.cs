using System;
using System.IO;
using System.IO.Compression;
using UnityEngine;
using Unity.Collections;

/// <summary>
/// High-performance binary serialization for chunk data
/// Replaces slow JSON serialization with fast binary format
/// </summary>
public static class BinaryChunkSerializer
{
    // Magic number to identify our binary format
    private const uint MAGIC_NUMBER = 0x434E4B42; // "CKNB" (ChuNK Binary)
    private const byte FORMAT_VERSION = 1;
    
    // Flags for chunk data
    [Flags]
    public enum ChunkFlags : byte
    {
        None = 0,
        HasModifications = 1 << 0,
        IsCompressed = 1 << 1,
        HasVoxelData = 1 << 2,
        IsEmpty = 1 << 3,
        IsSolid = 1 << 4
    }

    /// <summary>
    /// Serializes chunk data to binary format
    /// </summary>
    public static byte[] Serialize(ChunkData data, bool compress = true)
    {
        if (data == null)
            throw new ArgumentNullException(nameof(data));

        using (var ms = new MemoryStream())
        using (var writer = new BinaryWriter(ms))
        {
            // Write header
            writer.Write(MAGIC_NUMBER);
            writer.Write(FORMAT_VERSION);
            
            // Write chunk coordinate
            writer.Write(data.ChunkCoordinate.x);
            writer.Write(data.ChunkCoordinate.y);
            writer.Write(data.ChunkCoordinate.z);
            
            // Determine flags
            ChunkFlags flags = ChunkFlags.None;
            if (data.HasModifiedData) flags |= ChunkFlags.HasModifications;
            if (compress) flags |= ChunkFlags.IsCompressed;
            if (data.IsEmptyChunk) flags |= ChunkFlags.IsEmpty;
            if (data.IsSolidChunk) flags |= ChunkFlags.IsSolid;
            
            // Check if we have voxel data
            bool hasVoxelData = data.VoxelData.IsCreated && data.VoxelData.Length > 0;
            if (hasVoxelData) flags |= ChunkFlags.HasVoxelData;
            
            writer.Write((byte)flags);
            
            // Write array sizes for validation
            int densityPointCount = data.DensityPoints.IsCreated ? data.DensityPoints.Length : 0;
            int voxelCount = hasVoxelData ? data.VoxelData.Length : 0;
            
            writer.Write(densityPointCount);
            writer.Write(voxelCount);
            
            // If empty or solid with no modifications, we can skip data
            if ((flags & ChunkFlags.IsEmpty) != 0 && !data.HasModifiedData)
            {
                return ms.ToArray();
            }
            
            if ((flags & ChunkFlags.IsSolid) != 0 && !data.HasModifiedData)
            {
                return ms.ToArray();
            }
            
            // Prepare data for serialization
            byte[] densityData = null;
            byte[] voxelStateData = null;
            byte[] voxelHitpointData = null;
            
            // CRITICAL FIX: Use the serialized arrays prepared by PrepareForSerialization()
            // instead of the NativeArrays that jobs might still be writing to!
            if (densityPointCount > 0)
            {
                // Use serializedDensityValues if available (safe for async serialization)
                // Otherwise fall back to NativeArray (only for backward compatibility)
                if (data.serializedDensityValues != null && data.serializedDensityValues.Length > 0)
                {
                    densityData = SerializeDensityValues(data.serializedDensityValues);
                }
                else
                {
                    densityData = SerializeDensityPoints(data.DensityPoints);
                }
            }
            
            if (hasVoxelData)
            {
                // Use serialized voxel arrays if available (safe for async serialization)
                // Otherwise fall back to NativeArray (only for backward compatibility)
                if (data.serializedVoxelStates != null && data.serializedVoxelStates.Length > 0 &&
                    data.serializedVoxelHitpoints != null && data.serializedVoxelHitpoints.Length > 0)
                {
                    SerializeVoxelArrays(data.serializedVoxelStates, data.serializedVoxelHitpoints, 
                        out voxelStateData, out voxelHitpointData);
                }
                else
                {
                    SerializeVoxelData(data.VoxelData, out voxelStateData, out voxelHitpointData);
                }
            }
            
            // Compress if requested and data is large enough to benefit
            if (compress && densityData != null && densityData.Length > 1024)
            {
                densityData = CompressData(densityData);
            }
            
            // Write density data
            if (densityData != null)
            {
                writer.Write(densityData.Length);
                writer.Write(densityData);
            }
            else
            {
                writer.Write(0);
            }
            
            // Write voxel state data
            if (voxelStateData != null)
            {
                writer.Write(voxelStateData.Length);
                writer.Write(voxelStateData);
            }
            else
            {
                writer.Write(0);
            }
            
            // Write voxel hitpoint data
            if (voxelHitpointData != null)
            {
                writer.Write(voxelHitpointData.Length);
                writer.Write(voxelHitpointData);
            }
            else
            {
                writer.Write(0);
            }
            
            return ms.ToArray();
        }
    }

    /// <summary>
    /// Deserializes chunk data from binary format
    /// </summary>
    public static bool Deserialize(byte[] data, ChunkData chunkData)
    {
        if (data == null || data.Length == 0)
            return false;
            
        if (chunkData == null)
            throw new ArgumentNullException(nameof(chunkData));

        try
        {
            using (var ms = new MemoryStream(data))
            using (var reader = new BinaryReader(ms))
            {
                // Read and validate header
                uint magic = reader.ReadUInt32();
                if (magic != MAGIC_NUMBER)
                {
                    Debug.LogError($"Invalid binary chunk format - wrong magic number: {magic:X8}");
                    return false;
                }
                
                byte version = reader.ReadByte();
                if (version != FORMAT_VERSION)
                {
                    Debug.LogWarning($"Binary chunk format version mismatch: {version} (expected {FORMAT_VERSION})");
                    // Continue anyway - might be backward compatible
                }
                
                // Read chunk coordinate
                Vector3Int coord = new Vector3Int(
                    reader.ReadInt32(),
                    reader.ReadInt32(),
                    reader.ReadInt32()
                );
                
                // Validate coordinate matches
                if (coord != chunkData.ChunkCoordinate)
                {
                    Debug.LogError($"Chunk coordinate mismatch: expected {chunkData.ChunkCoordinate}, got {coord}");
                    return false;
                }
                
                // Read flags
                ChunkFlags flags = (ChunkFlags)reader.ReadByte();
                bool isCompressed = (flags & ChunkFlags.IsCompressed) != 0;
                bool isEmpty = (flags & ChunkFlags.IsEmpty) != 0;
                bool isSolid = (flags & ChunkFlags.IsSolid) != 0;
                bool hasModifications = (flags & ChunkFlags.HasModifications) != 0;
                bool hasVoxelData = (flags & ChunkFlags.HasVoxelData) != 0;
                
                // Read array sizes
                int densityPointCount = reader.ReadInt32();
                int voxelCount = reader.ReadInt32();
                
                // Validate sizes
                chunkData.EnsureArraysCreated();
                
                if (densityPointCount != chunkData.DensityPoints.Length)
                {
                    Debug.LogError($"Density point count mismatch: expected {chunkData.DensityPoints.Length}, got {densityPointCount}");
                    return false;
                }
                
                if (hasVoxelData && voxelCount != chunkData.VoxelData.Length)
                {
                    Debug.LogError($"Voxel count mismatch: expected {chunkData.VoxelData.Length}, got {voxelCount}");
                    return false;
                }
                
                // If chunk is empty/solid with no modifications, we're done
                if ((isEmpty || isSolid) && !hasModifications)
                {
                    return true;
                }
                
                // Read density data
                int densityDataLength = reader.ReadInt32();
                if (densityDataLength > 0)
                {
                    byte[] densityData = reader.ReadBytes(densityDataLength);
                    
                    if (isCompressed)
                    {
                        densityData = DecompressData(densityData);
                    }
                    
                    DeserializeDensityPoints(densityData, chunkData.DensityPoints);
                }
                
                // Read voxel state data
                int voxelStateDataLength = reader.ReadInt32();
                if (voxelStateDataLength > 0)
                {
                    byte[] voxelStateData = reader.ReadBytes(voxelStateDataLength);
                    
                    // Read hitpoint data
                    int voxelHitpointDataLength = reader.ReadInt32();
                    byte[] voxelHitpointData = null;
                    if (voxelHitpointDataLength > 0)
                    {
                        voxelHitpointData = reader.ReadBytes(voxelHitpointDataLength);
                    }
                    
                    if (hasVoxelData)
                    {
                        DeserializeVoxelData(voxelStateData, voxelHitpointData, chunkData.VoxelData);
                    }
                }
                
                return true;
            }
        }
        catch (Exception ex)
        {
            Debug.LogError($"Error deserializing binary chunk data: {ex.Message}\n{ex.StackTrace}");
            return false;
        }
    }

    #region Helper Methods

    private static byte[] SerializeDensityPoints(NativeArray<DensityPoint> points)
    {
        // Each density point: float3 position (12 bytes) + float density (4 bytes) = 16 bytes
        // We only need to store density values since positions are deterministic
        int count = points.Length;
        byte[] data = new byte[count * sizeof(float)];
        
        using (var ms = new MemoryStream(data))
        using (var writer = new BinaryWriter(ms))
        {
            for (int i = 0; i < count; i++)
            {
                writer.Write(points[i].density);
            }
        }
        
        return data;
    }

    private static void DeserializeDensityPoints(byte[] data, NativeArray<DensityPoint> points)
    {
        using (var ms = new MemoryStream(data))
        using (var reader = new BinaryReader(ms))
        {
            for (int i = 0; i < points.Length; i++)
            {
                float density = reader.ReadSingle();
                var point = points[i];
                point.density = density;
                points[i] = point;
            }
        }
    }

    private static void SerializeVoxelData(NativeArray<Chunk.Voxel> voxels, out byte[] stateData, out byte[] hitpointData)
    {
        int count = voxels.Length;
        
        // Pack voxel states into bytes (8 voxels per byte since isActive is 0 or 1)
        int stateByteCount = (count + 7) / 8;
        stateData = new byte[stateByteCount];
        
        for (int i = 0; i < count; i++)
        {
            if (voxels[i].isActive != 0)
            {
                int byteIndex = i / 8;
                int bitIndex = i % 8;
                stateData[byteIndex] |= (byte)(1 << bitIndex);
            }
        }
        
        // Serialize hitpoints as floats
        hitpointData = new byte[count * sizeof(float)];
        using (var ms = new MemoryStream(hitpointData))
        using (var writer = new BinaryWriter(ms))
        {
            for (int i = 0; i < count; i++)
            {
                writer.Write(voxels[i].hitpoints);
            }
        }
    }

    private static void DeserializeVoxelData(byte[] stateData, byte[] hitpointData, NativeArray<Chunk.Voxel> voxels)
    {
        int count = voxels.Length;
        
        // Unpack voxel states
        for (int i = 0; i < count; i++)
        {
            int byteIndex = i / 8;
            int bitIndex = i % 8;
            
            var voxel = voxels[i];
            voxel.isActive = ((stateData[byteIndex] >> bitIndex) & 1) == 1 ? 1 : 0;
            voxels[i] = voxel;
        }
        
        // Unpack hitpoints
        if (hitpointData != null && hitpointData.Length > 0)
        {
            using (var ms = new MemoryStream(hitpointData))
            using (var reader = new BinaryReader(ms))
            {
                for (int i = 0; i < count; i++)
                {
                    var voxel = voxels[i];
                    voxel.hitpoints = reader.ReadSingle();
                    voxels[i] = voxel;
                }
            }
        }
    }

    /// <summary>
    /// Serializes density values from prepared float array (job-safe)
    /// </summary>
    private static byte[] SerializeDensityValues(float[] densityValues)
    {
        int count = densityValues.Length;
        byte[] data = new byte[count * sizeof(float)];
        
        using (var ms = new MemoryStream(data))
        using (var writer = new BinaryWriter(ms))
        {
            for (int i = 0; i < count; i++)
            {
                writer.Write(densityValues[i]);
            }
        }
        
        return data;
    }

    /// <summary>
    /// Serializes voxel data from prepared arrays (job-safe)
    /// </summary>
    private static void SerializeVoxelArrays(int[] voxelStates, float[] voxelHitpoints, 
        out byte[] stateData, out byte[] hitpointData)
    {
        int count = voxelStates.Length;
        
        // Pack voxel states into bytes (8 voxels per byte since isActive is 0 or 1)
        int stateByteCount = (count + 7) / 8;
        stateData = new byte[stateByteCount];
        
        for (int i = 0; i < count; i++)
        {
            if (voxelStates[i] != 0)
            {
                int byteIndex = i / 8;
                int bitIndex = i % 8;
                stateData[byteIndex] |= (byte)(1 << bitIndex);
            }
        }
        
        // Serialize hitpoints as floats
        hitpointData = new byte[count * sizeof(float)];
        using (var ms = new MemoryStream(hitpointData))
        using (var writer = new BinaryWriter(ms))
        {
            for (int i = 0; i < count; i++)
            {
                writer.Write(voxelHitpoints[i]);
            }
        }
    }

    private static byte[] CompressData(byte[] data)
    {
        using (var output = new MemoryStream())
        {
            using (var gzip = new GZipStream(output, CompressionMode.Compress))
            {
                gzip.Write(data, 0, data.Length);
            }
            return output.ToArray();
        }
    }

    private static byte[] DecompressData(byte[] data)
    {
        using (var input = new MemoryStream(data))
        using (var gzip = new GZipStream(input, CompressionMode.Decompress))
        using (var output = new MemoryStream())
        {
            gzip.CopyTo(output);
            return output.ToArray();
        }
    }

    #endregion

    /// <summary>
    /// Checks if a file is in binary format
    /// </summary>
    public static bool IsBinaryFormat(string filePath)
    {
        if (!File.Exists(filePath))
            return false;
            
        try
        {
            using (var fs = File.OpenRead(filePath))
            using (var reader = new BinaryReader(fs))
            {
                if (fs.Length < 4)
                    return false;
                    
                uint magic = reader.ReadUInt32();
                return magic == MAGIC_NUMBER;
            }
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Gets the estimated compressed size ratio
    /// </summary>
    public static float GetCompressionRatio(byte[] originalData)
    {
        if (originalData == null || originalData.Length == 0)
            return 1.0f;
            
        byte[] compressed = CompressData(originalData);
        return (float)compressed.Length / originalData.Length;
    }
}

