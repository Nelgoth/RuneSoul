using System.Collections;
using UnityEngine;
using UnityEngine.Rendering.Universal;

public class FireHandler : MonoBehaviour
{
    public ParticleSystem flameParticals;
    public Light2D flickerLight;
    public Light2D angleLight;
    public Light2D glowLight;
    private StatManager statManager;
    private float intensity = 1;
    private float falloff = .25f;
    private float innerAngle = 50;
    private bool flickerUp;
    private bool falloffUp;
    private bool flickerAngleUp;
    public bool isLit = false;

    public void Start(){
        var emission = flameParticals.emission;
        emission.enabled = false;
        statManager = GetComponentInParent<StatManager>();
    }

    public void Light(){
        if(!isLit){
            if (flameParticals is not null){
                var emission = flameParticals.emission;
                emission.enabled = true;
            }
            if (flickerLight is not null) {
                flickerLight.enabled = true;
                StartCoroutine(Flicker());
            }
            if (glowLight is not null){
                glowLight.enabled = true;
                StartCoroutine(FlickerFalloff());
            }
            if (angleLight is not null){
                angleLight.enabled =true;
                StartCoroutine(FlickerAngle());
            }
            isLit = true;
            StartCoroutine(ConsumeFuel());
        }
    }
    public void Extinguish(){    
        if (isLit){
            if (flameParticals is not null){
                var emission = flameParticals.emission;
                emission.enabled = false;
            }
            if (flickerLight is not null) {
                flickerLight.enabled = false;
                StopCoroutine(Flicker());
            }
            if (glowLight is not null){
                glowLight.enabled = false;
                StopCoroutine(FlickerFalloff());
            }
            if (angleLight is not null){
                angleLight.enabled =false;
                StopCoroutine(FlickerAngle());
            }
            isLit = false;
        }
    }

    private IEnumerator ConsumeFuel(){
        while(true){
            float fuelTime = 0;
            bool hasFuel = false;
            for (int i = 0; i < statManager.inventory.items.Length; i++){
                if (statManager.inventory.items[i].item is not null && statManager.inventory.items[i].item.isFuel == true){
                    Debug.Log("Contains Fuel");
                    Debug.Log(statManager.inventory.items[i].item.fuelAmount);
                    fuelTime =  statManager.inventory.items[i].item.fuelAmount;
                    statManager.inventory.items[i].SubQuantity(1);
                    hasFuel = true;
                }
            }
            if(hasFuel)
                yield return new WaitForSeconds(fuelTime);
            else {    
                Debug.Log("No Fuel");
                Extinguish();
                yield break;
            }
        }
    }

    private IEnumerator Flicker(){
        while(true){
            if (intensity <= 1)
                flickerUp = true;
            if (intensity >= 1.5f)
                flickerUp = false;
            if(flickerUp)
                intensity += Random.Range(-.05f, .15f);
            if(!flickerUp)
                intensity -= Random.Range(0, .15f);
            if (flickerLight is not null) flickerLight.intensity = intensity;
            if (angleLight is not null) angleLight.intensity = intensity;
            if (glowLight is not null) glowLight.intensity = intensity/3;  
            yield return new WaitForSeconds(.1f);
        }
    }
    private IEnumerator FlickerFalloff(){
        while(true){
            if (falloff <= .3)
                falloffUp = true;
            if (falloff >= .4f)
                falloffUp = false;
            if(falloffUp)
                falloff += Random.Range(0f, .05f);
            if(!falloffUp)
                falloff -= Random.Range(0f, .05f);
            if (flickerLight is not null) flickerLight.falloffIntensity = falloff;
            if (angleLight is not null) angleLight.falloffIntensity = falloff;
            if (glowLight is not null) glowLight.falloffIntensity = falloff*2.75f; 
            yield return new WaitForSeconds(.1f);
        }
    }
     private IEnumerator FlickerAngle(){
        while(true){
            
            if (innerAngle <= 20f)
                flickerAngleUp = true;
            if (innerAngle >= 50f)
                flickerAngleUp = false;
            if(flickerAngleUp)
                innerAngle += Random.Range(0f, 15f);
            if(!flickerAngleUp)
                innerAngle -= Random.Range(0, 15f);
            angleLight.pointLightInnerAngle = innerAngle;
                
            yield return new WaitForSeconds(.1f);
        }

    }
    /*float innerAngle =50;
                float outerAngle =100;
                innerAngle -= Random.Range(0, 25);
                light.pointLightInnerAngle = innerAngle;
                light.pointLightOuterAngle = outerAngle;
    */
}