using UnityEngine;
using UnityEngine.UI;

public class InventoryUIController : MonoBehaviour
{
	[SerializeField] private GameObject itemCursor;
	[SerializeField] public InventoryManager inventory;
	[SerializeField] public InventoryManager crafting;
	[SerializeField] public InventoryManager output;
	[SerializeField] public InventoryManager cook;

	public static bool InventoryOpen { get; private set; }

	private SlotClass movingSlot;
	private SlotClass tempSlot;
	private SlotClass originalSlot;
	private InventoryManager originalInventory;
	private InventoryManager newInventory;

	void Start()
	{
		if (inventory != null && (inventory.items == null || inventory.items.Length == 0)) inventory.Initializer();
		if (crafting != null && (crafting.items == null || crafting.items.Length == 0)) crafting.Initializer();
		if (output != null && (output.items == null || output.items.Length == 0)) output.Initializer();
		if (cook != null && (cook.items == null || cook.items.Length == 0)) cook.Initializer();
	}

	void Update()
	{
		var kb = UnityEngine.InputSystem.Keyboard.current;
		var mouse = UnityEngine.InputSystem.Mouse.current;
		if (kb != null)
		{
			if (kb.iKey.wasPressedThisFrame) inventory?.OpenInv();
			if (kb.cKey.wasPressedThisFrame)
			{
				crafting?.OpenInv();
				output?.OpenInv();
			}
			if (kb.escapeKey.wasPressedThisFrame)
			{
				var panels = GameObject.FindGameObjectsWithTag("InvUI");
				for (int i = 0; i < panels.Length; i++) panels[i].GetComponent<InventoryManager>().OpenInv();
			}
		}

		// Track whether any inventory panel is open
		InventoryOpen = GameObject.FindGameObjectsWithTag("InvUI").Length > 0;

		// Drag/drop inputs only when inventory UI is open
		if (InventoryOpen && mouse != null)
		{
			bool leftDown = mouse.leftButton.wasPressedThisFrame;
			bool rightDown = mouse.rightButton.wasPressedThisFrame;
			if (leftDown)
			{
				if (movingSlot != null && movingSlot.item != null) EndItemMove();
				else BeginItemMove();
			}
			else if (rightDown)
			{
				if (movingSlot != null && movingSlot.item != null) EndItemMove_Single();
				else BeginItemMove_Half();
			}
		}

		// Cursor sprite
		if (itemCursor != null)
		{
			bool moving = movingSlot != null && movingSlot.item != null;
			itemCursor.SetActive(moving);
			if (moving)
			{
				var img = itemCursor.GetComponent<Image>();
				if (img != null) img.sprite = movingSlot.item.itemIcon;
				var rt = itemCursor.GetComponent<RectTransform>();
				if (rt != null && mouse != null) rt.position = mouse.position.ReadValue();
			}
		}

		// Hotbar scroll
		if (inventory != null && inventory.hotbarSlots != null && inventory.hotbarSlots.Length > 0 && mouse != null)
		{
			float scrollY = mouse.scroll.ReadValue().y;
			if (scrollY > 0) inventory.selectedSlotIndex = Mathf.Clamp(inventory.selectedSlotIndex + 1, 0, inventory.hotbarSlots.Length - 1);
			else if (scrollY < 0) inventory.selectedSlotIndex = Mathf.Clamp(inventory.selectedSlotIndex - 1, 0, inventory.hotbarSlots.Length - 1);
			if (inventory.hotbarSelector != null && inventory.hotbarSlots[inventory.selectedSlotIndex] != null)
				inventory.hotbarSelector.transform.position = inventory.hotbarSlots[inventory.selectedSlotIndex].transform.position;
			int baseIndex = inventory.hotbarSlots.Length * 3;
			int selectedIndex = inventory.selectedSlotIndex + baseIndex;
			if (inventory.items != null && selectedIndex >= 0 && selectedIndex < inventory.items.Length)
				inventory.selectedItem = inventory.items[selectedIndex];
		}
	}

	private bool BeginItemMove()
	{
		originalInventory = GetClosestInv();
		originalSlot = GetClosestSlot(originalInventory);
		if (originalSlot == null || originalSlot.item == null) return false;
		movingSlot = new SlotClass(originalSlot);
		originalSlot.Clear();
		originalInventory.RefreshUI();
		return true;
	}

