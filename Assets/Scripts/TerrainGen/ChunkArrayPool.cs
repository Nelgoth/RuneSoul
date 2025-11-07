using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Simple static pool for reusing big arrays needed by chunk generation.
/// Note: For multi-threaded code or coroutines in parallel,
///       you would need locking or a more advanced pool structure.
/// </summary>
public static class ChunkArrayPool
{
    // Pools for Vector3[] and int[]
    // Key = capacity of the array, Value = stack of arrays of that size
    private static Dictionary<int, Stack<Vector3[]>> _vectorPool
        = new Dictionary<int, Stack<Vector3[]>>();
    private static Dictionary<int, Stack<int[]>> _intPool
        = new Dictionary<int, Stack<int[]>>();

    /// <summary>
    /// Retrieve a Vector3[] with at least 'size' capacity.
    /// If none is found in the pool, create a new one.
    /// </summary>
    public static Vector3[] GetVectorBuffer(int size)
    {
        // Optionally round 'size' to nearest "bucket" or power-of-two
        // to reduce the number of distinct pools.
        // For a simple approach, we just use the requested size exactly.

        if (_vectorPool.TryGetValue(size, out var stack) && stack.Count > 0)
        {
            return stack.Pop();
        }
        else
        {
            // No existing array, create a new one
            return new Vector3[size];
        }
    }

    /// <summary>
    /// Return a used Vector3[] to the pool for future reuse.
    /// </summary>
    public static void ReturnVectorBuffer(Vector3[] array)
    {
        if (array == null) return;

        int size = array.Length;
        if (!_vectorPool.ContainsKey(size))
        {
            _vectorPool[size] = new Stack<Vector3[]>();
        }
        _vectorPool[size].Push(array);
    }

    /// <summary>
    /// Retrieve an int[] with at least 'size' capacity.
    /// </summary>
    public static int[] GetIndexBuffer(int size)
    {
        if (_intPool.TryGetValue(size, out var stack) && stack.Count > 0)
        {
            return stack.Pop();
        }
        else
        {
            return new int[size];
        }
    }

    /// <summary>
    /// Return an int[] to the pool for future reuse.
    /// </summary>
    public static void ReturnIndexBuffer(int[] array)
    {
        if (array == null) return;

        int size = array.Length;
        if (!_intPool.ContainsKey(size))
        {
            _intPool[size] = new Stack<int[]>();
        }
        _intPool[size].Push(array);
    }
}
