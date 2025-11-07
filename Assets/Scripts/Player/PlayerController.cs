using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem.UI;
using UnityEngine.UI; 
using TMPro;
using NelsUtils;

public class PlayerController : MonoBehaviour
{
    public InventoryManager inventory;
    public InventoryManager crafting;
    public InventoryManager output;
    public InventoryManager cook;
    public ObjectSelector objectSelector;
    public GameObject hitBox;
    public TextMeshProUGUI tipText;
    public RectTransform tipWindow;
    public Animator animator;
    public RuntimeAnimatorController animatorController;
    private AnimatorOverrideController overrideController;
    public AnimationClip originalIdle;
    public AnimationClip originalSwing;
    public AnimationClip unarmedlIdle;
    public AnimationClip unarmedSwing;
    private StatManager statManager;
    [HideInInspector]
    public float lastHorizontalVector;
    [HideInInspector]
    public float lastVerticalVector;
    [HideInInspector]
    public Vector2 moveDir;
    public float currentMoveSpeed;
    public ContactFilter2D movementFilter;
    private Vector2 moveInput;
    private List<RaycastHit2D> castCollisions = new List<RaycastHit2D>();
    
    private float chargeAmount;
    private bool held;
    private bool sprint;
    private bool sprinting;
    
    void Start(){
        // Ensure hitBox/animator
        if (hitBox == null){
            var hb = transform.Find("hitBox");
            if (hb != null) hitBox = hb.gameObject;
        }
        if (hitBox != null) animator = hitBox.GetComponent<Animator>();

        // Ensure StatManager
        statManager = GetComponent<StatManager>();
        if (statManager != null) currentMoveSpeed = statManager.speed;

        // Ensure base animator controller so override works
        if (animatorController == null && animator != null && animator.runtimeAnimatorController != null){
            animatorController = animator.runtimeAnimatorController;
        }

        // Initialize inventory defaults
        InitializeInventoryManager(inventory, true);
        InitializeInventoryManager(crafting, false);
        InitializeInventoryManager(output, false);
        InitializeInventoryManager(cook, false);

        // Ensure an EventSystem exists so UI buttons can receive clicks
        if (FindObjectOfType<EventSystem>() == null){
            var esGo = new GameObject("EventSystem");
            esGo.AddComponent<EventSystem>();
            // Prefer the new Input System UI module; fallback to Standalone when unavailable
            if (esGo.GetComponent<InputSystemUIInputModule>() == null)
                esGo.AddComponent<InputSystemUIInputModule>();
        }
    }
    private void InitializeInventoryManager(InventoryManager inv, bool selectFirst){
        if (inv == null) return;
        if (inv.items == null || inv.items.Length == 0) inv.Initializer();
        if (selectFirst && inv.selectedItem == null && inv.items != null && inv.items.Length > 0)
            inv.selectedItem = inv.items[0];
    }
    void Update(){
        if(inventory != null && inventory.selectedItem != null && inventory.selectedItem.item != null)
            AnimOverride(inventory.selectedItem.item.itemAnimIdle, inventory.selectedItem.item.itemAnimSwing);
        else
            AnimOverride(unarmedlIdle, unarmedSwing);
    
        // Input System device polling
        bool leftDown = UnityEngine.InputSystem.Mouse.current != null && UnityEngine.InputSystem.Mouse.current.leftButton.wasPressedThisFrame;
        bool rightDown = UnityEngine.InputSystem.Mouse.current != null && UnityEngine.InputSystem.Mouse.current.rightButton.wasPressedThisFrame;
        bool leftUp = UnityEngine.InputSystem.Mouse.current != null && UnityEngine.InputSystem.Mouse.current.leftButton.wasReleasedThisFrame;
        float scrollY = UnityEngine.InputSystem.Mouse.current != null ? UnityEngine.InputSystem.Mouse.current.scroll.ReadValue().y : 0f;
        var kb = UnityEngine.InputSystem.Keyboard.current;
        if (leftDown){
            if (GameObject.FindGameObjectsWithTag("InvUI").Length > 0)
                return;
            if (inventory != null && inventory.selectedItem != null && inventory.selectedItem.item != null && inventory.selectedItem.item.chargeTime != 0){
                held = true;
                StartCoroutine(Charge());
                return;
            }
            if (inventory != null && inventory.selectedItem != null && inventory.selectedItem.item != null)
                Action(inventory.selectedItem.toolType.ToString(), inventory.selectedItem.item);
            return;
        }
        if (leftUp){
            if (GameObject.FindGameObjectsWithTag("InvUI").Length > 0) 
                return; 
            if (inventory != null && inventory.selectedItem != null && inventory.selectedItem.item != null){
                if(held){
                    held = false;
                    if (statManager != null) currentMoveSpeed += statManager.speed * .66f;
                    Action(inventory.selectedItem.toolType.ToString(), inventory.selectedItem.item);
                }
                else return;
            }
        }
        if (rightDown){
            if(GameObject.FindGameObjectsWithTag("InvUI").Length > 0)
                return;
            if (inventory != null && inventory.selectedItem != null && inventory.selectedItem.item != null)
            ActionSecondary(inventory.selectedItem.toolType.ToString(), inventory.selectedItem.item);

        }
         if (kb != null && kb.eKey.wasPressedThisFrame){
            objectSelector.SelectionActivater(this);
        }
        if (kb != null && kb.iKey.wasPressedThisFrame)
            inventory.OpenInv();
        if (kb != null && kb.cKey.wasPressedThisFrame){
            crafting.OpenInv();
            output.OpenInv();
        }
        if (kb != null && kb.escapeKey.wasPressedThisFrame){
            var panels = GameObject.FindGameObjectsWithTag("InvUI");
            for (int i = 0; i < panels.Length; i++){
                panels[i].GetComponent<InventoryManager>().OpenInv();
            }
            if(objectSelector.selectedObject != null){
                objectSelector.ClearSelection(this);
            }
        }
        if (kb != null && kb.leftShiftKey.wasPressedThisFrame){
            sprint = true;
            StartCoroutine(Sprint());
        }
        if (kb != null && kb.leftShiftKey.wasReleasedThisFrame){
            sprint = false;
            if (sprinting){
                if (statManager != null) currentMoveSpeed -= statManager.speed * .66f;
                sprinting = false;
            }
        }
        InputManagement();
        
    }
    
