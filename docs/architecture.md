# 架构说明

## 1. 分层

```
MyPcdEditor.App (Avalonia 12, win-x64)
├── Controls/PointCloudView       : GlControlBase —— 3D 视图
├── Controls/MapView              : Skia 位图 —— 2D 视图
├── Rendering/GlControlBase       : ANGLE ↔ Silk.NET 桥、帧泵、输入桥接
├── Rendering/GlInfo              : 上下文能力探测
├── ViewModels/                   : 主窗口 VM、状态栏、交互状态
└── Views/MainWindow
        │
        ├── MyPcdEditor.Rendering.Gles3
        │   ├── Shader / GlObjectStats
        │   ├── GpuPointBuffer       : 分片上传、VBO 生命周期
        │   ├── OrbitCamera          : System.Numerics 矩阵
        │   ├── PointCloudRenderer   : program + 绘制（索引抽样降载、按块视锥剔除）
        │   └── GroundTextureRenderer: 单张栅格纹理 + 水平四边形（先画、开深度写入）
        │
        └── MyPcdEditor.Core （无 GL / 无 Avalonia 依赖）
            ├── Pcd/{PcdHeader,PcdReader,PcdWriter}
            ├── Grid/{PgmReader,PgmWriter,MapYaml,OccupancyMap}
            ├── Grid/{OccupancyPalette,GroundTexture} : 语义配色 / 降采样成 GL 纹理数据
            ├── Grid/GridMorphology   : 去毛刺 / 补洞 / 去孤岛（输出新 PgmImage，抹除填未知）
            ├── Grid/GridEraser       : 栅格橡皮擦（折线胶囊刷成未知 / 空闲，输出新 PgmImage）
            ├── Geometry/Frustum      : 从 view-projection 提 6 平面 + P 顶点 AABB 测试
            ├── PointCloud/ChunkedBounds : 按连续点区间累加块包围盒（可后台线程整云扫描）
            ├── PointCloud/PointCloudOps : 体素降采样 / SOR 去噪 / Box 裁剪 / 屏幕框选 / 屏幕笔刷（纯函数，可取消）
            ├── PointCloud/VoxelHash  : 体素键打包 + 链式哈希表 + 质心累加
            ├── PointCloud/VoxelGridIndex : SOR 的 3×3×3→5×5×5 近邻网格（只读，可并行查询；ICP 也用它找对应点）
            ├── Merge/AlignmentTransform : 4 自由度刚体（yaw + xyz），Then/Inverse/Difference
            ├── Merge/CloudAligner       : 锚点定初值 + 粗精两级 Huber 加权 ICP（4 自由度闭式 Procrustes）
            ├── Merge/PointCloudMerger   : 多份点按各自变换拼成一份，可选体素去重
            ├── Merge/GridMerger         : 多图按 origin + 变换放置，并集范围 + 冲突优先级（占用>未知>空闲）
            ├── Merge/MapMerger          : 编排层，产出 MergeOutcome + 中文对齐报告
            ├── MapPackageWriter     : 写完整包目录（文件名 / 锚点 / toml 沿用参考包）
            │                          两阶段提交：Prepare 只写 .tmp，Commit 才让原文件改名为 .bak
            │                          再就位（替换失败整包回滚）；Write 用于导出，Replace 用于覆盖原包
            └── MapPackage           : 目录级聚合（含 projected_origin / id_map）
```

依赖方向单向：`App → Rendering.Gles3 → Core`。Core 必须能在无窗口环境下跑测试。

## 2. 渲染栈

- Avalonia 12 在 Windows 上由 **ANGLE** 提供 GL 上下文（`av_libglesv2.dll`），能力等价 **GLES 3.x**。
- 绑定方式（照 MilkAvalon `Rendering/GlControlBase.cs:212`）：

```csharp
_gl = new Silk.NET.OpenGLES.GL(new LamdaNativeContext(glInterface.GetProcAddress));
```

`LamdaNativeContext` 来自 `Silk.NET.Core.Contexts`（拼写如此）。不需要自己建 EGL surface，也不需要 `Silk.NET.OpenGLES.ANGLE.Native`。

- 需要随包携带的原生 DLL 来自 `Avalonia.Angle.Windows.Natives`（`runtimes/win-x64/native/*.dll`），因为 Silk.NET 自带的 ANGLE 包是 x86 二进制。

## 3. GL 使用约束（务必遵守）

| 约束 | 原因 |
|---|---|
| GL 资源只在 `OnOpenGlInit` / `OnOpenGlRender` / `OnOpenGlDeinit` 里创建、使用、释放 | 上下文只在 Avalonia 渲染线程 current |
| 每帧自己 `BindFramebuffer(fb)` + `Viewport(...)` | Avalonia 传入的 FBO 不是默认帧缓冲 |
| 绘制后恢复 `UseProgram(0)` / `BindVertexArray(0)` / `ActiveTexture(Texture0)` | 否则污染 Skia 的 GL 状态机，出现花屏 |
| 控件需实现 `ICustomHitTest` 返回 true | GL 区域不由 Avalonia 绘制，默认命中测试收不到鼠标 |
| 帧泵靠 `RequestNextFrameRendering()` 自我续期 | `OpenGlControlBase` 不会自动连续重绘 |
| 像素尺寸用 `TopLevel.RenderScaling` 折算 | 否则高 DPI 下分辨率与点大小错 |
| 静态字段清空需 generation 守卫 | 旧控件的 `Deinit` 可能晚于新控件 `Init` |
| GLES3 查扩展必须 `GetStringS(StringName.Extensions, i)` | `glGetString(GL_EXTENSIONS)` 在 ES3 下非法 |

