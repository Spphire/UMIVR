using System.Collections.Generic;
using Oculus.Interaction;
using UnityEngine;
using UnityEngine.UI;

[ExecuteAlways]
public class UIHandler : MonoBehaviour
{
    public Transform rootTransform;
    
    public List<CanvasGroup> showHideList;

    public bool show = true;
    // Start is called once before the first execution of Update after the MonoBehaviour is created
    void Start()
    {
        
    }

    // Update is called once per frame
    void Update()
    {
        if (showHideList.Count > 0)
        {
            if (show != showHideList[0].interactable)
            {
                show = !show;
                ShowHide();
            }
        }
    }

    public void ShowHide()
    {
        show = !show;
        foreach (var cg in showHideList)
        {
            cg.alpha = show ? 1f : 0f;
            cg.interactable = show;
            cg.blocksRaycasts = show;
            
            // var raycaster = cg.GetComponent<GraphicRaycaster>();
            // raycaster.enabled = show;

            RayInteractable ri = cg.GetComponentInChildren<RayInteractable>();
            if (ri != null)
            {
                ri.enabled = show;
                ri.transform.GetChild(0).gameObject.SetActive(show);
            }
            
            MeshRenderer mr = cg.GetComponentInChildren<MeshRenderer>();
            if (mr != null)
            {
                mr.enabled = show;
            }

        }
    }
}
