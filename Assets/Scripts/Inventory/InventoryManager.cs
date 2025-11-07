using UnityEngine;
using UnityEngine.UI;


public class InventoryManager : MonoBehaviour{
    [SerializeField] public GameObject owner;
    [SerializeField] private GameObject slotHolder;
    [SerializeField] private GameObject hotbarSlotHolder;
    [SerializeField] public GameObject hotbarSelector;
    [SerializeField] private SlotClass[] startingItems;
    public SlotClass[]  items;
    public SlotClass overlayItem;
    public GameObject[] slots;
    public GameObject overlaySlot;
    public GameObject[] hotbarSlots;
    public SlotClass movingSlot;
    public SlotClass originalSlot;
    [SerializeField] public int selectedSlotIndex = 0;
    public SlotClass selectedItem;
    public bool invToggle = false;
    private HoverTipManager hoverTipManager;
    public bool hotBars;
    public bool hasOverlay;
    public bool internalOnly;
    
    public void Initializer(){  
        if(hotBars){
            if (hotbarSlotHolder == null){
                Debug.LogWarning($"{name}: hotBars enabled but hotbarSlotHolder is not assigned.");
            } else {
                hotbarSlots = new GameObject[hotbarSlotHolder.transform.childCount];
                for (int i = 0; i < hotbarSlots.Length; i++)    hotbarSlots[i] = hotbarSlotHolder.transform.GetChild(i).gameObject;
            }
        }
        hoverTipManager =  GetComponentInParent<HoverTipManager>();
        if (slotHolder == null){
            Debug.LogError($"{name}: slotHolder is not assigned on InventoryManager. Initialization aborted.");
            return;
        }
        slots = new GameObject[slotHolder.transform.childCount];
        items = new SlotClass[slots.Length];
        for (int i = 0; i< items.Length; i++)   items[i] = new SlotClass();
        //set all the slots
        if(hasOverlay)
            overlayItem = new SlotClass();
        for(int i = 0; i < slotHolder.transform.childCount; i++)    slots[i] = slotHolder.transform.GetChild(i).gameObject;
        //init start items
        if (startingItems != null){
            for (int i = 0; i< startingItems.Length; i++) {  
                var start = startingItems[i];
                if (start == null || start.item == null) continue;
                Add(start.item, start.quantity, start.item.maxDurability, start.item.toolType.ToString(), false);
            }
        }
        RefreshUI();
        
    }
    public void OpenInv(){
         if(invToggle == true){
                gameObject.SetActive(false);
                if(hotBars){
                    hotbarSlotHolder.SetActive(true);
                    hotbarSelector.SetActive(true);
                }
                hoverTipManager.HideTip();
                invToggle = false;
            }
            else{
                gameObject.SetActive(true);
                if(hotBars){
                    hotbarSlotHolder.SetActive(false);
                    hotbarSelector.SetActive(false);                
                }
                invToggle = true;
            }
    }
    
