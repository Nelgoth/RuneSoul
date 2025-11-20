using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

[BurstCompile]
public struct DensityFieldGenerationJob : IJobParallelFor
{
    [NativeDisableParallelForRestriction] 
    public NativeArray<DensityPoint> densityPoints;

    [ReadOnly] public int gridSize;
    [ReadOnly] public float maxHeight;
    [ReadOnly] public float3 chunkWorldPosition;
    [ReadOnly] public float voxelSize;
    [ReadOnly] public int seed;
    [ReadOnly] public FastNoiseLite.NoiseType noiseType;
    
    // Noise layer settings
    [ReadOnly] public float baseTerrainFrequency;
    [ReadOnly] public float baseTerrainScale;
    [ReadOnly] public float hillsFrequency;
    [ReadOnly] public float hillsScale;
    [ReadOnly] public float groundFrequency;
    [ReadOnly] public float groundScale;
    [ReadOnly] public float detailFrequency;
    [ReadOnly] public float detailScale;

    private FastNoiseLite InitializeNoise(int noiseSeed, float noiseFrequency)
    {
        FastNoiseLite noise = new FastNoiseLite();
        noise.SetSeed(noiseSeed);
        noise.SetNoiseType(noiseType);
        noise.SetFrequency(noiseFrequency);
        return noise;
    }

    private float SmoothStep(float edge0, float edge1, float x)
    {
        x = math.clamp((x - edge0) / (edge1 - edge0), 0.0f, 1.0f);
        return x * x * (3 - 2 * x);
    }

    [BurstCompile]
    public void Execute(int index)
    {
        int totalPoints = gridSize + 1;
        int x = index % totalPoints;
        int y = (index / totalPoints) % totalPoints;
        int z = index / (totalPoints * totalPoints);

        // Safety check for index
        if (index >= densityPoints.Length)
        {
            return;
        }

        float3 localPos = new float3(x, y, z);
        float3 worldPos = chunkWorldPosition + localPos * voxelSize;

        // Safety checks for invalid input values
        if (float.IsNaN(worldPos.x) || float.IsNaN(worldPos.y) || float.IsNaN(worldPos.z) ||
            float.IsInfinity(worldPos.x) || float.IsInfinity(worldPos.y) || float.IsInfinity(worldPos.z))
        {
            // Use a placeholder value to prevent NaN propagation
            densityPoints[index] = new DensityPoint(localPos, 1.0f); // 1.0f is outside surface
            return;
        }

        // Base terrain (larger features)
        var baseNoise = InitializeNoise(seed, baseTerrainFrequency);
        float baseVal = baseNoise.GetNoise(worldPos.x * baseTerrainScale, worldPos.z * baseTerrainScale);
        baseVal = (baseVal + 1f) * 0.5f;
        
        // Hills
        var hillNoise = InitializeNoise(seed + 1000, hillsFrequency);
        float hillVal = hillNoise.GetNoise(worldPos.x * hillsScale, worldPos.z * hillsScale);
        hillVal = (hillVal + 1f) * 0.5f;
        
        // Ground variation
        var groundNoise = InitializeNoise(seed + 2000, groundFrequency);
        float groundVal = groundNoise.GetNoise(worldPos.x * groundScale, worldPos.z * groundScale);
        groundVal = (groundVal + 1f) * 0.5f;

        // Surface detail
        var detailNoise = InitializeNoise(seed + 3000, detailFrequency);
        float detailVal = detailNoise.GetNoise(worldPos.x * detailScale, worldPos.z * detailScale);
        detailVal = (detailVal + 1f) * 0.5f;

        // Calculate base height using primary terrain
        float baseHeight = baseVal * maxHeight;
        float normalizedHeight = baseVal; // Use raw noise value for height zones

        // Define height zones
        float plainsMix = SmoothStep(0.3f, 0.7f, normalizedHeight);
        float mountainMix = SmoothStep(0.6f, 0.9f, normalizedHeight);

        // Apply different characteristics per zone
        float heightResult = baseHeight;

        // Plains zone influence
        heightResult += groundVal * maxHeight * 0.15f * (1.0f - mountainMix);
        heightResult += detailVal * 2.0f * (1.0f - mountainMix);

        // Mountain zone influence
        if (normalizedHeight > 0.6f) {
            float mountainModifier = hillVal * maxHeight * 0.3f * mountainMix;
            heightResult += mountainModifier;
            
            // Add some variation to peaks without creating discontinuities
            float peakDetail = detailVal * mountainMix * 1.5f;
            heightResult += peakDetail;
        }

        // Ensure smooth transitions between zones
        heightResult = math.lerp(baseHeight, heightResult, SmoothStep(0.0f, 1.0f, normalizedHeight));

        // Convert to density with a wider transition zone to prevent holes
        float heightDifference = worldPos.y - heightResult;
        float density = heightDifference / (voxelSize * 3f); // Wider transition

        // Prevent NaN or infinity results
        if (float.IsNaN(density) || float.IsInfinity(density))
        {
            // Fallback to a safe value
            density = 1.0f; // Outside the surface
        }

        densityPoints[index] = new DensityPoint(localPos, density);
    }
}