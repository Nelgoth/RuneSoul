using UnityEngine;

public class Projectile : MonoBehaviour
{
    Rigidbody rb;
    bool isMoving = false;
    private ItemController itemController;
    public int customRotation;
    private float initialFlightTime;
    public float flightTime;
    public float flightCurve;
    private float speed;
    private float Charge;
    private Vector3 currVel;
    private Vector3 prevPosition;

    void Awake()
    {
        rb = GetComponent<Rigidbody>();
        itemController = GetComponent<ItemController>();
    }

    void Start()
    {
        if (rb != null){
            float angle = Mathf.Atan2(rb.linearVelocity.z, rb.linearVelocity.x) * Mathf.Rad2Deg;
            transform.rotation = Quaternion.AngleAxis(angle + customRotation, Vector3.up);
        }
    }

    void Update()
    {
        if (isMoving)
        {
            Debug.Log(speed);
            // Reduce the velocity gradually based on the flight curve
            if (rb != null) rb.linearVelocity *= Mathf.Pow(flightCurve, Time.deltaTime);
            flightTime -= .03f;
            currVel = (transform.position - prevPosition) / Time.deltaTime;
            if(currVel.magnitude != 0)
                speed = currVel.magnitude;
            prevPosition = transform.position;
            if (flightTime <= 0)
            {
                if (rb != null) rb.linearVelocity = Vector3.zero;
                isMoving = false;
                if (rb != null) rb.isKinematic = true;
                var col = GetComponent<Collider>();
                if (col != null) col.isTrigger = true;
            }
        }
        
    }

    public void Fired(float range, float charge)
    {
        flightTime = (itemController.item.item.range + range) * charge;
        Charge = charge;
        initialFlightTime = flightTime;
        isMoving = true;
    }

    private void OnCollisionEnter(Collision collision)
    {
        if (collision.gameObject.TryGetComponent(out IStatus hit) && isMoving)
        {
            if (speed > 1000)
                speed = Charge * 10;
            Debug.Log("Adjusted Speed " + speed);
            hit.Damage(itemController.item.item.dmg * (speed/50), itemController.item.item.toolType.ToString());
            Destroy(gameObject);
        }
    }
}
