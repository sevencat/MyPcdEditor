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
- **实现**：`MapView` 每次重绘只生成一张视口大小的 `WriteableBitmap`，缩小时对每个屏幕像素覆盖的
  源矩形做 max-pooling（≤4×4 采样、占用优先级最高）。实测纯最近邻在 640×480 视口下会漏掉整段墙
  （采样亮点 78 → 484）。
- **与原计划的差异**：原打算"PGM 当 3D 地面纹理"（GL 分块纹理 ≤25 MB/块）。本轮先用 **并排 2D 面板
  + 点云 XY 投影叠加** 满足"同屏对照"，好处是零纹理显存、无 LOD 失真、缩放/读数自由；代价是看不到
  点云与地图在 3D 里的贴合关系。地面纹理列为可选后续，届时复用 `MapViewTransform` 的世界坐标换算。

## ADR-008 合并优先用 `projected_origin`（UTM）而非 ICP

- **理由**：数据自带 UTM zone 与投影原点的绝对坐标，跨趟巡检对齐是刚体变换即可求解，比配准稳定得多。
- **影响**：`MapPackage` 从第一天就保留 `ProjectedOrigin` / `Zone` / `Grids` 字段，查看阶段不参与逻辑。
- **兜底**：无锚点（例如跨 zone 或手工裁剪过的包）时才提供手工两点对齐。

## ADR-009 性能只对新增实现负责，且每里程碑留记录

- **理由**：延续既往项目习惯：功能定稿后立即量化，避免「优化过但没人知道」。
- **做法**：`docs/performance.md` 表格化记录打开耗时、首帧、FPS、显存；`docs/progress.md` 留时间线。
- **约束**：查看阶段不做花式优化，先跑基线；只有实测不达标才引入分块/剔除。

## ADR-011 抽样降载用索引抽样，不做点云重排/分块 LOD

- **理由**：32.46 M 点全量 17.7 FPS，且视口放大 67% 帧率不变 → 瓶颈是顶点提交。
  降低顶点数最省事的方式就是"每隔 N 个点画一个"，索引缓冲 1 行 GL 调用，不动点云数据本身。
- **为什么不前缀截断**（`DrawArrays(0, count/N)`）：点云按扫描顺序存放，画前 1/N 等于只显示
  地图的一小块。实测两者帧率相同（40.3 FPS），没有理由牺牲覆盖范围。
- **为什么不做空间 LOD（八叉树/体素分块）**：需要遍历 32 M 点重排或额外拷贝，加载变慢、
  内存翻倍；而本工具的用法是全景查看，稳定抽样已经够用。真要继续提性能，应该做
  **分块 + 视锥剔除**（少画视野外的块，与抽样正交），而不是更聪明的抽样。
- **影响**：`SampleStep` ∈ {1,2,4,8}，由工具栏「细节」控制；索引缓冲按档位重建（约 20 ms）。
  抽样后点密度下降，点大小由用户自行调节。

## ADR-010 地面纹理走「单张降采样纹理」，不分块

- **理由**：ADR-007 把地面纹理列作后续，前提是"分块按需上传（≤25 MB/块）"。实际度量后不需要：
  PGM 按整数块 max-pooling 降到 2048 上限只要 215 ms（后台线程），纹理 7.7 MB，
  而点云 VBO 是 194/390 MB；单个四边形对帧率无可测影响（开/关都是 33 / 17 FPS）。
- **实现**：`GroundTexture.Build` 降采样 → `GroundTextureRenderer` 单纹理 + 一个水平四边形，
  先画地面（深度写入）再画点云。
- **代价与取舍**：
  1. 缩放显示时 1 texel 的墙会退化成 1 px 细线 → 占用做半径 2 膨胀（显示用加粗，不动原始栅格）。
  2. 不生成 mipmap（mip 会把墙平均掉），细线在缩小/移动时轻微闪烁。
  3. GL 侧配色与 2D 面板分开：3D 背景近黑，必须用浅色系灰板，否则整片地面隐形。
- **未走的路**：不再做"按视距分块 + 只上传可见块"——本工具是全景俯视查看，纹理本来就只全屏一次。
