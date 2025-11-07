using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class ItemController : MonoBehaviour
{
    [SerializeField] public SlotClass item;
    private Transform modelParent;
    public bool canPickup = false;
    public bool isDrop = false;
    private bool pickedUp = false;
    void Start(){
        // If a 3D model prefab is assigned on the item, instantiate it as a child under a model parent
        if (item != null && item.item != null && item.item.worldModelPrefab != null){
            if (modelParent == null){
                var mp = new GameObject("Model");
                mp.transform.SetParent(transform);
                mp.transform.localPosition = Vector3.zero;
                mp.transform.localRotation = Quaternion.identity;
                mp.transform.localScale = Vector3.one;
                modelParent = mp.transform;
            }
            var instance = Instantiate(item.item.worldModelPrefab, modelParent);
            instance.transform.localPosition = item.item.worldModelLocalOffset;
            instance.transform.localRotation = Quaternion.Euler(item.item.worldModelLocalEuler);
            instance.transform.localScale = item.item.worldModelLocalScale;
            var sr = GetComponent<SpriteRenderer>();
            if (sr != null) sr.enabled = false; // hide 2D icon when using 3D model
        }
        else{
            // Fallback to 2D sprite-based pickup
            var sr = GetComponent<SpriteRenderer>();
            if (sr != null) sr.sprite = item.item.itemIcon;
        }
        // Initialize durability only if not set yet to avoid doubling above max
        if (!isDrop && item != null && item.item != null && item.durability <= 0f)
            item.AddDurability(item.item.maxDurability);
        StartCoroutine(CanPickup());
    }
    private IEnumerator CanPickup(){
        float timer = 1;
        while (!canPickup) {
            if(timer == 0)
                canPickup = true;
            timer -= 1;
            yield return new WaitForSeconds(2);
        }
    }
    
    public void Remove(){
        Destroy(gameObject);
    }

    // Prevent double pickup and disable colliders immediately when picked
    public bool TryMarkPickedUpAndDisableColliders(){
        if (pickedUp) return false;
        pickedUp = true;
        canPickup = false;
        foreach (var col in GetComponentsInChildren<Collider>())
            col.enabled = false;
        foreach (var col2d in GetComponentsInChildren<Collider2D>())
            col2d.enabled = false;
        return true;
    }

    public void ItemBurst(Transform caller, Vector2 dropBurstPos, float spawnRadius) {
        var rb3d = GetComponent<Rigidbody>();
        if (rb3d == null) return;
        Vector3 target = new Vector3(dropBurstPos.x, caller.position.y, dropBurstPos.y);
        Vector3 dir = (target - caller.position).normalized + Vector3.up * 0.5f;
        rb3d.AddForce(dir * 5f, ForceMode.Impulse);
    }
}
