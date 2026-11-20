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
        │   ├── PointCloudRenderer   : program + 绘制（含索引抽样降载）
        │   └── GroundTextureRenderer: 单张栅格纹理 + 水平四边形（先画、开深度写入）
        │
        └── MyPcdEditor.Core （无 GL / 无 Avalonia 依赖）
            ├── Pcd/{PcdHeader,PcdReader,PcdWriter}
            ├── Grid/{PgmReader,PgmWriter,MapYaml,OccupancyMap}
            ├── Grid/{OccupancyPalette,GroundTexture} : 语义配色 / 降采样成 GL 纹理数据
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
- **UI 状态**：`FrameStatsUpdated` 事件来自渲染线程，必须 `Dispatcher.UIThread.Post` 回 UI 线程再更新状态栏。

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
