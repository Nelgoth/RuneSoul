using System.Collections;
using UnityEngine;
using System;

[CreateAssetMenu(fileName = "new Weapon Class", menuName = "Item/Weapon/Ranged")]
public class RangedWeaponClass : ItemClass {

    public override void Use(PlayerController playerCaller, UnitController unitController, GameObject callObject, Transform hitBox, GameObject projectile, float chargeAmount) {
        base.Use(playerCaller, unitController, callObject, hitBox, projectile, chargeAmount);
    }
}
