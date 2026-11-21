using System;
using UnityEngine;
using UnityEngine.UI;
using TMPro;
using System.Collections.Generic;
using System.IO;
using Unity.VisualScripting;

[Serializable]
public class IPListJson
{
    public int index;
    public List<string> ipList;

    public IPListJson(int index,List<string> ipList)
    {
        this.index = index;
        this.ipList = ipList;
    }
}

/// <summary>
/// IP 列表管理器（Dropdown + 本地持久化 + 默认 IP）
/// </summary>
public class IPListManager : MonoBehaviour
{
    [Header("Persistence")] 
    public string saveFileName="iplist.json";
    
    [Header("UI References")]
    public TMP_InputField ipInputField;   // 输入新 IP
    public Button addIPButton;            // 添加按钮
    public Button deleteSelectedButton;   // 删除选中按钮
    public TMP_Dropdown ipDropdown;       // Dropdown 显示 IP

    [Header("IP Data")] public List<string> ipListPrefab;
    private List<string> ipList = new();
    private int currentIPIndex = 0;

    private string saveFilePath;

    void Awake()
    {
        // 设置保存文件路径
        saveFilePath = Path.Combine(Application.persistentDataPath, saveFileName);

        if (File.Exists(saveFilePath))
        {
            LoadIPList();
        }
        else
        {
            // 默认 IP 列表（第一次运行使用）
            ipList = new List<string>(ipListPrefab);

            SaveIPList(); // 第一次运行就保存默认 IP
        }
    }

    void Start()
    {
        addIPButton.onClick.AddListener(OnAddIP);
        deleteSelectedButton.onClick.AddListener(OnDeleteSelected);
        RefreshDropdown();
    }

    // 添加新 IP
    private void OnAddIP()
    {
        string newIP = ipInputField.text.Trim();
        if (string.IsNullOrEmpty(newIP)) return;

        if (!ipList.Contains(newIP))
        {
            ipList.Add(newIP);
            currentIPIndex = Mathf.Clamp(ipDropdown.value,0,ipList.Count - 1);
            RefreshDropdown();
            ipInputField.text = "";
            SaveIPList(); // 保存到本地
        }
    }

    // 删除当前选中的 IP
    private void OnDeleteSelected()
    {
        if (ipDropdown.options.Count < 2) return;

        int index = ipDropdown.value;
        string selectedIP = ipDropdown.options[index].text;

        ipList.Remove(selectedIP);
        currentIPIndex = Mathf.Clamp(ipDropdown.value,0,ipList.Count - 1);
        RefreshDropdown();
        SaveIPList(); // 保存到本地
    }

    // 刷新 Dropdown 显示
    private void RefreshDropdown()
    {
        ipDropdown.ClearOptions();
        ipDropdown.AddOptions(ipList);
        ipDropdown.value = currentIPIndex;
    }

    // 获取当前选中的 IP
    public string GetSelectedIP()
    {
        if (ipDropdown.options.Count == 0) return null;
        return ipDropdown.options[ipDropdown.value].text;
    }

    // 检查某个 IP 是否在列表中
    public bool ContainsIP(string ip)
    {
        return ipList.Contains(ip);
    }

    // -------------------------------
    // 本地持久化
    // -------------------------------

    public void SaveIPList()
    {
        try
        {
            string json = JsonUtility.ToJson(new IPListJson(ipDropdown.value, ipList));
            File.WriteAllText(saveFilePath, json);
            Debug.Log("IP列表已保存到: " + saveFilePath);
        }
        catch (System.Exception e)
        {
            Debug.LogError("保存IP列表失败: " + e.Message);
        }
    }

    private void LoadIPList()
    {
        try
        {
            IPListJson ipListClass = JsonUtility.FromJson<IPListJson>(File.ReadAllText(saveFilePath));
            ipList = ipListClass.ipList;
            currentIPIndex = ipListClass.index;
        }
        catch (System.Exception e)
        {
            Debug.LogError("加载IP列表失败: " + e.Message);
            ipList = new List<string>();
            currentIPIndex = 0;
        }
    }
}
