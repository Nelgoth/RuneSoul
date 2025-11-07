using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class MemManager : MonoBehaviour
{
    
    public GameObject lastTarget;
    public GameObject currentTarget;
    public string currentDesire;
    private StatManager statManager;
    private SenseManager senseManager;
    public List<string> allies = new List<string>();
    
    private void Start() {
        statManager = GetComponent<StatManager>();
        senseManager = GetComponent<SenseManager>();
    }

    public string GetDesire(){
        currentDesire = "Attack";
        return currentDesire;
    }

    public bool CheckAlly(string race) {
        int allyCount = 0;
        foreach (string ally in allies) {
            if (ally == race)
                allyCount ++;
        }
        if (allyCount > 0)
            return true;
        else    return false;
    }
}
