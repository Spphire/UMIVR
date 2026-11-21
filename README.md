# UMIVR

基于 Unity 和 Meta Quest 的双目图像、位姿与机器人遥操作数据采集项目。

## 环境

- Unity **6000.2.8f1**，安装 Android Build Support、SDK/NDK 和 OpenJDK。
- Meta XR SDK / Interaction SDK / MR Utility Kit **81.0.0**，URP **17.2.0**，OpenXR **1.16.0**。
- Android ARM64；当前项目最低 / 目标 Android API 均为 **32**。
- 使用支持所需 Passthrough Camera / Depth API 的 Quest 设备；运行时需授予相机等权限。
- OpenCV for Unity 已随项目保留；相关第三方组件各自适用其原有许可。

## 项目结构与数据链路

| 路径 | 用途 |
| --- | --- |
| `Assets/Scenes/QuestUMIVR_v2_fileupload.unity` | 文件上传与录制主场景；专用构建脚本入口 |
| `Assets/Scenes/QuestUMIVR_v2.unity`、`QuestUMIVR_v3.unity` | 其他录制场景；默认 Build Settings 同时启用 |
| `Assets/Scripts/HTTP` | HTTP 指令、时间同步、元数据、打包及校验 |
| `Assets/Scripts/Recorder/v2` | FramePusher、RecorderControllerPure、Mp4RecorderPure 双目采集与编码控制 |
| `Assets/Plugins/Android/com/Noematrix` | Android 原生视频编码、MP4 录制 |
| `Assets/Scripts/Depth` | 环境深度 GPU 降采样与异步 CPU 回读实验 |
| `Assets/Scripts/DAgger` | 遥操作、标定、推理相关模块 |
| `Doc` | 原有项目笔记、深度采集设计与压测记录 |

主录制链路：`UnityHttpReceiver → RecorderControllerPure → FramePusher → Mp4RecorderPure`。
输出包含左右目 MP4、帧位姿/时间戳 BSON、JSON 元数据，具体内容随场景配置而定。
HTTP 接收器默认端口为 `4050`，代码入口包含 `POST /unity` 和 `POST /record`。
运行前请检查 Inspector 中的接收/上传地址、目标 IP、相机和录制器引用，以及设备网络连接。

## 打开与构建

1. 用 Unity Hub 将仓库目录作为项目打开，使用 6000.2.8f1，等待 Package Manager 还原依赖。
2. 切换 Android 平台，检查 ARM64、XR 和相机权限配置。
3. 打开主场景并检查设备/服务器参数。默认 Build Settings 包含三个场景，而下述专用脚本只构建文件上传主场景。
4. 在 Player Settings > Publishing Settings 配置自己的签名密钥；仓库不包含原项目的 `user.keystore` 或密码。现有配置中的本地密钥路径需要自行替换。
5. 使用现有命令行构建方法：

```powershell
& '<Unity安装目录>/6000.2.8f1/Editor/Unity.exe' `
  -batchmode -quit -projectPath '<仓库目录>' -buildTarget Android `
  -executeMethod QuestBuildTools.BuildQuestUMIVRFileUploadApk `
  -logFile '<构建日志路径>'
```

脚本输出：`Build/questumivr_fileupload/QuestUMIVR_v2_fileupload.apk`。
构建脚本对应文件上传场景；不应把它与下方既有 dagger APK 视为同一构建配置。

## APK Release

GitHub Releases 提供项目中已有的最新 `Build/dagger/UMIVR.apk`（版本 `0.1.0`，包名 `com.Noematrix.UMIVR`）。
该文件原修改时间为 **2026-06-22**，本次整理未重新编译；历史 APK 与当前源码的精确对应关系未验证。
发布前已检查 APK 元数据、ZIP CRC 完整性及 Android v2 签名；尚未进行 Quest 真机安装与功能测试。
Release 同时附带 SHA-256 校验文件和构建来源信息。

```powershell
adb install -r UMIVR.apk
```

若设备上已有安装包使用不同签名，需先备份数据并处理签名冲突。

## 版本控制范围

保留 `Assets/`（含 `.meta`）、`Packages/`、`ProjectSettings/`、`Doc/` 和项目说明。
排除 Unity 缓存、临时文件、IDE 文件、构建输出、压测日志、恢复副本及签名密钥。
本仓库面向 Quest/Android，OpenCV 原生库保留 Android 与 Windows Editor；其他平台库被忽略，切换到那些平台时需从合法安装包恢复。
保留现有示例和资源，以维护场景、脚本和 `.meta` GUID 引用；未对工程运行逻辑进行重构。
仓库未额外授予统一开源许可证，第三方版权及许可声明保留原样。
