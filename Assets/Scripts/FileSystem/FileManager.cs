using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using Recorder;
using Recorder.v2;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.Video;

[Serializable]
public class UploadedPersistence
{
    public List<string> recorded;

    public UploadedPersistence()
    {
        recorded = new List<string>();
    }
}

public class FileManager : MonoBehaviour
{
    [Header("UI")]
    [SerializeField] private Transform contentRoot;   // ScrollView Content
    [SerializeField] private GameObject filePrefab;   // 每个文件的Prefab

    private string recordPath;
    
    public List<FileView> FileViews;
    private List<FileView> FileViewsSelected;
    private List<FileView> tempFileSelected;
    public TextMeshProUGUI cancelButton;

    private List<GameObject> fileListToDelete;

    private string uploadedPersistencePath;
    public UploadedPersistence uploadedPersistence;

    [Header("Generate Video Frame")] 
    public VideoPlayer sharedPlayer;
    public RenderTexture renderTexture;
    public List<Texture2D> displayFrames;

    private void Awake()
    {
        FileViews ??= new List<FileView>();
        FileViewsSelected ??= new List<FileView>();
        tempFileSelected ??= new List<FileView>();
        displayFrames ??= new List<Texture2D>();
        
        fileListToDelete = new List<GameObject>();
        recordPath = Path.Combine(Application.persistentDataPath, "record");
        ULog.Log($"recordPath: {recordPath}");
        
        uploadedPersistencePath = Path.Combine(Application.persistentDataPath, "UploadedPersistence.json");
        if (File.Exists(uploadedPersistencePath))
        {
            uploadedPersistence = JsonUtility.FromJson<UploadedPersistence>(File.ReadAllText(uploadedPersistencePath));
        }
        else
        {
            uploadedPersistence = new UploadedPersistence();
        }
    }

    private void Start()
    {
        sharedPlayer.targetTexture = renderTexture;
    }

    public void Update()
    {
        cancelButton.text = $"Cancel({FileViewsSelected.Count})";
        if (FramePusher.need2refresh)
        {
            FramePusher.need2refresh = false;
# if !UNITY_EDITOR
            LoadFiles();
# endif
        }
    }

    public void OnChildToggleClick(FileView fileViewChild, bool isOn)
    {
        if (isOn)
        {
            if (!FileViewsSelected.Contains(fileViewChild))
            {
                FileViewsSelected.Add(fileViewChild);
            }
        }
        else
        {
            if (FileViewsSelected.Contains(fileViewChild))
            {
                FileViewsSelected.Remove(fileViewChild);
            }
        }
    }

    public void LoadFiles()
    {
        if (!Directory.Exists(recordPath))
        {
            ULog.LogWarning("Record path not found: " + recordPath);
            return;
        }

        // 获取所有子文件夹
        string[] directories = Directory.GetDirectories(recordPath);

        int fileListIndex = 0;
        for (int directories_i = directories.Length - 1; directories_i >= 0; directories_i--)
        {
            string dir = directories[directories_i];

            if (IsValidRecordFolder(dir))
            {
                if (fileListIndex < FileViews.Count && Path.GetFileName(dir) == FileViews[fileListIndex].gameObject.name)
                {
                    
                }
                else
                {
                    FileView fv = CreateFileItem(dir);
                    fv.transform.SetSiblingIndex(fileListIndex);
                    FileViews.Insert(fileListIndex, fv);
                }
                fileListIndex++;
               
            }
        }
        
        if (fileListIndex < FileViews.Count)
        {
            fileListToDelete.Clear();
            for (int i = fileListIndex; i < FileViews.Count; i++)
            {
                fileListToDelete.Add(FileViews[i].gameObject);
            }
            FileViews.RemoveRange(fileListIndex, FileViews.Count - fileListIndex);
            foreach (GameObject fg in fileListToDelete)
            {
                DestroyImmediate(fg);
            }
        }

        StartCoroutine(GenerateThumbnails());
    }

    /// <summary>
    /// 校验是否为合格的文件夹
    /// </summary>
    private bool IsValidRecordFolder(string folderPath)
    {
        
        string bsonPath = Path.Combine(folderPath, QuestMp4Recorder.dataFileName);
        string jsonPath = Path.Combine(folderPath, QuestMp4Recorder.metaFileName);
        
        return File.Exists(bsonPath) && File.Exists(jsonPath);
        
        string mp4Path = Path.Combine(folderPath, QuestMp4Recorder.videoFileName);
        if (File.Exists(mp4Path))
        {
            return File.Exists(bsonPath) && File.Exists(jsonPath);
        }
        else
        {
            mp4Path = Path.Combine(folderPath, FramePusher.leftVideoFileName);
            return File.Exists(mp4Path) && File.Exists(bsonPath) && File.Exists(jsonPath);
        }

        
    }