    #region Inventory Utils
    public void RefreshUI(){
        if (slots == null || items == null) return;
        for(int i = 0; i<slots.Length; i++){
            try {
                if (slots[i] == null || items.Length <= i || items[i] == null || items[i].item == null){
                    throw new System.NullReferenceException();
                }
                slots[i].transform.GetChild(0).GetComponent<Image>().enabled = true;
                slots[i].transform.GetChild(0).GetComponent<Image>().sprite = items[i].item.itemIcon;
                var hoverComp = slots[i].transform.GetComponent<HoverTip>();
                if (hoverComp != null) hoverComp.enabled = true;
                if (items[i].item.isDurable){
                    slots[i].transform.GetComponent<HoverTip>().tipToShow = items[i].item.itemName + "<br>" +  
                    items[i].durability + " / " + items[i].item.maxDurability + "<br>" + 
                    items[i].toolType;
                    slots[i].transform.GetChild(2).GetChild(0).GetComponent<Image>().enabled = true;
                    slots[i].transform.GetChild(2).GetChild(1).GetComponent<Image>().enabled = true;
                    slots[i].transform.GetChild(2).GetChild(1).GetComponent<Image>().fillAmount = items[i].durability / items[i].item.maxDurability;
                }
                else {
                    slots[i].transform.GetChild(2).GetChild(0).GetComponent<Image>().enabled = false;
                    slots[i].transform.GetChild(2).GetChild(1).GetComponent<Image>().enabled = false;
                }
                if (items[i].item.isStackable){
                    slots[i].transform.GetChild(1).GetComponent<TMPro.TextMeshProUGUI>().text = items[i].quantity + "";
                    slots[i].transform.GetComponent<HoverTip>().tipToShow = "";
                }
                else   
                    slots[i].transform.GetChild(1).GetComponent<TMPro.TextMeshProUGUI>().text = "";
            }
            catch{
                if (slots != null && i >= 0 && i < slots.Length && slots[i] != null){
                    var slotTransform = slots[i].transform;
                    if (slotTransform.childCount > 0){
                        var iconImage = slotTransform.GetChild(0).GetComponent<Image>();
                        if (iconImage != null){
                            iconImage.sprite = null;
                            iconImage.enabled = false;
                        }
                    }
                    var hover = slotTransform.GetComponent<HoverTip>();
                    if (hover != null && hoverTipManager != null) hoverTipManager.HideTip();
                    if (slotTransform.childCount > 1){
                        var qtyText = slotTransform.GetChild(1).GetComponent<TMPro.TextMeshProUGUI>();
                        if (qtyText != null) qtyText.text = "";
                    }
                    if (slotTransform.childCount > 2){
                        var barParent = slotTransform.GetChild(2);
                        if (barParent.childCount > 0){
                            var img0 = barParent.GetChild(0).GetComponent<Image>();
                            if (img0 != null) img0.enabled = false;
                        }
                        if (barParent.childCount > 1){
                            var img1 = barParent.GetChild(1).GetComponent<Image>();
                            if (img1 != null) img1.enabled = false;
                        }
                    }
                    if (hover != null) hover.tipToShow = "";
                }
                if(hasOverlay && overlaySlot != null){
                    overlaySlot.transform.GetChild(0).GetComponent<Image>().sprite = null;
                    overlaySlot.transform.GetChild(0).GetComponent<Image>().enabled = false;
                    overlaySlot.transform.GetChild(1).GetComponent<TMPro.TextMeshProUGUI>().text = "";
                }
            }
        }
        if(hotBars)
            RefreshHotbar();
        if(hasOverlay){
            RefreshOverlay();
        }
    }
    public void RefreshHotbar(){
        if (!hotBars || hotbarSlots == null || items == null) return;
        int baseIndex = hotbarSlots.Length * 3;
        for(int i = 0; i < hotbarSlots.Length; i++){
            try {
                if (hotbarSlots[i] == null || items.Length <= i + baseIndex || items[i + baseIndex] == null || items[i + baseIndex].item == null){
                    throw new System.NullReferenceException();
                }
                hotbarSlots[i].transform.GetChild(0).GetComponent<Image>().enabled = true;
                hotbarSlots[i].transform.GetChild(0).GetComponent<Image>().sprite = items[i + baseIndex].item.itemIcon;
                if (items[i + baseIndex].item.isDurable) {
                    hotbarSlots[i].transform.GetComponent<HoverTip>().tipToShow = items[i + baseIndex].item.itemName + "<br>" +   
                    items[i + baseIndex].durability + " / " +  items[i + baseIndex].item.maxDurability + "<br>" + 
                    items[i + baseIndex].toolType;
                    hotbarSlots[i].transform.GetChild(2).GetChild(0).GetComponent<Image>().enabled = true;
                    hotbarSlots[i].transform.GetChild(2).GetChild(1).GetComponent<Image>().enabled = true;
                    hotbarSlots[i].transform.GetChild(2).GetChild(1).GetComponent<Image>().fillAmount = items[i + baseIndex].durability / items[i + baseIndex].item.maxDurability;
                }
                else {
                    hotbarSlots[i].transform.GetChild(2).GetChild(0).GetComponent<Image>().enabled = false;
                    hotbarSlots[i].transform.GetChild(2).GetChild(1).GetComponent<Image>().enabled = false;
                }
                if (items[i + baseIndex].item.isStackable){
                    hotbarSlots[i].transform.GetChild(1).GetComponent<TMPro.TextMeshProUGUI>().text = items[i + baseIndex].quantity + "";
                    hotbarSlots[i].transform.GetComponent<HoverTip>().tipToShow  = "";
                }
                else   
                    hotbarSlots[i].transform.GetChild(1).GetComponent<TMPro.TextMeshProUGUI>().text = "";
            }
            catch{
                hotbarSlots[i].transform.GetChild(0).GetComponent<Image>().sprite = null;
                hotbarSlots[i].transform.GetChild(0).GetComponent<Image>().enabled = false;
                hotbarSlots[i].transform.GetComponent<HoverTip>().tipToShow = "";
                hotbarSlots[i].transform.GetChild(1).GetComponent<TMPro.TextMeshProUGUI>().text = "";
                hotbarSlots[i].transform.GetChild(2).GetChild(0).GetComponent<Image>().enabled = false;
                hotbarSlots[i].transform.GetChild(2).GetChild(1).GetComponent<Image>().enabled = false;
            }
        }
    }

