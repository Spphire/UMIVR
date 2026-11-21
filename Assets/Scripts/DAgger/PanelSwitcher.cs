using System.Collections.Generic;
using UnityEngine;

[ExecuteAlways]
public class PanelSwitcher : MonoBehaviour
{

    public List<GameObject> panels;
    // Start is called once before the first execution of Update after the MonoBehaviour is created
    void Start()
    {
        
    }

    // Update is called once per frame
    void Update()
    {
        
    }

    public void SwitchPanel(int panelIndex)
    {
        foreach (GameObject panel in panels)
        {
            panel.SetActive(false);
        }
        panels[panelIndex].SetActive(true);
        // refresh ui
        Canvas.ForceUpdateCanvases();
    }
}
