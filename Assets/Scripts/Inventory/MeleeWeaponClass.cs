using System.Collections;
using UnityEngine;
using System;

[CreateAssetMenu(fileName = "new Weapon Class", menuName = "Item/Weapon/Melee")]
public class MeleeWeaponClass : ItemClass {

    public override void Use(PlayerController playerCaller, UnitController unitCaller, GameObject callObject, Transform hitBox, SlotClass itemSlot, float chargeAmount){
        base.Use(playerCaller, unitCaller, callObject, hitBox, itemSlot, chargeAmount);
        
    }

}
