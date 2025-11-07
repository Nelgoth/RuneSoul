using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

public class CraftingHandler : MonoBehaviour
{
    [SerializeField] private CraftingRecipeClass[] craftingRecipes;
    [SerializeField] private InventoryManager craftingInventory;
    [SerializeField] private InventoryManager outputInventory;
    public FireHandler fireSource;
    public Image cookFillImage;
    private CraftingRecipeClass recipeToUse;
    private bool canCraft;
    private bool loopRunning = false;
    public bool cooking = false;
    public void Update(){
        if (craftingInventory.transform.gameObject.activeSelf && !loopRunning){
            loopRunning = true;
            StartCoroutine(RecipeLoop());
        }
        else if (!craftingInventory.transform.gameObject.activeSelf){
            StopCoroutine(RecipeLoop());
            loopRunning = false;
        }
    }
    
    private IEnumerator RecipeLoop(){
        while (true){
            for ( int i = 0; i < craftingRecipes.Length; i++){
                canCraft = false;
                if (CanCraft(craftingRecipes[i])){
                    recipeToUse = craftingRecipes[i];
                    canCraft = true;
                    if (recipeToUse.cookNeeded && !cooking){
                        cooking = true;
                        StartCoroutine(Cook());
                    }
                    break;
                }
                if (i == craftingRecipes.Length-1)
                    cooking = false;
            }
            yield return new WaitForSeconds(.1f);
        }
        
    }

    public bool CanCraft(CraftingRecipeClass recipe){
        if (recipe.CanCraft(craftingInventory, outputInventory)){
            if(outputInventory.overlayItem is not null && outputInventory.overlayItem != recipe.outputItem){
                outputInventory.overlayItem.Clear();
                outputInventory.RefreshOverlay();
            }
            outputInventory.Add(recipe.outputItem.item, recipe.outputItem.quantity, recipe.outputItem.durability, recipe.outputItem.toolType, true);
            return true;
        }
        else

            if(outputInventory.overlayItem is not null){
                outputInventory.overlayItem.Clear();
                outputInventory.RefreshOverlay();
            }
            return false;
    }

    public IEnumerator Cook(){
        float cookAmount = 0;
        while(true){
            if (!cooking){
                cookFillImage.fillAmount = 0f;
                yield break;
            }
            cookAmount += .1f;
            float cookNormalized = cookAmount / recipeToUse.craftPoints;
            cookFillImage.fillAmount = cookNormalized;
            if (cookAmount >= recipeToUse.craftPoints){
                Craft();
                cooking = false;
                yield break;
            }
            yield return new WaitForSeconds(.1f);
        }
    }

    public void Craft(){
        Debug.Log($"[CraftingHandler] Craft button clicked on '{gameObject.name}'. Pre-check canCraft={canCraft}");
        canCraft = false;
        for (int i = 0; i < craftingRecipes.Length; i++){
            if (CanCraft(craftingRecipes[i])){
                recipeToUse = craftingRecipes[i];
                canCraft = true;
                Debug.Log($"[CraftingHandler] Found valid recipe: '{(recipeToUse != null ? recipeToUse.name : "<null>")}'.");
                break;
            }
        }
        if (canCraft){
            Debug.Log("[CraftingHandler] Crafting now...");
            recipeToUse.Craft(craftingInventory,outputInventory);
            if (outputInventory.overlayItem is not null){
                outputInventory.overlayItem.Clear();
                outputInventory.RefreshOverlay();
            }
            Debug.Log("[CraftingHandler] Craft completed.");
        }
        else Debug.LogWarning("[CraftingHandler] Craft failed: no valid recipe or insufficient inputs/output space.");
    }
}
