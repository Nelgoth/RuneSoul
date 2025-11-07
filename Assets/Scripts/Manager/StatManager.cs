using System.Collections;
using UnityEngine;
using NelsUtils;
using UnityEngine.SocialPlatforms;
using Unity.VisualScripting;

public class StatManager : MonoBehaviour, IStatus
{
    
    public float Health { set; get; }
    public float maxHealth;
    private float healthRegen = .25f;
    public float Stamina { set; get; }
    public float maxStamina;
    private float staminaRegen = 3f;
    public float Mental { set; get; }
    public float maxMental;
    private float mentalRegen = 1f;
    public float Hunger { set; get; }
    public float maxHunger;
    public float Thirst { set; get; }
    public float maxThirst;
    public float perception;
    public float speed;
    public float range;
    public string race = "None";
    public string type = "Base";
    public GameObject BaseItem;
    private StatusBars statusBars;
    public InventoryManager inventory;
    public FireHandler fireHandler;
    public bool inanimate;
    public SlotClass[] dropItems;
    private Utils utils;
    private float normal;
    //private bool isPlayer = false;


    void Start() {    
        utils = new Utils();
        Health = maxHealth;
        Stamina = maxStamina;
        Mental = maxMental;
        Hunger = maxHunger;
        Thirst = maxThirst;
        if (CompareTag("Player")){
            //isPlayer = true;
            statusBars = Object.FindFirstObjectByType<StatusBars>();
        }
        if (!inanimate){
            StartCoroutine(HealthTick());
            StartCoroutine(StaminaTick());
            StartCoroutine(MentalTick());
            StartCoroutine(HungerTick());
            StartCoroutine(ThirstTick());
        }
           
    }

    public void Damage(UnitController caller, float damage, string toolType){
        Health -= damage;
        float healthNormalized = Health / maxHealth;
        if (statusBars != null)
            statusBars.SetStat(healthNormalized, "Health");
        DamagePopup.Create(transform.position,damage,false);    
        if (Health <= 0)
            Remove();
    }
    public void Damage(float damage, string toolType){
        Health -= damage;
        float healthNormalized = Health / maxHealth;
        if (statusBars != null)
            statusBars.SetStat(healthNormalized, "Health");    
        DamagePopup.Create(transform.position,damage,false);
        if (Health <= 0)
            Remove();
    }
    
    public void UseStat(float value, string statToUse){
            normal = 0;
            if (statToUse == "Health") {
                if ((Health + value) >= maxHealth)
                    normal = (Health = maxHealth) / maxHealth;
                if ((Health + value) <= 0)
                    normal = Health = 0;
                if (normal != 100)
                    normal = (Health += value) / maxHealth;
            }
            if (statToUse == "Stamina") {
                if ((Stamina + value) >= maxStamina)
                    normal = (Stamina = maxStamina) / maxStamina;
                if ((Stamina + value) <= 0)
                    normal = Stamina = 0;
                if (normal != 100)
                    normal = (Stamina += value) / maxStamina;
            }
            if (statToUse == "Mental") {
                if ((Mental + value) >= maxMental)
                    normal = (Mental = maxMental) / maxMental;
                if ((Mental + value) <= 0)
                    normal = Mental = 0;
                if (normal != 100)
                    normal = (Mental += value) / maxMental;
            }
            if (statToUse == "Hunger") {
                if ((Hunger + value) >= maxHunger)
                    normal = (Hunger = maxHunger) / maxHunger;
                if ((Hunger + value) <= 0)
                    normal = Hunger = 0;
                if (normal != 100)
                    normal = (Hunger += value) / maxHunger;
            }
            if (statToUse == "Thirst") {
                if ((Thirst + value) >= maxThirst)
                    normal = (Thirst = maxThirst) / maxThirst;
                if ((Thirst + value) <= 0)
                    normal = Thirst = 0;
                if (normal != 100)
                    normal = (Thirst += value) / maxThirst;
            }
            if (statusBars != null)
                SetStat(normal, statToUse);
            
    }