    /// <summary>
    /// 创建UI条目
    /// </summary>
    private FileView CreateFileItem(string folderPath)
    {
        GameObject item = Instantiate(filePrefab, contentRoot);

        // 文件夹名称
        string folderName = Path.GetFileName(folderPath);
        item.name = folderName;
        
        // metadata.json 路径
        string metadataPath = Path.Combine(folderPath, "metadata.json");
        if (File.Exists(metadataPath))
        {
            try
            {
                string json = File.ReadAllText(metadataPath);
                MetaDatav0_9 metadata = JsonUtility.FromJson<MetaDatav0_9>(json);
                item.GetComponent<FileView>().nameDisplay.text = $"TaskTag:{metadata.task_tag}" +"\n"+ folderName.Split("record_")[1];
                item.GetComponent<FileView>().videoSizeDisplay.text = $"{(float)metadata.duration:F1}s";
            }
            catch (System.Exception e)
            {
                ULog.LogError($"读取 metadata.json 失败: {metadataPath}\n{e}");
            }
        }
        else
        {
            ULog.LogWarning($"未找到 metadata.json: {metadataPath}");
        }

        if (uploadedPersistence.recorded.Contains(item.name))
        {
            item.GetComponent<FileView>().uploaded = true;
            item.GetComponent<FileView>().uploadedText.text = "uploaded";
        }
        else
        {
            item.GetComponent<FileView>().uploaded = false;
            item.GetComponent<FileView>().uploadedText.text = "";
        }
        
        return item.GetComponent<FileView>();
    }
    
    public void ClearChildren(Transform parent)
    {
        for (int i = parent.childCount - 1; i >= 0; i--)
        {
            Destroy(parent.GetChild(i).gameObject);
        }
        FileViews.Clear();
    }

    public void UploadSelectedFile()
    {
        foreach (FileView fileView in FileViewsSelected)
        {
            fileView.Upload();
        }
    }
    
    public void DeleteSelectedFile()
    {
        foreach (FileView fileView in FileViewsSelected)
        {
            if (uploadedPersistence.recorded.Contains(fileView.gameObject.name))
            {
                uploadedPersistence.recorded.Remove(fileView.gameObject.name);
            }
            fileView.OnDelete();
        }
        SaveUploadedPersistence();
        FileViewsSelected.Clear();
        LoadFiles();
    }

    public void PlaySelectedFile()
    {
        FileViewsSelected[0].PlayVideo();
    }

    public void ResetSelectedFile()
    {
        foreach (FileView fileView in FileViewsSelected)
        {
            fileView.uploaded = false;
            fileView.uploadedText.text = "";
            if (uploadedPersistence.recorded.Contains(fileView.gameObject.name))
            {
                uploadedPersistence.recorded.Remove(fileView.gameObject.name);
            }
        }
        SaveUploadedPersistence();
    }

    public void SaveUploadedPersistence()
    {
        // save to path uploadedPersistence
        File.WriteAllText(uploadedPersistencePath, JsonUtility.ToJson(uploadedPersistence));
    }
    
    public void CancelSelectedFile()
    {
         
        tempFileSelected.Clear();
        foreach (FileView fileView in FileViewsSelected)
        {
            tempFileSelected.Add(fileView);
        }

        foreach (FileView fileView in tempFileSelected)
        {
            fileView.toggle.isOn = false;
        }
        
    }
    
    private IEnumerator GenerateThumbnails()
    {
        sharedPlayer.sendFrameReadyEvents = true;

        foreach (var item in FileViews)
        {
            if (item.videoImage.texture != null)
                continue;

            // 创建 Texture2D（CPU 端）
            Texture2D tempTex = new Texture2D(
                renderTexture.height,
                renderTexture.height,
                TextureFormat.RGBA32,
                false
            );

            bool frameReady = false;

            // frameReady 只是“信号”，不要在这里截图
            void OnFrameReady(VideoPlayer vp, long frameIdx)
            {
                frameReady = true;
            }

            sharedPlayer.frameReady += OnFrameReady;

            // 设置视频
            sharedPlayer.url = item.GetVideoPath();
            sharedPlayer.Prepare();

            // 等 Prepare（只代表解码器 ready）
            while (!sharedPlayer.isPrepared)
                yield return null;

            // ⭐ 取第一帧：不要 seek
            sharedPlayer.time = 0;
            sharedPlayer.Play();

            // 等到解码器真的吐出一帧
            while (!frameReady)
                yield return null;

            // ⭐ 再等一帧，确保 RenderTexture 已被 GPU 写入
            yield return new WaitForEndOfFrame();

            // 现在截图，一定不是白的
            RenderTexture.active = renderTexture;
            tempTex.ReadPixels(
                new Rect(
                    (renderTexture.width - renderTexture.height) / 2,
                    0,
                    renderTexture.height,
                    renderTexture.height
                ),
                0,
                0
            );
            tempTex.Apply();
            RenderTexture.active = null;

            sharedPlayer.Pause();
            sharedPlayer.frameReady -= OnFrameReady;

            // 直接给 RawImage
            item.videoImage.texture = tempTex;

            // 给 VideoPlayer 一点喘息时间，避免下一个被打断
            yield return null;
        }

        ULog.Log("All thumbnails generated!");
    }
}