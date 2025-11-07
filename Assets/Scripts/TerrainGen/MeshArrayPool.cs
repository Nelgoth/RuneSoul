using UnityEngine;
using System.Collections.Generic;

public static class MeshArrayPool
{
    private const int MAX_POOL_SIZE = 100;
    private static readonly Stack<Vector2[]> uvArrayPool = new Stack<Vector2[]>();
    private static readonly Stack<Vector3[]> normalArrayPool = new Stack<Vector3[]>();
    private static readonly object poolLock = new object();

    public static Vector2[] GetUVArray(int size)
    {
        lock (poolLock)
        {
            if (uvArrayPool.Count > 0)
            {
                var array = uvArrayPool.Pop();
                if (array.Length >= size)
                {
                    return array;
                }
            }
            return new Vector2[size];
        }
    }

    public static Vector3[] GetNormalArray(int size)
    {
        lock (poolLock)
        {
            if (normalArrayPool.Count > 0)
            {
                var array = normalArrayPool.Pop();
                if (array.Length >= size)
                {
                    return array;
                }
            }
            return new Vector3[size];
        }
    }

    public static void ReturnUVArray(Vector2[] array)
    {
        if (array == null) return;
        lock (poolLock)
        {
            if (uvArrayPool.Count < MAX_POOL_SIZE)
            {
                uvArrayPool.Push(array);
            }
        }
    }

    public static void ReturnNormalArray(Vector3[] array)
    {
        if (array == null) return;
        lock (poolLock)
        {
            if (normalArrayPool.Count < MAX_POOL_SIZE)
            {
                normalArrayPool.Push(array);
            }
        }
    }

    public static void ClearPools()
    {
        lock (poolLock)
        {
            uvArrayPool.Clear();
            normalArrayPool.Clear();
        }
    }
}