## 4. 线程模型

- **文件解析**：`Task.Run` 后台线程，产出 `PointCloudData`（SOA 或 MMF 视图）。
- **GPU 上传**：只能在渲染回调里做。控件持有 `_pendingCloud` 字段，后台完成后赋值 + `RequestNextFrameRendering()`；`OnFrame` 开头检测并上传（分片，每帧最多 N 片，避免长时间占满一帧）。
- **分块包围盒**：`ChunkBoundsBuilder.Build` 也在后台线程整云扫描（32 M 点 Debug 下 1.4~1.9 s），与上传并行；结果放 `_readyChunks`，渲染回调里的 `ApplyReadyChunks` 取走并 `SetChunks`。算完之前不分块（整块画）。
- **后台扫描的生命周期**：它读的是点云的映射内存，换图/`OnGlReleased` 前必须 `CancelChunkBuild()`——取消**并且等待**任务退出（切片内部不检查取消，64 MB 一片要几百毫秒），否则会和 `XyzCloud.Dispose` 抢指针。
- **UI 状态**：`FrameStatsUpdated` 事件来自渲染线程，必须 `Dispatcher.UIThread.Post` 回 UI 线程再更新状态栏。
- **优化算子**：`Task.Run` 里跑，`IProgress<double>` 回报进度（UI 线程回调自动排队），`CancellationToken` 只在每 65536 点检查一次。
  SOR 的查询阶段对网格只读、每点写自己的下标，点数 ≥ 20 万时走 `Parallel.For`（5.4 M 点 k=8：串行 71.6 s → 并行 7.5 s）。
  算子输出是**新建的托管数组**（`XyzCloud.Create`），不持有 MMF，所以能直接替换视图里的云、也能导出；换云后旧的 `XyzCloud` 由控件释放。
- **合并**：整条链路（读包 → 采样 → ICP → 拼接 → 栅格合成）都在一个 `Task.Run` 里，进度按阶段分摊
  （开包/采样 0~5%、粗配准 5~30%、精配准 30~55%、点云拼接 55~70%、栅格 70~100%）；
  ICP 每次迭代检查取消，点云拼接每 65536 点检查。合并会**再开一次两包的 MMF**（与视图里那份并存，
  48 M 点的样例峰值约 2.5 GB），输出是新建托管数组，交接给视图后由控件释放。
- **刚换完云的空窗**：`PointCloudView.Cloud` 要等上传完（渲染帧）才就绪，之前新数据在 `_pendingCloud` 里。
  导出、算子、替换统一用 `View.CurrentCloud`（`PendingCloud ?? Cloud`）——**顺序不能反**：
  换云之后、渲染帧之前 `Cloud` 还是旧的那份，优先读它会导出上一次的数据（曾经的真 bug）。
  `SetCloud` 现在会释放被覆盖的旧 pending：否则连着换两次云而中间没渲染帧，零拷贝的旧云一直占着 pcd，
  替换时改名直接失败。
- **撤销栈**（`MainWindow._undo`）：编辑类算子动手前把「改动前的点云 + 栅格 + 状态栏文案」压栈。
  点云不可变，压栈只是拿引用；但视图会在换云时释放旧云，所以编辑前先 `View.DetachCloud()`
  把所有权取进撤销栈，撤销时再 `SetCloud` 交回去。栈深 8 步、内存预算 2 GB（只算非零拷贝点云 + 栅格像素），
  换包/合并清空。撤销栅格后的地面纹理重建与擦除共用同一个版本号，连撤几次也只让最后一次上屏。

## 5. 内存/显存预算（样例数据实测）

| 数据 | 点/像素 | 内存 | 显存（12 B/点） |
|---|---|---|---|
| `map-...-180314` PCD | 16.19 M | 185 MB（MMF 可映射，不占堆） | 194 MB |
| `map-...-210819` PCD | 32.46 M | 371 MB | 390 MB |
| PGM | 6811×4503 | 30.7 MB（单通道） | 纹理按块 ≤ 25 MB/块 |

- 点云优先走 MMF 零拷贝上传，避免「文件 → 数组 → 顶点缓冲」三份拷贝。
- 2D 地图**不整图转 RGBA**（否则 +120 MB），只按视口区域采样生成屏幕尺寸位图。

## 6. 参考实现出处

| 本仓库文件 | 来源 | 说明 |
|---|---|---|
| `App/Rendering/GlControlBase.cs` | MilkAvalon `src/MilkAvalon/Rendering/GlControlBase.cs` | 帧泵、输入、DPI、generation 守卫 |
| `App/Rendering/GlInfo.cs` | MilkAvalon 同名文件 | 能力表探测（含 `GetStringi` 修正） |
| `Rendering.Gles3/Shader.cs` | MilkDrop.Rendering.Gles3 `Shader.cs` | uniform location 缓存 |
| `App/Controls/SpikeGlControl.cs` | MilkAvalon 同名文件 | 保留作「GL 故障隔离对照物」 |

移植时保留出处注释，并确认上游 LICENSE 与本仓库 GPLv3 兼容。
