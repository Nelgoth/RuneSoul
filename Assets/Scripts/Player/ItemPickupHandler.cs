using UnityEngine;

public class ItemPickupHandler : MonoBehaviour
{
	[SerializeField] private InventoryManager inventory;

	void Awake()
	{
		if (inventory == null) inventory = GetComponent<InventoryManager>();
		if (inventory == null)
		{
			var pc = GetComponent<PlayerController>();
			if (pc != null) inventory = pc.inventory;
		}
	}

	void OnTriggerEnter(Collider col)
	{
		TryPickup(col);
	}

	void OnTriggerStay(Collider col)
	{
		TryPickup(col);
	}

	private void TryPickup(Collider col)
	{
		var itemController = col.GetComponent<ItemController>();
		if (itemController == null) itemController = col.GetComponentInParent<ItemController>();
		if (itemController == null || inventory == null) return;
		if (!inventory.isFull() && itemController.canPickup == true)
		{
			if (itemController.TryMarkPickedUpAndDisableColliders())
			{
				inventory.Add(itemController.item.item, itemController.item.quantity, itemController.item.durability, itemController.item.item.toolType.ToString(), false);
				itemController.Remove();
			}
		}
	}
}


