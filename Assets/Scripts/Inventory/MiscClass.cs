using System.Collections;
using UnityEngine;
[CreateAssetMenu(fileName = "new Tool Class", menuName = "Item/Misc")]
public class MiscClass : ItemClass{
    
    //data specific to misc class
    public override void Use(PlayerController playerCaller, UnitController unitCaller, GameObject callObject, Transform hitBox, SlotClass itemSlot){}
    public override MiscClass GetMisc() {return this;}
}