# 技术决策记录（ADR）

格式：编号 / 决策 / 理由 / 影响面。**已定决策若要推翻，请新增条目而非改写旧条目。**

---

## ADR-001 只用 `net11.0` + `win-x64`

- **理由**：用户指定 net11；只服务 Windows 巡检工作站，不背跨平台成本。
- **影响**：Avalonia 12 只提供 `net8.0`/`net10.0` 资产，net11 项目向前兼容消费，无 net11 专用优化；`RuntimeIdentifier=win-x64` 固定在 App 项目。

## ADR-002 UI 使用 Avalonia 12.1.6

- **理由**：已实测 12.1.6 仍提供 `Avalonia.OpenGL.Controls.OpenGlControlBase`（注意 `OpenGL` 大写，Avalonia 11 是 `OpenGl`）。
- **影响**：所有 `Avalonia.*` 包必须**同版本**，混用 12.1.1 与 12.1.3 会 `TypeLoadException`。默认开启 CompiledBindings，XAML 需 `x:DataType`。

## ADR-003 3D 走 `Silk.NET.OpenGLES` + Avalonia 的 ANGLE 上下文

- **理由**：Windows 上 Avalonia 的 GL 上下文本身由 ANGLE（GLES 3.x）提供，用 `new Silk.NET.OpenGLES.GL(new LamdaNativeContext(glInterface.GetProcAddress))` 一行绑定即可，无需自建 EGL surface、无需 `Silk.NET.Windowing`。
- **代价**：着色器必须写 `#version 300 es`；桌面 GL 特性（如 `glGetString(GL_EXTENSIONS)`）不可用。
- **备选**（未采用）：`Silk.NET.Windowing` 自建 GLFW 窗口 + `NativeControlHost` 嵌入 —— UI 层级、DPI、输入都麻烦。
- **原生依赖**：`Silk.NET.OpenGLES.ANGLE.Native` 内是 x86 二进制（`BadImageFormatException`），改用 `Avalonia.Angle.Windows.Natives` 的 win-x64 `av_libglesv2.dll`。

## ADR-004 点云 IO 自研，不引 PCL / PclSharp

- **理由**：PclSharp 是 P/Invoke 包装原生 PCL，体积大、部署重、跨版本易崩；本项目只需要 PCD/PGM 的读写与少量几何算法。
- **做法**：按 PCD 规范实现通用字段表（`FIELDS/SIZE/TYPE/COUNT`），支持三种 DATA，自写 LZF。

## ADR-005 顶点布局 12 B/点（纯 xyz），颜色在着色器里算

- **理由**：样例数据只有 `x y z`，没有 intensity/rgb。预计算颜色会到 16 B/点（32M 点 = 520 MB 显存），且切换着色模式要重算 buffer。
- **做法**：VBO 只放 `vec3`，高度色带/单色由 uniform + 片元着色器决定。字段扩展（有 intensity 时）再增加可选属性。

## ADR-006 点云上传走 MMF 零拷贝快路径

- **理由**：样例 PCD 是纯 `float32 xyz` 的连续块（185.32 MB = 16,193,746 × 12 字节，无多余字节），文件字节即顶点字节。
- **影响**：`PcdReader.TryOpenRawXyz` 返回 MMF 视图，直接分片 `BufferSubData`；避免「文件→数组→顶点缓冲」三份拷贝。不满足条件（ascii/压缩/多字段）时回退通用解析。

## ADR-007 2D 栅格图不进 GL，用 Skia 视口采样

- **理由**：PGM 达 30.7 M px，整图转 RGBA 要 +120 MB 且每次重采样昂贵；2D 只读查看用 Skia 足够。
- **例外**：点云同屏叠加时需要地面纹理，这部分走 GL 分块纹理（每块 ≤ 25 MB），按需上传。

## ADR-008 合并优先用 `projected_origin`（UTM）而非 ICP

- **理由**：数据自带 UTM zone 与投影原点的绝对坐标，跨趟巡检对齐是刚体变换即可求解，比配准稳定得多。
- **影响**：`MapPackage` 从第一天就保留 `ProjectedOrigin` / `Zone` / `Grids` 字段，查看阶段不参与逻辑。
- **兜底**：无锚点（例如跨 zone 或手工裁剪过的包）时才提供手工两点对齐。

## ADR-009 性能只对新增实现负责，且每里程碑留记录

- **理由**：延续既往项目习惯：功能定稿后立即量化，避免「优化过但没人知道」。
- **做法**：`docs/performance.md` 表格化记录打开耗时、首帧、FPS、显存；`docs/progress.md` 留时间线。
- **约束**：查看阶段不做花式优化，先跑基线；只有实测不达标才引入分块/剔除。
