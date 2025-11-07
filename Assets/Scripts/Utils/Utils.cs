using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using System;

using System.Linq;

namespace NelsUtils {
    public class Utils {
        public Vector2 GetRandomSpawnPosition(Transform caller, float spawnRadius) {
            Vector2 randomDirection = UnityEngine.Random.insideUnitCircle.normalized;
            Vector2 spawnPosition = (Vector2)caller.position + randomDirection * spawnRadius;
            return spawnPosition;
        }

        public static Vector3 GetMouseWorldPosition()
        {
            Vector3 vec = GetMouseWorldPositionWithZ(Input.mousePosition, GetCurrentCinemachineCamera());
            vec.z = 0f;
            return vec;
        }

        public static Vector3 GetMouseWorldPositionWithZ()
        {
            return GetMouseWorldPositionWithZ(Input.mousePosition, GetCurrentCinemachineCamera());
        }

        public static Vector3 GetMouseWorldPositionWithZ(Camera worldCamera)
        {
            return GetMouseWorldPositionWithZ(Input.mousePosition, worldCamera);
        }

        public static Vector3 GetMouseWorldPositionWithZ(Vector3 screenPosition, Camera worldCamera)
        {
            Vector3 worldPosition = worldCamera.ScreenToWorldPoint(screenPosition);
            return worldPosition;
        }

        private static Camera GetCurrentCinemachineCamera(){
            // Since the main camera (with the CinemachineBrain component)
            // is used for rendering, simply return it.
            return Camera.main;
        }
        // Returns 0-255
	    public static int Hex_to_Dec(string hex) {
		    return Convert.ToInt32(hex, 16);
	    }
        // Returns a float between 0->1
	    public static float Hex_to_Dec01(string hex) {
		    return Hex_to_Dec(hex)/255f;
	    }
        public static Color GetColorFromString(string color) {
		    float red = Hex_to_Dec01(color.Substring(0,2));
		    float green = Hex_to_Dec01(color.Substring(2,2));
		    float blue = Hex_to_Dec01(color.Substring(4,2));
            float alpha = 1f;
            if (color.Length >= 8) {
                // Color string contains alpha
                alpha = Hex_to_Dec01(color.Substring(6,2));
            }
		    return new Color(red, green, blue, alpha);
	    }
        
    }
    
    public static class EnumerableExtensions
    {
        public static IEnumerable<IEnumerable<T>> Chunk<T>(this IEnumerable<T> source, int chunkSize)
        {
            var sourceList = source.ToList(); // Materialize the source
            for (int i = 0; i < sourceList.Count; i += chunkSize)
            {
                yield return sourceList.Skip(i).Take(chunkSize);
            }
        }
    }

    public static class Coord {
        // World -> Chunk coordinates
        public static Vector3Int WorldToChunkCoord(Vector3 worldPos, int chunkSize, float voxelSize) {
            return new Vector3Int(
                Mathf.FloorToInt(worldPos.x / (chunkSize * voxelSize)),
                Mathf.FloorToInt(worldPos.y / (chunkSize * voxelSize)), 
                Mathf.FloorToInt(worldPos.z / (chunkSize * voxelSize))
            );
        }

        // World -> Voxel coordinates within a chunk
        public static Vector3Int WorldToVoxelCoord(Vector3 worldPos, Vector3 chunkOrigin, float voxelSize) {
            Vector3 localPos = worldPos - chunkOrigin;
            return new Vector3Int(
                Mathf.FloorToInt(localPos.x / voxelSize),
                Mathf.FloorToInt(localPos.y / voxelSize),
                Mathf.FloorToInt(localPos.z / voxelSize)
            );
        }

        // World -> Density point coordinates 
        public static Vector3Int WorldToDensityCoord(Vector3 worldPos, Vector3 chunkOrigin, float voxelSize) {
            Vector3Int voxelCoord = WorldToVoxelCoord(worldPos, chunkOrigin, voxelSize);
            return VoxelToDensityCoord(voxelCoord);
        }

        // Voxel -> Density point coordinates (adds padding)
        public static Vector3Int VoxelToDensityCoord(Vector3Int voxelCoord) {
            return voxelCoord + Vector3Int.one;
        }

        // Get world position from chunk and local coordinates
        public static Vector3 GetWorldPosition(Vector3Int chunkCoord, Vector3Int localPos, int chunkSize, float voxelSize) {
            return new Vector3(
                chunkCoord.x * chunkSize * voxelSize + localPos.x * voxelSize,
                chunkCoord.y * chunkSize * voxelSize + localPos.y * voxelSize,
                chunkCoord.z * chunkSize * voxelSize + localPos.z * voxelSize
            );
        }
        
        // Get index for density point in flattened array
        public static int GetDensityPointIndex(Vector3Int pointCoord, int totalPointsPerAxis) {
            return pointCoord.x + totalPointsPerAxis * (pointCoord.y + totalPointsPerAxis * pointCoord.z);
        }

        // Get index for voxel in flattened array
        public static int GetVoxelIndex(Vector3Int voxelCoord, int chunkSize) {
            return voxelCoord.x + chunkSize * (voxelCoord.y + chunkSize * voxelCoord.z);
        }

        // Validate position is within chunk bounds
        public static bool IsVoxelPositionValid(Vector3Int voxelPosition, int chunkSize) {
            return voxelPosition.x >= 0 && voxelPosition.x < chunkSize &&
                   voxelPosition.y >= 0 && voxelPosition.y < chunkSize &&
                   voxelPosition.z >= 0 && voxelPosition.z < chunkSize;
        }

        // Validate density point position
        public static bool IsDensityPositionValid(Vector3Int densityPosition, int totalPointsPerAxis)
        {
            return densityPosition.x >= 0 && densityPosition.x < totalPointsPerAxis &&
                densityPosition.y >= 0 && densityPosition.y < totalPointsPerAxis &&
                densityPosition.z >= 0 && densityPosition.z < totalPointsPerAxis;
        }

        // Convert world position to chunk-local position
        public static Vector3 WorldToLocalPosition(Vector3 worldPos, Vector3Int chunkCoord, int chunkSize, float voxelSize) {
            Vector3 chunkWorldPos = GetWorldPosition(chunkCoord, Vector3Int.zero, chunkSize, voxelSize);
            return worldPos - chunkWorldPos;
        }
    }
    
    public class ChunkConfigurations
    {
        public enum ChunkStatus
        {
            None,       // Default state
            Loading,    // Chunk is being loaded
            Loaded,     // Chunk is fully loaded
            Unloading,  // Chunk is being unloaded
            Unloaded,   // Chunk is unloaded
            Saving,     // Chunk is being saved
            Saved,
            Modified,
            Error       // Error state
        }

        public class ChunkError
        {
            public string Message { get; set; }
            public string StackTrace { get; set; }
            public ChunkStatus Status { get; set; }
            public DateTime Timestamp { get; set; }
            public int RetryCount { get; set; }
        }

        public enum ChunkGenerationStage
        {
            None,
            DensityGeneration,
            VoxelInitialization,
            MarchingCubes,
            ProcessMarchOutput,
            MeshApplication,
            Complete,
            Error
        }

        [System.Flags]
        public enum ChunkStateFlags
        {
            None = 0,
            Active = 1 << 0,      // Chunk is visible/active in the scene
            Culled = 1 << 1,      // Chunk is loaded but not visible
            Modified = 1 << 2,    // Chunk has pending modifications
            Error = 1 << 3        // Error state
        }


    }

}