    void FixedUpdate() {
    }
    public void ActionSecondary(string toolType, ItemClass item){
        if (toolType== "Resource") {
            item.UseSecondary(this, null, gameObject,hitBox.transform,inventory.selectedItem);
            return;
        }
    }

    public void Action(string toolType, ItemClass item){
        if (toolType== "Consumable") {
            item.Use(this, null, gameObject, hitBox.transform, inventory.selectedItem);
            return;
        }
        if (toolType == "Ranged") {
            statManager.ChargeUI(0, held);
            for (int i = 0; i < item.projectileTypes.Length; i++){
                if (inventory.Contains(inventory.selectedItem.item.projectileTypes[i].item, inventory.selectedItem.item.projectileTypes[i].quantity)) {                        
                    item.Use(this, null, gameObject, hitBox.transform,inventory.selectedItem.item.projectileTypes[i].item.projectilePrefab, chargeAmount);
                    inventory.Remove(inventory.selectedItem.item.projectileTypes[i].item, inventory.selectedItem.item.projectileTypes[i].quantity, false);
                }
            }
            return;
        }
        if (toolType == "FireStarter"){
            item.Use(this, null, gameObject, hitBox.transform, inventory.selectedItem);
            return;
        }
        if(toolType != "Ranged") {
            statManager.ChargeUI(0, held);
            item.Use(this, null, gameObject, hitBox.transform, inventory.selectedItem, chargeAmount);
            return;
        }
        else animator.SetTrigger("Swing");
    }

