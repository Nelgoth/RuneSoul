using System.Collections;
using UnityEngine;
[CreateAssetMenu(fileName = "new Tool Class", menuName = "Item/Consumable")]
public class ConsumableClass : ItemClass{
    [Header("Consumable")]//data specific to consumable class
    public float healthValue;
    public float hungerValue;
    public float thirstValue;
    public override void Use(PlayerController playerCaller, UnitController unitCaller, GameObject callObject, Transform hitBox, SlotClass itemSlot){
        if (callObject.TryGetComponent(out IStatus callerhit)){
            callerhit.UseStat(healthValue,"Health");
            callerhit.UseStat(hungerValue,"Hunger"); 
            callerhit.UseStat(thirstValue,"Thirst");
        }
        if (playerCaller is not null)
            playerCaller.inventory.UseSelectedConsumable();
        else unitCaller.inventory.UseSelectedConsumable();
    }
    public override ConsumableClass GetConsumable() {return this;}
}
