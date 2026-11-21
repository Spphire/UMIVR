using System;
using UnityEngine;
using UnityEngine.InputSystem;
using System.Collections;
using System.Collections.Generic;
using Newtonsoft.Json;
using TMPro;
using Unity.VisualScripting;

public class Teleoperator : MonoBehaviour
{
    [Header("Target IP")]
    public TMP_InputField targetIP;

    [Header("Controllers")]
    public Transform leftHandTransform;
    public Transform rightHandTransform;
    public Transform headTransform;

    [Header("Left Hand Input Actions")]
    public InputAction leftTrackAction;

    public InputAction leftTriggerAction;

    [Header("Right Hand Input Actions")]
    public InputAction rightTrackAction;
    public InputAction rightTriggerAction;

    private void OnEnable()
    {
        leftTrackAction.Enable();
        rightTrackAction.Enable();
        leftTriggerAction.Enable();
        rightTriggerAction.Enable();
    }

    private void OnDisable()
    {
        leftTrackAction.Disable();
        rightTrackAction.Disable();
        leftTriggerAction.Disable();
        rightTriggerAction.Disable();
    }

    void Update()
    {
        StartCoroutine(SendTeleopData());
    }

    IEnumerator SendTeleopData()
    {
        // 构建消息
        var unityMes = new UnityMes
        {
            timestamp = Time.time,
            leftHand = GetHandData(leftHandTransform, true),
            rightHand = GetHandData(rightHandTransform, false),
            headPos = GetPositionList(headTransform),
            headQuat = GetQuaternionList(headTransform)
        };

        string json = JsonConvert.SerializeObject(unityMes);

        // POST
        string ip = targetIP?.text;
        if (!string.IsNullOrEmpty(ip))
        {
            string url = $"http://{ip}:8082/unity";
            using (UnityEngine.Networking.UnityWebRequest request = UnityEngine.Networking.UnityWebRequest.PostWwwForm(url, ""))
            {
                byte[] bodyRaw = System.Text.Encoding.UTF8.GetBytes(json);
                request.uploadHandler = new UnityEngine.Networking.UploadHandlerRaw(bodyRaw);
                request.SetRequestHeader("Content-Type", "application/json");
                request.timeout = 1; // 1秒超时
                yield return request.SendWebRequest();

                if (request.result != UnityEngine.Networking.UnityWebRequest.Result.Success)
                {
                    //Debug.LogWarning($"Teleop POST失败: {request.error}");
                }
            }
        }
    }

    HandMes GetHandData(Transform handTransform, bool isLeft)
    {
        var handMes = new HandMes
        {
            wristPos = GetPositionList(handTransform),
            wristQuat = GetQuaternionList(handTransform),
            triggerState = isLeft ? leftTriggerAction?.ReadValue<float>() ?? 0f : rightTriggerAction?.ReadValue<float>() ?? 0f,
            buttonState = new List<bool> { false, false, false, false, false }
        };

        if (isLeft)
        {
            handMes.buttonState[4] = leftTrackAction?.IsPressed() ?? false;
        }
        else
        {
            handMes.buttonState[4] = rightTrackAction?.IsPressed() ?? false;
        }

        return handMes;
    }

    List<float> GetPositionList(Transform t)
    {
        if (t == null) return new List<float> { 0, 0, 0 };
        return new List<float> { t.position.x, t.position.y, t.position.z };
    }

    List<float> GetQuaternionList(Transform t)
    {
        if (t == null) return new List<float> { 1, 0, 0, 0 };
        return new List<float> { t.rotation.w, t.rotation.x, t.rotation.y, t.rotation.z };
    }

    [System.Serializable]
    public class UnityMes
    {
        public float timestamp;
        public HandMes leftHand;
        public HandMes rightHand;
        public List<float> headPos;
        public List<float> headQuat;
    }

    [System.Serializable]
    public class HandMes
    {
        public List<float> wristPos;
        public List<float> wristQuat;
        public float triggerState;
        public List<bool> buttonState;
        public int? cmd;
    }
}
