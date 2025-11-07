using System.Collections;
using UnityEngine;
using System;
using NelsUtils;
using System.Runtime.CompilerServices;

public class ItemClass : ScriptableObject {
    [Header("Item")] //data shared across every item
    public string itemName;
    public Sprite itemIcon;
    [Tooltip("Optional world model prefab to represent this item when spawned/dropped in 3D")] public GameObject worldModelPrefab;
    [Tooltip("Local offset applied to the spawned world model under the pickup wrapper")] public Vector3 worldModelLocalOffset = Vector3.zero;
    [Tooltip("Local euler rotation applied to the spawned world model under the pickup wrapper")] public Vector3 worldModelLocalEuler = Vector3.zero;
    [Tooltip("Local scale applied to the spawned world model under the pickup wrapper")] public Vector3 worldModelLocalScale = Vector3.one;
    public AnimationClip  itemAnimIdle;
    public AnimationClip  itemAnimSwing;
    public bool isStackable = true;
    public bool isDurable = true;
    public bool isFuel = false;
    public float fuelAmount;
    public int stackSize = 64;
    public LayerMask layerMask = 1 << 7;
    public enum ToolType{
        Weapon,
        Pickaxe,
        Hammer,
        Axe,
        Ranged,
        Projectile,
        Consumable,
        Resource,
        FireStarter,
    }
    public ToolType toolType;
    [UnityEngine.Serialization.FormerlySerializedAs("BaseItem")] public GameObject pickupWrapperPrefab;
    private Utils utils = new Utils();
    [Header("Weapon Stats")]
    public float dmg;
    public float range;
    public float maxDurability;
    [HideInInspector]
    public float durability;
    public float staminaUse;
    public float chargeTime;
    public SlotClass[] dropItems;
    
    [Header("Melee")]
    public float cleave;
    
    [Header("Ranged")]
    public int launchForce;
    public SlotClass[] projectileTypes;
    public GameObject projectilePrefab;
    
    public virtual void Use(PlayerController playerCaller, UnitController unitCaller, GameObject callObject, Transform hitBox, SlotClass itemSlot){
        
    }

    
    public virtual void Use(PlayerController playerCaller, UnitController unitCaller, GameObject callObject, Transform hitBox, SlotClass itemSlot, float chargeAmount){
        layerMask = 1 << 7;
        if (callObject.TryGetComponent(out IStatus callerhit))
            if (callerhit.Stamina < staminaUse){
                return;
            }
        callerhit.UseStat(-staminaUse, "Stamina");
        if (playerCaller is not null)
            playerCaller.animator.SetTrigger("Swing");
        else unitCaller.animator.SetTrigger("Swing");
        Collider2D[] colList = Physics2D.OverlapCircleAll(hitBox.position, range, layerMask);
        Array.Sort(colList, (a, b) => Vector2.Distance(a.transform.position, callObject.transform.position).CompareTo(Vector2.Distance(b.transform.position, callObject.transform.position)));
        for (int i = 0, x = 0; i < colList.Length && x < cleave; i++) {
            if (callObject.GetComponent<MemManager>().CheckAlly(colList[i].GetComponent<StatManager>().race)) 
                continue;      
            if (colList[i].gameObject != callObject)
                if (colList[i].TryGetComponent(out IStatus hit)){
                    x++;
                    hit.Damage(dmg*chargeAmount,toolType.ToString());
                    if (playerCaller is not null)
                        playerCaller.inventory.UseSelected(itemSlot);
                    else unitCaller.inventory.UseSelected(itemSlot);
                }
        }   
    }

    public virtual void Use(PlayerController playerCaller, UnitController unitCaller ,GameObject callObject, Transform hitBox, GameObject projectile, float chargeAmount) {

        GameObject newProjectile = Instantiate(projectile, hitBox.position, hitBox.rotation);
        newProjectile.GetComponent<Rigidbody2D>().linearVelocity = hitBox.right * launchForce * chargeAmount;
        newProjectile.GetComponent<Projectile>().Fired(range, chargeAmount);
        if (playerCaller is not null)
            playerCaller.inventory.UseSelected();
        else unitCaller.inventory.UseSelected();
    }
    
    public virtual void UseSecondary(PlayerController playerCaller, UnitController unitCaller, GameObject callObject, Transform hitBox, SlotClass itemSlot){
        if (isFuel){
            layerMask = 1 << 9;
            Collider2D[] colList = Physics2D.OverlapCircleAll(hitBox.position, range, layerMask);
            Array.Sort(colList, (a, b) => Vector2.Distance(a.transform.position, callObject.transform.position).CompareTo(Vector2.Distance(b.transform.position, callObject.transform.position)));
            if (colList.Length > 0)
                colList[0].GetComponent<StatManager>().inventory.Add(this,1,durability,toolType.ToString(),false);
            else return;
            if (playerCaller is not null)
                playerCaller.inventory.UseSelectedConsumable();
            else unitCaller.inventory.UseSelectedConsumable();

        }
        else return;
    }

    public bool DropItems(GameObject caller){
        var opts = new SpawnOptions{
            position = caller.transform.position,
            rotation = caller.transform.rotation,
            source = caller.transform,
            burst = true,
            scatterRadius = 1f,
            initialImpulse = 0f,
            markAsDrop = true,
        };
        for (int i = 0; i < dropItems.Length; i++) {
            if (dropItems[i].item != null) {
                opts.durabilityOverride = dropItems[i].durability;
                opts.toolTypeOverride = dropItems[i].toolType;
                ItemSpawnService.Spawn(dropItems[i].item, dropItems[i].quantity, opts);
            }
        }
        return true;
    }

    // Convenience: spawn this item into the world as a pickup
    public GameObject SpawnPickup(Vector3 position, Quaternion rotation, int quantity = 1, float? durabilityOverride = null, string toolTypeOverride = null, bool burstFromCaller = false, Transform caller = null){
        var opts = new SpawnOptions{
            position = position,
            rotation = rotation,
            source = caller,
            burst = burstFromCaller,
            burstDirection = Vector2.zero,
            scatterRadius = 1f,
            durabilityOverride = durabilityOverride,
            toolTypeOverride = toolTypeOverride,
            markAsDrop = burstFromCaller,
        };
        return ItemSpawnService.Spawn(this, quantity, opts);
    }
    public virtual ItemClass GetItem(){return this;}
    //public virtual ToolClass GetTool(){return null;}
    public virtual MiscClass GetMisc(){return null;}
    public virtual ConsumableClass GetConsumable(){return null;}
}
