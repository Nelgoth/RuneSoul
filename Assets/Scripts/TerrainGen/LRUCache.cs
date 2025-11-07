// ADD new file: LRUCache.cs
using System.Collections.Generic;

public class LRUCache<TKey, TValue>
{
    private readonly int capacity;
    private readonly Dictionary<TKey, LinkedListNode<CacheItem>> cacheMap;
    private readonly LinkedList<CacheItem> lruList;

    public struct CacheItem
    {
        public TKey Key;
        public TValue Value;

        public CacheItem(TKey key, TValue value)
        {
            Key = key;
            Value = value;
        }
    }

    public LRUCache(int capacity)
    {
        this.capacity = capacity;
        this.cacheMap = new Dictionary<TKey, LinkedListNode<CacheItem>>(capacity);
        this.lruList = new LinkedList<CacheItem>();
    }

    public bool TryGetValue(TKey key, out TValue value)
    {
        value = default;
        if (cacheMap.TryGetValue(key, out LinkedListNode<CacheItem> node))
        {
            value = node.Value.Value;
            lruList.Remove(node);
            lruList.AddFirst(node);
            return true;
        }
        return false;
    }

    public void Add(TKey key, TValue value)
    {
        if (cacheMap.TryGetValue(key, out LinkedListNode<CacheItem> existingNode))
        {
            lruList.Remove(existingNode);
        }
        else if (cacheMap.Count >= capacity)
        {
            // Remove least recently used item
            LinkedListNode<CacheItem> lastNode = lruList.Last;
            cacheMap.Remove(lastNode.Value.Key);
            lruList.RemoveLast();
        }

        // Add new item
        CacheItem newItem = new CacheItem(key, value);
        LinkedListNode<CacheItem> newNode = new LinkedListNode<CacheItem>(newItem);
        lruList.AddFirst(newNode);
        cacheMap[key] = newNode;
    }

    public void Clear()
    {
        cacheMap.Clear();
        lruList.Clear();
    }
}