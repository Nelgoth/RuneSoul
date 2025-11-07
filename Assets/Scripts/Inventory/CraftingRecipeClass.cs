using System.Collections;
using System.Collections.Generic;
using UnityEngine;
[CreateAssetMenu(fileName = "newCraftingRecipe", menuName = "Crafting/Recipe")]
public class CraftingRecipeClass : ScriptableObject{
    [Header("Crafting Recipe")]
    public bool containsOnly;
    public bool cookNeeded;
    public float craftPoints;
    public SlotClass[] inputItems;
    public SlotClass outputItem;
    public SlotClass outputTool;

    public bool CanCraft(InventoryManager craftInv, InventoryManager output){
        //check if there is space in inventory
        if (output.items[0].item != null && (!output.items[0].item.isStackable || outputItem.item != output.items[0].item || (output.items[0].quantity + outputItem.quantity) > output.items[0].item.stackSize)){
            return false;
        }
        if (containsOnly){
            for (int i=0; i < inputItems.Length; i++){
                if(inputItems[i].toolType.ToString() == "")
                    if (!craftInv.Contains(inputItems[i].item,inputItems[i].quantity)){
                        return false;
                    }
                if(inputItems[i].toolType is not null && inputItems[i].toolType.ToString() != ""){
                    if(craftInv.Contains(inputItems[i].toolType.ToString()) is null){
                        return false;
                    }
                    else outputTool = craftInv.Contains(inputItems[i].toolType.ToString());
                }
            }
        }
        if(!containsOnly){
            for (int i=0; i < inputItems.Length; i++){
                if(inputItems[i].quantity == 0 && craftInv.items[i].quantity != 0){
                    return false;
                }
            }
            for (int i=0; i < inputItems.Length; i++){
                if(inputItems[i].item is not null && (inputItems[i].item != craftInv.items[i].item || craftInv.items[i].quantity < inputItems[i].quantity)){
                    return false;
                }
            }
        }
        return true;
    }
    public void Craft(InventoryManager craftInv, InventoryManager output){
        //remove the input items from the inventory
        for (int i=0; i < inputItems.Length; i++){
            if(inputItems[i].quantity != 0 && inputItems[i].toolType.ToString() == ""){
                craftInv.Remove(inputItems[i].item, inputItems[i].quantity, false);
            }
            if(inputItems[i].quantity != 0 && inputItems[i].toolType.ToString() != ""){
                craftInv.UseSelected(outputTool);
            }
        }
        //add the output item to inventory
        output.Add(outputItem.item, outputItem.quantity, outputItem.item.maxDurability, outputItem.item.toolType.ToString(), false);
    }
}