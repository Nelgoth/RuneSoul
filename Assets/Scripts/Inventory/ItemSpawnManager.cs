using System.Collections.Generic;
using UnityEngine;

public class ItemSpawnManager : MonoBehaviour {
    public static ItemSpawnManager Instance { get; private set; }

    private void Awake(){
        if (Instance != null && Instance != this){
            Destroy(gameObject);
            return;
        }
        Instance = this;
    }

    public GameObject Spawn(ItemClass item, int quantity, SpawnOptions? optsNullable){
        var opts = optsNullable ?? new SpawnOptions{ position = Vector3.zero, rotation = Quaternion.identity };
        if (item == null) return null;
        if (item.pickupWrapperPrefab == null){
            Debug.LogError($"{item.name}: pickupWrapperPrefab not assigned; cannot spawn.");
            return null;
        }
        GameObject go = Instantiate(item.pickupWrapperPrefab, opts.position, opts.rotation);
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

    public void SpawnBundle(IEnumerable<(ItemClass item, int qty, float durability, string toolType)> drops, SpawnOptions? optsNullable){
        var opts = optsNullable ?? new SpawnOptions();
        foreach (var d in drops){
            var local = opts;
            local.durabilityOverride = d.durability;
            local.toolTypeOverride = d.toolType;
            Spawn(d.item, d.qty, local);
        }
    }
}


