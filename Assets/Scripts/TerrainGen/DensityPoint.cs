using UnityEngine;
using Unity.Mathematics;
using System;
// Define a simple Voxel struct
public struct DensityPoint
{
    public float3 position;
    public float density;

    public DensityPoint(float3 position, float density)
    {
        this.position = position;
        this.density = density;

    }

    public static implicit operator float(DensityPoint v)
    {
        throw new NotImplementedException();
    }
}