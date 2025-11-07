using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using System;

public class SenseManager : MonoBehaviour {

    //[SerializeField] private float radius = 5f;
    private StatManager statManager;
    private GlobalLighting globalLighting;
    public int objCount;
    public int Counter;
    public Collider[] colList;
    public List<GameObject> targetList;
    private Transform target;
    public float lightingLevel;
    void Start(){
        statManager = GetComponent<StatManager>();
        globalLighting = FindFirstObjectByType<GlobalLighting>();
        StartCoroutine(senseObj());
        StartCoroutine(Lighting());
    }


    IEnumerator Lighting(){
        while(true){        
            lightingLevel = globalLighting.lightingLevel;
            TargetList("Light Source");
            foreach (GameObject target in targetList){
                lightingLevel += (statManager.perception - TargetDistance(target))*.1f;
            }
            yield return new WaitForSeconds(1);
        }
    }


    IEnumerator senseObj() {
        while (true){
            colList = Physics.OverlapSphere(transform.position, statManager.perception);
            foreach (Collider hitCol in colList) {
                    Counter++;
            }
            objCount = Counter;
            Counter = 0;
            yield return new WaitForSeconds(1);
        }
    }

    public Transform TargetCheck(string targetType){
        target = null;
        if (colList.Length > 0){
            for (int i = 0, x = -1; i < colList.Length || i==x; i++) {
                if (colList[i] == null) continue;
                if (colList[i].gameObject.tag == targetType) {
                    target = colList[i].transform;
                    x=i;
                }
            }
            return target;
        }
        else return null;
    }

    public List<GameObject> TargetList(string targetType) {
        targetList.Clear();
        if (colList.Length > 0) {
            for (int i = 0; i < colList.Length; i++) {
                if (colList[i] == null) continue;
                if (colList[i].gameObject.tag == targetType) {
                    targetList.Add(colList[i].gameObject);                
                }
            }
            return targetList;
        }
        else return null;
    }

    public float TargetDistance(GameObject target){
        return Vector3.Distance(target.transform.position, transform.position);
        
    }
}

