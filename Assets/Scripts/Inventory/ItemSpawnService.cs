using System.Collections.Generic;
using UnityEngine;

public struct SpawnOptions {
    public Vector3 position;
    public Quaternion rotation;
    public Transform source;
    public bool burst;
    public Vector2 burstDirection;
    public float scatterRadius;
    public float initialImpulse;
    public float? durabilityOverride;
    public string toolTypeOverride;
    public bool markAsDrop;
}

public static class ItemSpawnService {
    public static GameObject Spawn(ItemClass item, int quantity = 1, SpawnOptions? optsNullable = null){
        var opts = optsNullable ?? new SpawnOptions{ position = Vector3.zero, rotation = Quaternion.identity };
        if (item == null) return null;
        if (item.pickupWrapperPrefab == null){
            Debug.LogError($"{item.name}: pickupWrapperPrefab not assigned; cannot spawn.");
            return null;
        }
        GameObject go = Object.Instantiate(item.pickupWrapperPrefab, opts.position, opts.rotation);
        var controller = go.GetComponent<ItemController>();
        if (controller == null){
            Debug.LogError($"{item.name}: pickupWrapperPrefab missing ItemController component.");
            return go;
        }
        float durabilityToUse = opts.durabilityOverride.HasValue ? opts.durabilityOverride.Value : item.maxDurability;
        string toolTypeToUse = !string.IsNullOrEmpty(opts.toolTypeOverride) ? opts.toolTypeOverride : item.toolType.ToString();
        controller.isDrop = opts.markAsDrop;
        controller.item.AddItem(item, quantity, durabilityToUse, toolTypeToUse);
        if (opts.burst){
            Vector2 target = opts.burstDirection == Vector2.zero ? new Vector2(opts.position.x, opts.position.z) : opts.burstDirection;
            controller.ItemBurst(opts.source, target, Mathf.Max(0.1f, opts.scatterRadius));
            var rb = go.GetComponent<Rigidbody>();
            if (rb != null && opts.initialImpulse > 0f){
                rb.AddForce(Random.onUnitSphere * opts.initialImpulse, ForceMode.Impulse);
            }
        }
        return go;
    }

    public static void SpawnBundle(IEnumerable<(ItemClass item, int qty, float durability, string toolType)> drops, SpawnOptions? opts = null){
        foreach (var d in drops){
            var local = opts ?? new SpawnOptions();
            local.durabilityOverride = d.durability;
            local.toolTypeOverride = d.toolType;
            Spawn(d.item, d.qty, local);
        }
    }
}


