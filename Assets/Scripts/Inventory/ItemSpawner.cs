using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Utilities;
#endif

public class ItemSpawner : MonoBehaviour
{
	[Header("Item")] public ItemClass item;
	[Tooltip("How many of this item each pickup contains")] public int quantity = 1;

	[Header("Spawn Settings")]
	public Transform spawnPoint;
	[Tooltip("Number of pickups to spawn per trigger")] public int count = 1;
	[Tooltip("Random horizontal spread radius in meters")] public float spreadRadius = 0f;
	[Tooltip("Randomize yaw rotation when spawning")] public bool randomizeYaw = false;

	[Header("Triggers")]
	public bool spawnOnStart = false;
	public bool spawnOnKey = true;
	[Tooltip("New Input System action to trigger spawning (recommended)")]
#if ENABLE_INPUT_SYSTEM
	public InputActionReference spawnAction;
#endif
	[Tooltip("Old Input Manager fallback key (used only if the old Input Manager is active)")]
	public KeyCode spawnKey = KeyCode.P;

#if ENABLE_INPUT_SYSTEM
	void OnEnable(){ if (spawnAction != null) spawnAction.action.Enable(); }
	void OnDisable(){ if (spawnAction != null) spawnAction.action.Disable(); }
#endif

	void Start()
	{
		if (spawnOnStart)
			Spawn();
	}

	void Update()
	{
		if (spawnOnKey){
#if ENABLE_INPUT_SYSTEM
			if (spawnAction != null && spawnAction.action != null){
				if (spawnAction.action.WasPressedThisFrame()) Spawn();
			}
#else
			if (Input.GetKeyDown(spawnKey)) Spawn();
#endif
		}
	}

	public void Spawn()
	{
		if (item == null)
		{
			Debug.LogWarning($"{name}: No ItemClass assigned on ItemSpawner.");
			return;
		}
		if (quantity < 1) quantity = 1;
		if (count < 1) count = 1;

		Vector3 basePos = spawnPoint != null ? spawnPoint.position : transform.position;
		Quaternion baseRot = spawnPoint != null ? spawnPoint.rotation : transform.rotation;

		for (int i = 0; i < count; i++)
		{
			Vector3 pos = basePos;
			if (spreadRadius > 0f)
			{
				Vector2 circle = Random.insideUnitCircle * spreadRadius;
				pos += new Vector3(circle.x, 0f, circle.y);
			}
			Quaternion rot = baseRot;
			if (randomizeYaw)
			{
				rot = Quaternion.Euler(0f, Random.Range(0f, 360f), 0f);
			}
			ItemSpawnService.Spawn(item, quantity, new SpawnOptions{ position = pos, rotation = rot, markAsDrop = true });
		}
	}
}


