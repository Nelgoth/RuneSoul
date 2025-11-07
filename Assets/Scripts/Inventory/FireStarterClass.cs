using System.Collections;
using UnityEngine;
using System;
using Unity.VisualScripting;

[CreateAssetMenu(fileName = "new Fire Starter Class", menuName = "Item/FireStarter")]
public class FireStarterClass : ItemClass {

    public override void Use(PlayerController playerCaller, UnitController unitCaller, GameObject callObject, Transform hitBox, SlotClass itemSlot){
        layerMask = 1 << 9;
        Collider[] colList = Physics.OverlapSphere(hitBox.position, range, layerMask);
        Array.Sort(colList, (a, b) => Vector3.Distance(a.transform.position, callObject.transform.position).CompareTo(Vector3.Distance(b.transform.position, callObject.transform.position)));
        if (colList.Length > 0)
            if(colList[0].GetComponent<StatManager>().fireHandler is not null && !colList[0].GetComponent<StatManager>().fireHandler.isLit){
                colList[0].GetComponent<StatManager>().fireHandler.Light();
            }
        else return;
        //if (playerCaller is not null)
        //    playerCaller.inventory.UseSelectedConsumable();
        //else unitCaller.inventory.UseSelectedConsumable();      
    }

}