    void Remove() {
        if (inventory != null){
            for (int i = 0; i < inventory.items.Length; i++) {
                if (inventory.items[i].item != null) {
                    var opts = new SpawnOptions{
                        position = transform.position,
                        rotation = transform.rotation,
                        source = transform,
                        burst = true,
                        scatterRadius = 1f,
                        markAsDrop = true,
                        durabilityOverride = inventory.items[i].durability,
                        toolTypeOverride = inventory.items[i].toolType,
                    };
                    ItemSpawnService.Spawn(inventory.items[i].item, inventory.items[i].quantity, opts);
                }
            }
        }
        for (int i = 0; i < dropItems.Length; i++) {
            if (dropItems[i].item != null) {
                var opts = new SpawnOptions{
                    position = transform.position,
                    rotation = transform.rotation,
                    source = transform,
                    burst = true,
                    scatterRadius = 1f,
                    markAsDrop = true,
                    durabilityOverride = dropItems[i].durability,
                    toolTypeOverride = dropItems[i].toolType,
                };
                ItemSpawnService.Spawn(dropItems[i].item, dropItems[i].quantity, opts);
            }
        }
        Destroy(gameObject);
    }

    
    
#region Ticks
     private IEnumerator HealthTick() {
        while (true){
            float regenMod = healthRegen;
            if (Hunger < (maxHunger *.5f))
                regenMod -= healthRegen * .5f;
            if (Hunger < (maxHunger *.25f))
                regenMod -= healthRegen * .5f;
            if (Thirst < (maxThirst * .5f))
                regenMod -= healthRegen * .5f;
            if (Thirst < (maxThirst * .25f))
                regenMod -= healthRegen * .5f;
            if(Health < maxHealth){
                if((Health + regenMod) < maxHealth)
                    Health += regenMod; 
                else    Health = maxHealth;
                if (statusBars != null)
                    SetStat(Health / maxHealth, "Health");
            }
            if (Health <= 0)
                Remove();
            yield return new WaitForSeconds(1);
        }
    }
    private IEnumerator StaminaTick() {
        while (true){
            float regenMod = staminaRegen;
            if (Hunger < (maxHunger *.5f))
                regenMod -= staminaRegen * .25f;
            if (Hunger < (maxHunger *.25f))
                regenMod -= staminaRegen * .25f;
            if (Thirst < (maxThirst * .5f))
                regenMod -= staminaRegen * .25f;
            if (Thirst < (maxThirst * .25f))
                regenMod -= staminaRegen * .25f;
            if(Stamina < maxStamina){    
                if((Stamina + regenMod) < maxStamina){
                    Stamina += regenMod;  
                }
                else    Stamina = maxStamina;
                if (statusBars != null)
                    SetStat(Stamina / maxStamina, "Stamina");
            }
            yield return new WaitForSeconds(1);
        }
    }
    private IEnumerator MentalTick() {
        while (true){
            if(Mental < maxMental){
                if((Mental + mentalRegen) < maxMental)
                    Mental += mentalRegen;
                else    Mental = maxMental;
                if (statusBars != null)
                        SetStat(Mental / maxMental, "Mental");
             }
            yield return new WaitForSeconds(1);
        }
    }
    private IEnumerator HungerTick() {
        while (true){
            if(Hunger <= 0)
                Hunger = 0;
            else { 
                Hunger -= 1;
                if (statusBars != null)
                    SetStat(Hunger / maxHunger, "Hunger");
            }
            yield return new WaitForSeconds(1);
        }
    }
    private IEnumerator ThirstTick() {
        while (true){
            if(Thirst <= 0)
                Thirst = 0;
            else {
                Thirst -= 1;
                if (statusBars != null)
                    SetStat(Thirst / maxThirst, "Thirst");
            }
            yield return new WaitForSeconds(1);
        }
    } 
#endregion
    
    public void SetStat(float normalized, string statToUse){
        statusBars.SetStat(normalized, statToUse);
    }
    
    public void ChargeUI(float charge, bool held) {
        statusBars.chargeBar.SetActive(held);
        SetStat(charge / inventory.selectedItem.item.chargeTime,"Charge");
    }
}
    