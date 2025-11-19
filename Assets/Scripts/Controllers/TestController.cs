using UnityEngine;

[RequireComponent(typeof(CharacterController))]
public class SimpleMovement : MonoBehaviour
{
    public float moveSpeed = 5f;
    private CharacterController controller;
    
    void Start()
    {
        controller = GetComponent<CharacterController>();
    }
    
    void Update()
    {
        // Get input
        float horizontal = Input.GetAxis("Horizontal"); // A/D or Left/Right arrows
        float vertical = Input.GetAxis("Vertical");     // W/S or Up/Down arrows
        
        // Calculate movement
        Vector3 move = new Vector3(horizontal, 0, vertical);
        move = transform.TransformDirection(move);
        move *= moveSpeed;
        
        // Apply gravity
        move.y -= 9.81f * Time.deltaTime;
        
        // Move
        controller.Move(move * Time.deltaTime);
    }
}