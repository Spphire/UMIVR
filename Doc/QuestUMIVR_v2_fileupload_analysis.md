# UMIVR 项目分析与深度录制迭代记录（主场景：QuestUMIVR_v2_fileupload）

本文档作为持续维护的项目“记忆”，用于减少会话上下文消耗。  
最后更新：2026-03-18

## 1. 项目关键上下文

- 项目路径：`C:/Users/yibo/Desktop/UMIVR`
- 主场景：`Assets/Scenes/QuestUMIVR_v2_fileupload.unity`
- 当前目标：在尽量小改动前提下，为录制流程增加 Depth 数据采集能力，并验证性能上限（重点 30Hz）

## 2. 当前录制主链路（稳定部分）

主链路（已在用）：

1. `UnityHttpReceiver` 接收外部录制命令。
2. `RecorderControllerPure` 触发录制开始/结束。
3. `FramePusher` 采集双目图像、位姿与时间戳。
4. `Mp4RecorderPure` 编码输出 `left_recording.mp4` / `right_recording.mp4`。
5. 同目录输出元数据（如 `frame_data.bson`、`metadata.json`）。

## 3. Depth 方向方案结论（当前共识）

候选方案：

1. Depth MP4（可复用视频链路，但精度损失较大）
2. Raw Depth 回读到 CPU（二进制保存，精度更好）
3. 直接记录点云（改动和复杂度都较大）

当前优先路径：

1. 先做 GPU 侧降采样（ComputeShader）
2. 再做 CPU 回读压测（AsyncGPUReadback）
3. 性能通过后，再决定存储格式（Raw Depth / 点云）

## 4. 已落地脚本与文件

已新增：

1. `Assets/Scripts/Depth/DepthCpuProbe.cs`
2. `Assets/Scripts/Depth/DepthDownsample.compute`

### 4.1 DepthCpuProbe 功能

- 从全局纹理 `_EnvironmentDepthTexture` 获取深度源。
- 按目标频率调度 `AsyncGPUReadback`。
- 支持“Compute 降采样 -> CPU 回读”路径。
- 输出压测日志：`scheduled/completed/skipInFlight/missTex/avgLatency/maxLatency`。
- 对外提供 `TryGetLatestDepthFrame(out DepthCpuFrame frame)` 获取最新 CPU 字节数据。

### 4.2 Compute 降采样实现要点

- `DepthDownsample.compute` 使用内核 `KMain`。
- 输入：`_InputTex`（原始深度纹理经 Blit 到 compute 输入 RT）。
- 输出：`_OutputTex`（降采样后深度，单通道）。
- 参数：`_InputWidth/_InputHeight/_OutputWidth/_OutputHeight/_Factor`。
- 算法：按 `factor x factor` 小块做有效深度均值（过滤非正值/异常值）。

### 4.3 兼容性保护（已加）

- Compute 输出格式仅选择“支持随机写”的单通道格式（优先 `RFloat`，其次 `RHalf`）。
- Compute 不可用时自动回退到直接回读路径（不会阻断整体采样）。
- 修复了 compute 后进入 staging 时尺寸错误问题：staging 现在按当前回读纹理尺寸创建，不会放大回原分辨率。

## 5. 压测使用说明（Quest 端）

在 `QuestUMIVR_v2_fileupload` 场景中：

1. 给任意常驻对象挂载 `DepthCpuProbe`。
2. Inspector 里把 `DepthDownsample.compute` 拖到 `Depth Downsample Compute` 字段。
3. 先测基线（不走 compute）：
   - `useComputeDownsample = false`
   - `targetHz = 30`
   - 记录 20 秒 summary 日志。
4. 再测 compute x2：
   - `useComputeDownsample = true`
   - `downsampleFactor = 2`
   - `skipStagingAfterCompute = true`
   - 记录 20 秒 summary 日志。
5. 再测 compute x4（可选）：
   - `downsampleFactor = 4`
   - 记录 20 秒 summary 日志。

重点看 summary：

- `completedHz`：实际完成频率（目标尽量接近 30）
- `avgLatency/maxLatency`：单帧回读延迟
- `lastFrame=WxH ... bytes=...`：每帧数据体积是否明显下降

## 6. 当前已知测试结论（你提供的上一轮日志）

在未降采样的情况下（`1024x1024 RFloat`，约 4MB/帧）：

- 20 秒汇总约 `completedHz ≈ 14.10`
- `avgLatency ≈ 39.45ms`，`maxLatency ≈ 77.64ms`

结论：

- 当前全分辨率回读无法达到 30Hz。
- 优先用 compute 做降采样是正确方向。

## 7. 下一步最小改动建议

1. 先跑完 `x2/x4` 三组对比日志（基线、x2、x4）。
2. 若 `x2` 已显著提升（例如 `completedHz` 接近 24~30），优先保留 x2 并进入存储模块。
3. 存储模块首版建议先写 `depth_frames.bin + depth_index.json`（最小依赖、可快速验证）。

