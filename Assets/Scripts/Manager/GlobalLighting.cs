using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering.Universal;

public class GlobalLighting : MonoBehaviour
{
    public Light2D lightObject;
    public float lightingLevel;
    // Start is called before the first frame update
    void Start()
    {
        lightObject = GetComponent<Light2D>();
    }

    // Update is called once per frame
    void Update()
    {
        lightingLevel = lightObject.intensity;
    }
}