    public void RefreshOverlay(){
        try{       
            if(items[0].item is not null)
                throw new System.Exception();
            overlaySlot.SetActive(true);
            overlaySlot.transform.GetChild(0).GetComponent<Image>().enabled = true;
            overlaySlot.transform.GetChild(0).GetComponent<Image>().sprite = overlayItem.item.itemIcon;
            if(overlayItem.item.isStackable) {
                overlaySlot.transform.GetChild(1).GetComponent<TMPro.TextMeshProUGUI>().text = overlayItem.quantity + "";
                overlaySlot.transform.GetComponent<HoverTip>().tipToShow  = "";
            }
            else    
                overlaySlot.transform.GetChild(1).GetComponent<TMPro.TextMeshProUGUI>().text = "";
            
        }
        catch{
            
            overlaySlot.SetActive(false);
            overlaySlot.transform.GetChild(0).GetComponent<Image>().sprite = null;
            overlaySlot.transform.GetChild(0).GetComponent<Image>().enabled = false;
            overlaySlot.transform.GetComponent<HoverTip>().tipToShow = "";
            overlaySlot.transform.GetChild(1).GetComponent<TMPro.TextMeshProUGUI>().text = "";
        }
    }

    public bool Add(ItemClass item, int quantity, float durability, string toolType, bool overlay){
        //check if inventory contains item
        SlotClass slot = Contains(item);
        if (overlay){
            overlayItem.AddItem(item, quantity, durability, toolType);
            RefreshUI();
            return true;
        }
        if (slot is not null && slot.item.isStackable && slot.quantity < item.stackSize){
            var quantityCanAdd = slot.item.stackSize - slot.quantity;
            var quantityToAdd = Mathf.Clamp(quantity, 0, quantityCanAdd);
            var remainder = quantity - quantityCanAdd;
            slot.AddQuantity(quantityToAdd);
            if (remainder > 0)  Add(item, remainder, durability, toolType, false);
        }
        else{
            for ( int i = 0; i < items.Length; i ++){
                    if (items[i].item == null){ //this slot is empty
                        var quantityCanAdd = item.stackSize - items[i].quantity;
                        var quantityToAdd = Mathf.Clamp(quantity, 0, quantityCanAdd);
                        var remainder = quantity - quantityCanAdd;
                        items[i].AddItem(item, quantityToAdd, durability, toolType);
                        if (remainder > 0)  Add(item, remainder, durability, toolType, false);
                        break;
                    }
                }
        }     
        RefreshUI();
        return true;
    }
    public bool Remove(ItemClass item, SlotClass slotItem){
        SlotClass temp;
        if(slotItem is null) 
            temp = ContainsAny(item);
        else temp = slotItem;
        if (temp != null)
            if (temp.quantity > 1)
                temp.SubQuantity(1);
            else    temp.Clear();
        else    return false;
        RefreshUI();
        return true;
    }
    public bool Remove(ItemClass item, int quantity, bool overlay){
        SlotClass temp = ContainsAny(item);
        if (overlay){
            overlayItem.Clear();
            RefreshUI();
            return true;
        }
        if (temp != null){
            if (temp.quantity > 1){
                temp.SubQuantity(quantity);
            }
            else{
                int slotToRemoveIndex = 0;
                for(int i = 0; i < items.Length; i++){
                    if(items[i].item == item){
                        slotToRemoveIndex = i;
                        break;
                    }
                }
                items[slotToRemoveIndex].Clear();
            }
        }
        else{
            return false;
        }
        RefreshUI();
        return true;
    }
    public void UseSelectedConsumable(){
        selectedItem.SubQuantity(1);
        RefreshUI();
    }
    public void UseSelected(){
        SlotClass item = items[selectedSlotIndex + (hotbarSlots.Length * 3)];
        item.SubDurability(1);
        if (item.durability <= 0){
            if(item.item.DropItems(owner))
                Remove(null, item);
        }
        RefreshUI();
    }
    public void UseSelected(SlotClass item){
        //SlotClass itemSlot = ContainsAny(item);
        item.SubDurability(1);
        if (item.durability <= 0){
             if(item.item.DropItems(owner))
                Remove(null, item);
        }
        RefreshUI();
    }
    public bool isFull(){
        for (int i=0; i < items.Length; i++){
            if (items[i].item == null){
                return false;
            }
        }
        return true;
    }
    public SlotClass Contains(ItemClass item){
       for (int i = 0; i< items.Length; i++){
            if (items[i].item == item && items[i].quantity < items[i].item.stackSize){
                return items[i];
            }
        }
        return null;
    }
    
    public SlotClass ContainsAny(ItemClass item){
       for (int i = 0; i< items.Length; i++){
            if (items[i].item == item){
                return items[i];
            }
        }
        return null;
    }
    public GameObject SlotContains(ItemClass item){
       for (int i = 0; i< items.Length; i++){
            if (items[i].item == item){
                return slots[i];
            }
        }
        return null;
    }
    public bool Contains(ItemClass item, int quantity){
        for (int i = 0; i< items.Length; i++){
            if (items[i].item == item && items[i].quantity >= quantity )
                return true;
        }
        return false;
    }
    public SlotClass Contains(string toolType){
        for (int i = 0; i< items.Length; i++){
            if (items[i].item is not null && items[i].toolType.ToString() == toolType)
                return items[i];
        }
        return null;
    }
    #endregion Inventory Utils
}
