using Unity.Mathematics;

[System.Serializable]
public class ChunkSaveData {
    public int3 ChunkCoordinate;
    public NelsUtils.ChunkConfigurations.ChunkStatus Status;
    
    public float[] DensityValues;     // Store densities for each density point
    public int[] VoxelStates;         // Store voxel activity (active/inactive)
    public float[] VoxelHitpoints;    // Store voxel hitpoints

    // Utility method to build save data from a ChunkData instance
    public static ChunkSaveData FromChunkData(ChunkData chunkData) {
        var saveData = new ChunkSaveData();

        saveData.ChunkCoordinate = new int3(
            chunkData.ChunkCoordinate.x,
            chunkData.ChunkCoordinate.y,
            chunkData.ChunkCoordinate.z
        );
        saveData.Status = chunkData.Status;

        // Extract densities
        var densityArr = chunkData.DensityPoints;
        saveData.DensityValues = new float[densityArr.Length];
        for (int i = 0; i < densityArr.Length; i++) {
            saveData.DensityValues[i] = densityArr[i].density;
        }

        // Extract voxel data
        var voxArr = chunkData.VoxelData;
        saveData.VoxelStates = new int[voxArr.Length];
        saveData.VoxelHitpoints = new float[voxArr.Length];
        for (int i = 0; i < voxArr.Length; i++) {
            saveData.VoxelStates[i] = voxArr[i].isActive;
            saveData.VoxelHitpoints[i] = voxArr[i].hitpoints;
        }

        return saveData;
    }
}