	private bool BeginItemMove_Half()
	{
		originalInventory = GetClosestInv();
		originalSlot = GetClosestSlot(originalInventory);
		if (originalSlot == null || originalSlot.item == null) return false;
		movingSlot = new SlotClass(originalSlot.item, Mathf.CeilToInt(originalSlot.quantity / 2f), originalSlot.item.durability, originalSlot.toolType);
		originalSlot.SubQuantity(Mathf.CeilToInt(originalSlot.quantity / 2f));
		if (originalSlot.quantity == 0) originalSlot.Clear();
		originalInventory.RefreshUI();
		return true;
	}

	private bool EndItemMove()
	{
		newInventory = GetClosestInv();
		originalSlot = GetClosestSlot(newInventory);
		if (originalSlot == null)
		{
			originalInventory.Add(movingSlot.item, movingSlot.quantity, movingSlot.durability, movingSlot.toolType, false);
			movingSlot.Clear();
		}
		else
		{
			if (originalSlot.item != null)
			{
				if (originalSlot.item == movingSlot.item && originalSlot.item.isStackable && originalSlot.quantity < originalSlot.item.stackSize)
				{
					var quantityCanAdd = originalSlot.item.stackSize - originalSlot.quantity;
					var quantityToAdd = Mathf.Clamp(movingSlot.quantity, 0, quantityCanAdd);
					var remainder = movingSlot.quantity - quantityToAdd;
					originalSlot.AddQuantity(quantityToAdd);
					if (remainder <= 0) movingSlot.Clear();
					else
					{
						movingSlot.SubQuantity(quantityCanAdd);
						newInventory.RefreshUI();
						return false;
					}
				}
				else
				{
					tempSlot = new SlotClass(originalSlot);
					originalSlot.AddItem(movingSlot.item, movingSlot.quantity, movingSlot.durability, movingSlot.toolType);
					movingSlot.AddItem(tempSlot.item, tempSlot.quantity, tempSlot.durability, tempSlot.toolType);
					newInventory.RefreshUI();
					return true;
				}
			}
			else
			{
				originalSlot.AddItem(movingSlot.item, movingSlot.quantity, movingSlot.durability, movingSlot.toolType);
				movingSlot.Clear();
			}
		}
		newInventory.RefreshUI();
		return true;
	}

	private bool EndItemMove_Single()
	{
		newInventory = GetClosestInv();
		originalSlot = GetClosestSlot(newInventory);
		if (originalSlot == null) return false;
		if (originalSlot.item != null && (originalSlot.item != movingSlot.item || originalSlot.quantity >= originalSlot.item.stackSize || movingSlot.item.isStackable == false)) return false;
		if (originalSlot.item != null && originalSlot.item == movingSlot.item) originalSlot.AddQuantity(1);
		else originalSlot.AddItem(movingSlot.item, 1, movingSlot.durability, movingSlot.toolType);
		movingSlot.SubQuantity(1);
		if (movingSlot.quantity < 1) movingSlot.Clear();
		newInventory.RefreshUI();
		return true;
	}

	private InventoryManager GetClosestInv()
	{
		var mouse = UnityEngine.InputSystem.Mouse.current;
		Vector2 screenPos = mouse != null ? mouse.position.ReadValue() : (Vector2)UnityEngine.Input.mousePosition;
		InventoryManager fallback = null;
		float bestDist = float.MaxValue;
		var panels = GameObject.FindGameObjectsWithTag("InvUI");
		for (int i = 0; i < panels.Length; i++)
		{
			var invGO = panels[i];
			var inv = invGO.GetComponent<InventoryManager>();
			var rt = invGO.GetComponent<RectTransform>();
			if (rt != null && RectTransformUtility.RectangleContainsScreenPoint(rt, screenPos)) return inv;
			if (rt != null)
			{
				float d = Vector2.Distance((Vector2)rt.position, screenPos);
				if (d < bestDist)
				{
					bestDist = d;
					fallback = inv;
				}
			}
		}
		return fallback;
	}

	private SlotClass GetClosestSlot(InventoryManager inv)
	{
		var mouse = UnityEngine.InputSystem.Mouse.current;
		Vector2 screenPos = mouse != null ? mouse.position.ReadValue() : (Vector2)UnityEngine.Input.mousePosition;
		for (int i = 0; i < inv.slots.Length; i++)
		{
			var rt = inv.slots[i].GetComponent<RectTransform>();
			if (rt != null && RectTransformUtility.RectangleContainsScreenPoint(rt, screenPos)) return inv.items[i];
		}
		return null;
	}
}


