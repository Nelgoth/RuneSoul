using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEditor.Animations;

public class UnitController : MonoBehaviour
{
    public InventoryManager inventory;
    public SlotClass equipedItem;
    private GameObject hitBox;
    public Animator animator;
    public AnimatorController animatorController;
    private AnimatorOverrideController overrideController;
    public AnimationClip originalIdle;
    public AnimationClip originalSwing;
    public AnimationClip unarmedlIdle;
    public AnimationClip unarmedSwing;
    private StatManager statManager;
    public GameManager gameManager;
    // Removed terrain chunk parenting logic
    
    void Start(){
        hitBox = transform.Find("hitBox").gameObject;
        animator = hitBox.GetComponent<Animator>();
        statManager = GetComponent<StatManager>();
        gameManager = Object.FindFirstObjectByType<GameManager>();
        inventory.Initializer();
        inventory.selectedItem = inventory.items[0];
    }

    void Update(){
        if(inventory.selectedItem.item != null){
            AnimOverride(inventory.selectedItem.item.itemAnimIdle, inventory.selectedItem.item.itemAnimSwing);
        }
        else
            AnimOverride(unarmedlIdle, unarmedSwing);
        hitBox.GetComponent<Animator>();
        if (inventory.selectedItem.item.toolType.ToString() != "Axe"){
            inventory.selectedItem = inventory.Contains("Axe");
        equipedItem = inventory.selectedItem;
    }
    }

    

    public void Attack(){
        equipedItem.item.Use(null, this, gameObject, hitBox.transform, inventory.selectedItem, inventory.selectedItem.item.chargeTime); //
    }

    private void AnimOverride(AnimationClip Idle, AnimationClip Swing) {
        // Create a new Animator Override Controller
        overrideController = new AnimatorOverrideController(animatorController);

        // Override the original Motion with the new Motion
        overrideController[originalIdle.name] = Idle;
        overrideController[originalSwing.name] = Swing;

        // Set the Animator Override Controller on the Animator component
        animator.runtimeAnimatorController = overrideController;
    }
    // terrain parenting removed
}