## 8. 参考资料

- 你提供的参考文档：  
  `C:/Users/yibo/Downloads/在 Unity 中使用 Meta Quest Depth API 和相机内参构建点云教程.md`

## 9. 2026-03-18 第二轮实测结论（GPU 降采样阶段）

你提供的 summary（20s）：

- `scheduledHz=13.14`
- `completedHz=13.09`
- `avgLatency=41.80ms`
- `maxLatency=117.50ms`
- `lastFrame=1024x1024 RFloat bytes=4194304`

对比上一轮（约 `completedHz=14.10`, `avgLatency=39.45ms`）：

- 本轮没有提升，且略有下降。

关键判断依据：

1. `lastFrame` 仍是 `1024x1024`（若 compute x2 生效应接近 `512x512`）。
2. `bytes` 仍是 `4194304`（4MB，仍是全分辨率 `RFloat` 体积）。

因此这轮大概率未真正走到“compute 输出 -> readback”的有效路径（或中途回退到了原路径）。

为便于下一轮直接判断，本轮已在 `DepthCpuProbe` 增加日志字段：

- `computeUsed`：成功使用 compute 降采样的帧数
- `computeFallback`：需要回退到非 compute 路径的次数

下一轮请重点看 summary 是否出现：

- `computeUsed > 0`
- `lastFrame` 降到 `512x512`（x2）或 `256x256`（x4）
- `bytes` 明显下降（x2 下 RFloat 约 1MB）

## 10. 2026-03-18 第三轮实测结论（Compute 已确认生效）

你提供的 summary（20s）：

- `computeUsed=258`
- `computeFallback=0`
- `completedHz=12.84`
- `avgLatency=40.65ms`
- `lastFrame=512x512 RFloat bytes=1048576`

结论：

1. Compute 路径确实生效（不是参数没设置）。
2. 数据量已从 4MB/帧 降到 1MB/帧（x2，RFloat）。
3. 但吞吐仍未提升（`completedHz` 仍在约 13Hz），说明当前主要瓶颈不只在“回读字节量”，还包括 readback pipeline 本身延迟/串行调度方式。

下一步最小改动优先级：

1. 先试 `preferredComputeOutputFormat = RHalf`（再降一半到约 0.5MB/帧）。
2. 再试 `downsampleFactor = 4`（`256x256`）。
3. 若仍卡在 10~15Hz，需要把 probe 从“单 in-flight 请求”改为“2~3 个并行 in-flight 请求”（小改动即可验证上限）。

## 11. 2026-03-18 代码更新：并行 Readback（最小改动）

已在 `DepthCpuProbe` 增加：

- `maxInFlight`（默认 2）

行为变化：

1. 旧版：单 in-flight（上一帧未回调就跳过新请求）。
2. 新版：允许最多 `maxInFlight` 个回读请求并行在飞。
3. `skipInFlight` 现在表示“到达并发上限而跳过”。

日志新增：

- 周期日志：`inFlight=current/maxInFlight`、`peakInFlight`
- summary：`peakInFlight=peak/maxInFlight`

建议压测矩阵（保持其它参数不变）：

1. `maxInFlight=1`（对照组）
2. `maxInFlight=2`（默认）
3. `maxInFlight=3`（上限探索）

每组跑 20s，重点比较：

- `completedHz`
- `avgLatency/maxLatency`
- `skipInFlight`

## 12. 2026-03-18 第四轮实测结论（maxInFlight=2）

你提供的 summary（20s）：

- `computeUsed=265`
- `computeFallback=0`
- `peakInFlight=2/2`
- `skipInFlight=0`
- `completedHz=13.19`
- `avgLatency=43.03ms`
- `maxLatency=126.40ms`
- `lastFrame=512x512 RFloat bytes=1048576`

结论：

1. 并行回读路径已生效（确实有 2 个 in-flight）。
2. 吞吐仅小幅提升（相对上一轮约 `12.84 -> 13.19`，+0.35Hz）。
3. 延迟未改善（平均/峰值都有波动，峰值更高）。

推断：

- 当前主要瓶颈已不在“单 in-flight 限制”，更可能是整体管线（含设备侧深度更新、GPU/驱动回读时序、主循环帧率）。

补充观测（x4 结果）：

- 你把 `downsampleFactor` 改为 4 后，`lastFrame=256x256`、`bytes=262144`，这在 `RFloat` 下是正确值（不是异常）。
- 若改成 `RHalf` 且生效，`256x256` 时 `bytes` 预期约 `131072`。

新增日志字段（用于下一轮定位）：

- `approxFps`：日志窗口内主循环近似帧率
- `scheduledPerSec` / `completedPerSec`：日志窗口内请求调度与完成速率

## 13. 2026-03-18 调度修复（针对 30Hz 被降档问题）

问题点：