    private IEnumerator Sprint() {
        bool canSprint = false;
        while(sprint){
            if (statManager.Stamina > statManager.maxStamina * .10f){
                canSprint = true;
            }
            if (sprinting && statManager.Stamina > 0){
                canSprint = true;
            }
            if (statManager.Stamina < statManager.maxStamina * .10f) {
                canSprint = false;
                sprint = false;
            }
            if(moveDir != Vector2.zero && !sprinting && canSprint){
                currentMoveSpeed += statManager.speed * .66f;
                sprinting = true;
            }
            if (moveDir != Vector2.zero && canSprint){
                statManager.UseStat(-3f,"Stamina");
            }
            if (moveDir == Vector2.zero && sprinting){
                currentMoveSpeed -= statManager.speed * .66f;
                sprinting = false;
            }
            if (sprinting && !canSprint){
                currentMoveSpeed -= statManager.speed * .66f;
                sprinting = false;
                sprint = false;
            }
            
            
            yield return new WaitForSeconds(.1f);
        }
    }

    private IEnumerator Charge() {
        chargeAmount = 0f;
        currentMoveSpeed -= statManager.speed * .66f;
        while(held){
            if (statManager.Stamina < inventory.selectedItem.item.staminaUse){
                currentMoveSpeed += statManager.speed * .66f;
                held = false;
            }
            if (chargeAmount >= inventory.selectedItem.item.chargeTime)
                break;
            chargeAmount += .1f;
            statManager.ChargeUI(chargeAmount, held);
            yield return new WaitForSeconds(.1f);
        }
    }
#region Player Movement
    

    // Pickup handled by ItemPickupHandler
    

    private void AnimOverride(AnimationClip Idle, AnimationClip Swing) {
        if (animator == null) return;
        if (animatorController == null) return;
        if (Idle == null || Swing == null || originalIdle == null || originalSwing == null) return;
        // Create a new Animator Override Controller
        overrideController = new AnimatorOverrideController(animatorController);
        // Override the original Motions with the new Motions
        overrideController[originalIdle] = Idle;
        overrideController[originalSwing] = Swing;
        // Apply override
        animator.runtimeAnimatorController = overrideController;
    }
    void InputManagement(){
        float moveX = 0f;
        float moveY = 0f;
        var kb = UnityEngine.InputSystem.Keyboard.current;
        if (kb != null){
            if (kb.aKey.isPressed || kb.leftArrowKey.isPressed) moveX -= 1f;
            if (kb.dKey.isPressed || kb.rightArrowKey.isPressed) moveX += 1f;
            if (kb.sKey.isPressed || kb.downArrowKey.isPressed) moveY -= 1f;
            if (kb.wKey.isPressed || kb.upArrowKey.isPressed) moveY += 1f;
        }
        moveDir = new Vector2(moveX,moveY).normalized;
        if (moveDir.x != 0)
            lastHorizontalVector = moveDir.x;
        if (moveDir.y != 0)
            lastVerticalVector = moveDir.y;
    }
    public void OnMove(InputValue value){
        moveInput = value.Get<Vector2>();
    }
    // Call this when you want to apply force
    void AddForce(Rigidbody2D rbUnit) {
        StartCoroutine(FakeAddForceMotion(rbUnit));
    }
    
    IEnumerator FakeAddForceMotion(Rigidbody2D rbUnit) {
        while (true) {
            if (rbUnit == null) break;
            var rbTransform = rbUnit.transform;
            if (rbTransform == null) break;
            if (Vector2.Distance(rbTransform.position, transform.position) >= .5f) break;
            Vector2 direction = (rbTransform.position - transform.position).normalized;
            if (rbUnit != null) rbUnit.linearVelocity = direction;
            currentMoveSpeed = statManager.speed/5;
            yield return new WaitForEndOfFrame();
        }
        currentMoveSpeed = statManager.speed;
        if (rbUnit != null) rbUnit.linearVelocity = Vector2.zero;
        yield return null;
    }
#endregion


}