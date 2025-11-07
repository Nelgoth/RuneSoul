using System.Collections;
using UnityEngine;

[System.Serializable]
public class SlotClass {
   [field: SerializeField] public ItemClass item { get; private set; } = null;
   [field: SerializeField] public int quantity { get; private set; } = 0;
   [field: SerializeField] public float durability { get; private set; } = 0;
   [field: SerializeField] public string toolType { get; private set; } = null;
    public SlotClass(){
        item = null;
        quantity = 0;
        durability = 0;
        toolType = null;
    }
    public SlotClass(ItemClass _item, int _quantity, float _durability, string _toolType){
        item = _item;
        quantity = _quantity;
        durability = _durability;
        toolType = _toolType;
    }
    public SlotClass(SlotClass slot){
        item = slot.item;
        quantity = slot.quantity;
        durability = slot.durability;
        toolType = slot.toolType;
    }
    public void Clear(){
        item = null;
        quantity = 0;
        durability = 0;
        toolType = null;
    }
    public void AddQuantity(int _quantity) {quantity += _quantity;}
    public void SubQuantity(int _quantity) {
            quantity -= _quantity;
            if (quantity <= 0){
                Clear();
            }
        }
    public void AddItem(ItemClass item, int quantity, float durability, string toolType){
        this.item = item;
        this.quantity = quantity;
        this.durability = durability;
        this.toolType = toolType;
    }
    public void AddDurability(float _durability) {durability += _durability;}
    public void SubDurability(float _durability) {durability -= _durability;}
}
