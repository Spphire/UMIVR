using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

public class VRMenu : MonoBehaviour
{
    [Header("Head Reference")]
    public Transform headTransform;          // 通常是 XR Camera / CenterEye

    [Header("Menu Position")]
    public float distanceFromHead = 1.2f;    // 菜单距离头显的距离
    public float verticalOffset = 0f;        // 整体上下偏移

    [Header("UI Layout")]
    public float itemSpacing = 0.15f;         // UIHandler 之间的间隔
    public List<UIHandler> uiHandlers = new List<UIHandler>();

    [Header("ArUco")]
    public Toggle switchArUco;
    
    private void Start()
    {
        if (headTransform == null)
        {
            headTransform = Camera.main.transform;
        }
    }

    void Update()
    {
        UpdateMenuTransform();

        // Quest 菜单按钮（可根据你项目替换）
        if (OVRInput.GetDown(OVRInput.RawButton.Start))
        {
            LayoutUIHandlers();
        }
    }

    /// <summary>
    /// 始终保持在头显正前方并面向用户
    /// </summary>
    void UpdateMenuTransform()
    {
        if (!headTransform) return;
        Vector3 forward = headTransform.forward;
        forward.y = 0f;
        forward.Normalize();

        transform.position =
            headTransform.position +
            forward * distanceFromHead +
            Vector3.up * verticalOffset;

        // 面向头部
        transform.LookAt(headTransform);
    }


    /// <summary>
    /// 根据 UIHandler 数量进行上下排列
    /// </summary>
    void LayoutUIHandlers()
    {
        int count = uiHandlers.Count;
        if (count == 0) return;

        float totalHeight = (count - 1) * itemSpacing;
        float startY = totalHeight * 0.5f;

        for (int i = 0; i < count; i++)
        {
            UIHandler handler = uiHandlers[i];
            handler.show = false;
            if (handler == null || handler.rootTransform == null) continue;

            float yOffset = startY - i * itemSpacing;

            handler.rootTransform.position =
                transform.position +
                transform.up * yOffset;

            handler.rootTransform.rotation = transform.rotation;
        }
    }

    public void ControlArUco()
    {
        if (switchArUco==null)return;
        switchArUco.isOn = !switchArUco.isOn;
    }
    
    public void ControlArUco(bool isOn)
    {
        if (switchArUco==null)return;
        switchArUco.isOn = isOn;
    }
}