- 原调度使用 `nextTick = now + period`。
- 在帧率不明显高于目标频率时，会出现“每 2 帧调度 1 次”的降档现象（常见表现 30 目标掉到 20/15 左右）。

修复：

- 改为固定时间栅格：`nextTick += period`。
- 当发生滞后时，按缺失 tick 数前推 `nextTick`（避免长期漂移）。

预期：

1. `scheduledPerSec` 应更接近 `targetHz`（受 `maxInFlight`、GPU 回读延迟和实际帧率共同约束）。
2. 在相同硬件负载下，`completedHz` 通常会比修复前更稳定。

## 14. 2026-03-18 第五轮实测结论（x4 + RHalf + maxInFlight=2）

你提供的日志特征：

1. 周期日志中 `scheduledPerSec/completedPerSec` 多数在 `~29-31/s`。
2. `skipInFlight=0`，`peakInFlight=2/2`，说明并行队列未卡满。
3. `lastFrame=256x256 RHalf bytes=131072`，参数完全生效。

旧 summary（修复前口径）仍显示约 `19.44Hz` 的原因：

- summary 之前按 wall time 统计；
- 日志中出现 `OVRManager OnApplicationFocus` 事件，存在非活跃时间掺入，导致 wall 口径被拉低；
- 这与周期日志窗口内接近 30/s 的表现并不矛盾。

已修复：

- summary 现在同时输出 `active` 与 `wall` 两套频率，优先看 `active` 口径做性能判断。

进一步修复（防焦点切换污染 active 口径）：

- 发现 `OnApplicationFocus` 后首帧可能出现超大 `unscaledDeltaTime`，会错误抬高 `activeElapsed`。
- 现已将 active 计时改为“按 wall delta 累加并单帧裁剪”（`maxCountedDeltaSeconds`，默认 `0.1s`）。
- summary 新增 `maxCountedDelta=...` 字段，便于确认该保护配置。

## 15. 2026-03-18 第六轮实测结论（最终验证通过）

参数：

- `downsampleFactor=4`
- `preferredComputeOutputFormat=RHalf`
- `maxInFlight=2`
- 调度修复 + activeElapsed 裁剪已启用

summary：

- `wallElapsed=26.61s`，`activeElapsed=20.00s`
- `scheduledHz(active/wall)=29.45/22.14`
- `completedHz(active/wall)=29.40/22.10`
- `avgLatency=43.21ms`，`maxLatency=91.31ms`
- `lastFrame=256x256 RHalf bytes=131072`

结论：

1. 按 active 口径，Depth CPU 回读已稳定接近 30Hz。
2. wall 口径偏低是因非活跃时段（焦点切换/停顿）引入，不是管线吞吐退化。
3. 该参数组可作为当前“最小改动可落地配置”进入录制模块开发。

## 16. 2026-03-18 对比补充（downsample=2, RHalf）

参数：

- `downsampleFactor=2`
- `preferredComputeOutputFormat=RHalf`
- `maxInFlight=2`

summary：

- `scheduledHz(active)=29.43`
- `completedHz(active)=29.38`
- `avgLatency=42.61ms`
- `maxLatency=87.69ms`
- `lastFrame=512x512 RHalf bytes=524288`

与 `x4 + RHalf` 对比（上一轮）：

1. 吞吐几乎相同（都接近 29.4Hz）。
2. x2 延迟略优（平均/峰值都略低）。
3. x2 空间分辨率更高（512x512），但单帧数据量是 x4 的 4 倍。

决策建议：

1. 若优先保留更多深度细节，选 `x2 + RHalf`。
2. 若优先减小后续存储/传输压力，选 `x4 + RHalf`。

## 17. 2026-03-18 真机长时间日志（非模拟器）

真机日志片段（运行中）显示：

1. `scheduledPerSec / completedPerSec` 长时间稳定在 `~29.5 - 30.7/s`。
2. `computeUsed` 持续增长，`computeFallback=0`，说明 compute 路径稳定。
3. `inFlight` 常见 `1/2`、`peakInFlight=2`，属于正常流水线状态。
4. `skipInFlight` 从 6 增到 7（相对 `scheduled~1700+`，比例很低）。
5. `avgLatency` 约 `40.7ms`，较此前有改善；`maxLatency=230ms` 为少量长尾尖峰。
6. `missTex=2` 仍为启动阶段固定值，无持续增长。

补充观察：

1. 日志中有 `OVRManager HMDUnmounted` 事件。
2. 同时存在 `QuestStreamSender Connection refused` 周期重连告警。

结论：

1. 真机下 Depth 采样链路已满足“接近 30Hz 持续工作”目标。
2. 当前需要关注的是偶发长尾（`maxLatency`），而非平均吞吐。

建议（落地前）：

1. 正式录制时尽量避免头显摘下/焦点切换。
2. 压测阶段可临时关闭 `QuestStreamSender` 重连，减少干扰噪声。
3. 深度文件索引按真实时间戳记录，不假设固定帧间隔。
