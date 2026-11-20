# 进度留痕

按时间倒序或正序追加，一条一个动作，写清楚「做了什么 / 卡在哪 / 下一步」。

---

## 2026-11-20（九）M2e：分块 + 视锥剔除（连续点区间）

### 实现

- Core 新增 `ChunkedBounds`：`ChunkRange(FirstPoint, PointCount, Bounds)` + `ChunkBoundsBuilder`。
  按**连续索引区间**切块（默认 64 K 点），不做空间重排——点云是零拷贝映射（ADR-005），
  重排要复制 390 MB；按索引切块则是遍历时顺路累加 AABB，零成本。
- Core 新增 `Frustum`：Gribb–Hartmann 从 view-projection 提 6 平面（列点积，System.Numerics 行向量约定），
  p-vertex 保守 AABB 测试；退化平面按"永远可见"处理，宁可多画不能漏画。
- `PointCloudRenderer`：`SetChunks` + `SampleIndexRange` + `DrawChunks`。因为抽样索引升序生成
  （第 k 个 = k·step），一个点区间天然对应索引缓冲里一段连续区间，逐块 `DrawElements(offset)` 即可，
  **不需要为每块单独建索引**——剔除与抽样正交。
- `PointCloudView`：分块包围盒在**后台 Task** 整云扫描（Debug 下 32 M 要 1.9 s），与 GL 上传并行；
  算完之前 chunks 为空 → 整块画，算完由 `ApplyReadyChunks` 在渲染线程接管。换图/关闭时
  `CancelChunkBuild` 会取消**并等待**任务退出（任务读的是云映射内存，先 Dispose 会崩）。
- 环境变量：`PGV_NO_CULL=1`（关剔除 A/B）、`PGV_CAM_ZOOM=0.08`（拟合后拉近，测局部视角）、
  `PGV_CHUNK_POINTS=N`（块粒度）、`PGV_SELFTEST_PARTIAL_VIEW=1`（放大时允许点云出视野）。
- 状态栏仅在真的有块被剔时显示 `· 块 170/496`；`PGV_TRACE_FPS=1` 每行也带块数与实画点数。
- `--selftest`：新增"分块剔除把 N 块全剔了"判据；读回像素前先等后台分块就绪（上限 15 s），
  否则测到的还是"不分块整块画"那条路径。
- 测试：Core 34 个（新增 `CullingTests` 10 个：单位矩阵视锥、AABB 压边不误剔、剔除相机身后、
  退化矩阵全保留、按索引切块、末块不满、分片上传、未触及块用兜底、跳过非有限值、偏移未对齐报错）、
  App 15 个（新增 `SampleIndexRange` 分区不变式）。

### 实测（2380×1312，Debug，稳态中位数）

| 场景 | 剔除关 | 剔除开 |
|---|---|---|
| 32.46 M 全图 | 16.8 | 17.4 |
| 32.46 M `zoom 0.08`（提交 11.10 M/32.46 M = 34.2%） | 19.7 | 32.7 / 34.4 |
| 32.46 M `zoom 0.25` | 8.6 | 9.4 |
| 16.19 M 全图 | 30.7 | 31.7 |
| 32.46 M `zoom 0.08` + 1/4 抽样 | — | 54.0 |

- 全图视角 496 块全可见，与单块（`PGV_CHUNK_POINTS=33554432`，16.4）持平 → 496 次 draw call 无可测开销。
- 块粒度定 64 K 的依据：这份数据点序几乎无空间局部性（4 K 点横跨 132 m），
  1/2 M 块放大只省 18% 点，64 K 省 66%。
- 正确性：`zoom 0.08` 剔除开/关两帧像素统计完全一致（非背景 100.0%、唯一色 132、亮度 68.5、彩色 6.6%）。
- 加载：后台扫描把 32 M 从 7.2 s 拉回 5.8~6.4 s（上传完成 5.1~5.6 s / 扫描 1.4~1.9 s 并行）；16 M 2.2 s。
- 自检两套数据 PASS（exit=0），Core 34 + App 15 全绿。

### 下一步

- 若还要更快：视锥剔除已到顶（点序不紧凑），剩下的是更细块（draw call 成本上升）
  或"按可见块重建索引"的 LOD；本轮不再深挖。
- 抽样时点变稀可考虑点大小 `sqrt(step)` 补偿（一直未做）。

---

## 2026-11-20（八）M2d：点云抽样降载（索引抽样）

### 先定位瓶颈

上一轮遗留「32 M 只 17 FPS，是不是 2D 面板拖的」。加 `PGV_NO_MAP=1` 收起 2D 面板后 GL 视口从
1424×1312 放大到 2380×1312，**帧率仍是 17.1** —— 视口面积涨 67% 而帧率不变，说明不是像素填充，
是每帧 3200 万顶点的提交。所以优化方向只有减少顶点数，分辨率/着色器都白搭。

### 实现

- `PointCloudRenderer.SampleStep` + 惰性索引缓冲：`glDrawElements` + `i*step` 的 uint 索引，
  1/4 抽样 8.1 M 索引 = 32 MB，重建约 20 ms（只在切档时做一次）。
- 为什么不"只画前 1/N 个点"：点云按扫描顺序排列，前缀截断会让视野里只剩一小块区域。
  实测两者帧率完全一样（40.3 FPS），所以索引抽样的跨步访问没有带宽惩罚。
- `PointCloudRenderer.SamplePointCount` 纯函数（向上取整，UI 与测试共用）。
- 工具栏「细节」下拉：全部 / 1/2 / 1/4 / 1/8；状态栏显示 `绘制 1/4（8,115,667 点）`。
- `PGV_DRAW_STEP` 保留给基准脚本。

### 实测（窗口 1424×1312，2D 面板 + 地面纹理全开，Debug）

| 数据集 | 全部 | 1/2 | 1/4 | 1/8 |
|---|---|---|---|---|
| 16.19 M `180314` | 31.0 | 49.6 | 63.0 | 62.4 |
| 32.46 M `210819` | 17.7 | 31.0 | 43.0 | 60.0 |

- 32 M 数据 1/4 档 43 FPS（2.4×），1/8 档 60 FPS 已到帧泵上限（16 M 的 1/4、1/8 也是 62~63，
  说明 63 FPS 左右是天花板，再抽也不会更快）。
- 抽样是空间全覆盖的：1/4 档截图里点云结构完整、没有"缺一块"，只是密度变稀。

### 下一步

- 若要让 32 M 全量也顺：分块 + 视锥剔除（只画进入视野的块），预期比全量少画一半以上。
- 抽样时点变稀，可以考虑点大小自动补偿（`sqrt(step)`），看实际使用是否值得。

---

## 2026-11-20（七）M2c：PGM 贴成 3D 地面纹理

### 完成

- Core 新增 `GroundTexture`：PGM（30.7 M px）按整数块 max-pooling 降到 ≤2048 边长
  （6811×4503 → 1703×1126，块 4，7.7 MB）；类别按 占用 > 未知 > 空闲 取最大，
  再对占用做半径 2 的膨胀，避免细墙在缩小显示时被平均掉。行序与 PGM 一致（v=0 对应 MaxY）。
- `OccupancyPalette` 为 GL 单独定义一套配色/alpha（墙 246/249/255/255、未知 136/144/162/170、
  空闲 96/104/120/140）：2D 面板那套深色空闲色贴到近黑背景上等于隐形。
- `GroundTextureRenderer`（Rendering.Gles3）：单个 RGBA8 纹理 + 一个水平四边形，
  顶点 xyz+uv；先画地面（开深度写入、alpha 混合）再画点云。
- `PointCloudView`：`SetGroundTexture` 挂起上传，等点云上传结束再传（要拿包围盒最低点定 z，
  否则会用到默认 (0,0)..(1,1) 包围盒贴到 z=0）；`GroundZ()` 取 `min.Z - max(0.02, 高度×0.002)` 防 z-fighting。
- 工具栏加「地面纹理」勾选框；`PGV_NO_GROUND=1` 可启动时关掉（基准 A/B 用）。
- `--selftest` 增加判据：包内有 PGM 但地面纹理没上传到 GL 即失败，并打印上传尺寸。
- 测试：Core 24 个（新增降采样/膨胀/alpha 层次断言）、App 12 个（真实包构建 + 落盘 PNG + 构建耗时上限）。
- 性能：构建 215/224 ms（后台），纹理 7.7/8.2 MB，稳态 FPS 与关掉地面纹理一致（31.5 vs 33 / 17.0 vs 17.0）。

### 踩坑

1. **一度以为纹理链路上传坏了**：真实数据渲染出来是一整片均匀灰，而棋盘纹理完全正常。
   逐层排查（UV 走 fract 平铺、强制 `textureLod(…, 0)`、把 `texture().rgb` 直接当颜色输出、
   把 `Rgba` 落盘 PNG）后才确认**墙其实画出来了，只是 1 px 细白线**——放大截图才看得见。
   根因是"1703 texel 铺 680 m，屏幕上每像素只对应 1.7 texel"，1 texel 宽的白线视觉上等于没有。
   处置：膨胀半径 1 → 2（约 5 texel ≈ 3 屏幕像素），并把空闲区从"几乎透明"提到可辨认的灰板。
2. **深色配色搬不过来**：先把 2D 面板的 `free(30,34,40)` 直接给 GL，alpha 40/96 时叠在黑背景上
   和背景差不到 8 级灰度，整片地面像不存在。GL 侧必须用浅色系，两套配色分开定义。
3. **mipmap 会吃掉墙**：2048 级纹理生成 mipmap 后，全景视角采样到 4~5 级，1~2 texel 的墙
   被逐级平均成背景。改为不生成 mipmap（Linear + ClampToEdge），代价是细线轻微闪烁。
4. `gl.GetError()` 返回的枚举名 `Points` 其实是 0（`GL_NO_ERROR`）——`GL_POINTS` 与错误码枚举
   在 Silk.NET 里共用一个值，别把它当错误。

### 下一步

- 32 M 点 20 FPS 的抽样降载（LOD）→ 见 M2d。
- 复核 2D 面板的渲染成本 → 见 M2d：收掉面板帧率不变，与面板无关。

---

## 2026-11-20（六）M2b：2D 栅格地图视图 + 点云投影叠加

### 完成

- Core 新增两块纯逻辑（无 Avalonia 依赖，headless 单测覆盖）：
  `MapViewTransform`（视口中心 + 每米像素数，屏幕↔世界、锚点缩放、适配、平移）、
  `OccupancyPalette`（按 ROS 语义 `negate/*_thresh` 分类，暗色主题配色：占用亮 / 空闲近底色 / 未知居中）。
- `MapView : Control`（Skia）：`WriteableBitmap` 视口采样，只在交互/尺寸变化时重绘；
  缩小时每个屏幕像素对覆盖的源矩形取 ≤4×4 采样点、按 占用>未知>空闲 取最大（max-pooling），
  避免细墙被最近邻整段漏掉。滚轮以光标为锚点缩放、左键拖动平移、`F`/`R` 适配、点击后接管键盘焦点。
- 叠加层：点云 `SamplePoints(200_000)` 按世界 XY 投影画成橙色，"叠加点云"勾选框即时开关；
  与栅格共用同一套世界坐标变换，因此天然对齐。
- 窗口改为 `3* / GridSplitter / 2*` 三分栏，工具栏「2D 地图」开关收起时把列宽一起归零；
  地图标题栏实时显示光标世界坐标、栅格行列、occupancy 语义与当前缩放。
- 加载流程里 PGM 只读一次（30 M px），摘要与 2D 视图共用；点云叠加层抽样在后台线程完成。
- `--selftest` 增加"2D 地图已加载"检查（包内有 PGM 但视图为空即失败）并打印缩放/叠加点数。
- 测试：Core 21 个（新增 6 个变换/语义用例）、App 11 个（新增合成图与真实包的像素级截图用例）。
  构建 0 警告，实测 16.19 M PASS（9.6% / 1.4 s）、32.46 M PASS（15.0% / 6.1 s）。

### 踩坑

1. **`Render` 里不能 `InvalidateVisual()`**：headless 下抛「InvalidateVisual() called inside the
   measure/arrange/render pass」，进程直接以 0xE0434352 退出。适配计算要拆成 `TryFitCore()`，
   渲染中直接调用、交互里再触发重绘。
2. 纯最近邻缩小时会**整段漏掉 1~2 px 的墙**（640×480 视口仅 78 个采样亮点），必须 max-pooling。
3. `IsVisible=false` 不会释放 Grid 星号列，面板"收起"后仍占 2/5 宽——要把 `ColumnDefinitions`
   的宽度一起改。
4. 原计划里 M2b 还有"PGM 当地面纹理贴进 3D"，本轮用并排 2D 面板替代（见 `decisions.md` ADR-007），
   纹理路线留作可选后续。

### 下一步

- 32 M 点 20 FPS 的抽样降载（LOD）。
- 2D 面板截图基线（`tests/baselines/`）。
- M3 优化算子的第一批：体素降采样 + 栅格去孤岛。

---

## 2026-11-20（五）默认视角上线：修掉矩阵转置 bug + 按抽样点拟合

### 完成

- 相机默认视角改为斜 45° 俯视（`OrbitCamera.DefaultYaw/DefaultPitch`），拖动只允许微调
  （水平 ±25°、俯仰 ±20°，灵敏度 0.9），点云中心即 `Target`。
- `Fit` 拆成 `Fit(Bounds3)` 与 `Fit(ReadOnlySpan<Vector3> points, center, aspect)`：
  逐点求"让它落在视野内所需的最小距离"，并按视口宽高比修正水平方向。
- `XyzCloud.SamplePoints(max)` 均匀抽样；`PointCloudView` 上传完成后抽样 8192 点做拟合，
  换点云时丢弃缓存。用抽样点而不是包围盒角点：角点常落在数据外，画面里点云会缩成一小块
  （16.19 M 样例非背景从 0.9% 提到 12.2%）。
- `--selftest` 增加"抽样点 NDC 范围"判定（越界 > 2% 即失败）与 PNG 帧快照（`PGV_SELFTEST_PNG`）。
- 实测：16.19 M PASS（12.2% / 唯一色 150 / 2.9 s）、32.46 M PASS（12.4% / 169 / 6.7 s）、
  空目录 FAIL（1）。测试 19 个全绿，构建 0 警告。

### 踩坑

1. **`Shader.SetMatrix4` 多做了一次 `Matrix4x4.Transpose`**：`System.Numerics` 是行主序 + 行向量
   （`v * M`），GLSL 是列主序 + 列向量（`M * v`）；把行主序内存原样交给 GL，GLSL 读成转置，
   `M^T * v` 恰好等于 `v * M`。多转置一次会让整个画面长期是错的（点偏向一侧、大小异常），
   而 CPU 侧算出的 NDC 与图像对不上就是这个原因。
2. 屏幕截图抓不到 GL 渲染内容（拿到黑块或残留画面），只能靠 `glReadPixels` + `WriteableBitmap`
   落盘 PNG 核对；据此才定位到上面的矩阵问题。
3. `Bitmap.Save(path)` 已过时，改用 `Save(path, new PngBitmapEncoderOptions())`。

### 下一步

- `MapView` 2D 地图视图 + 点云叠加。
- 32 M 点 20 FPS 的抽样降载。
- `--selftest` 增加着色模式参数，把单色模式也纳入判定。

---

## 2026-11-20（四）渲染自检 `--selftest` 落地

### 完成

- `FramePixelStats`（`Rendering.Gles3`）：`glReadPixels` 读回 RGBA8，统计非背景占比、量化唯一色数、
  平均亮度、彩色占比，并给出 `LooksRendered` 判据。
- `PointCloudView.RequestPixelCapture()` + `PixelCaptured` 事件：读回在渲染回调内完成（FBO 仍绑定）。
- `SelfTestSession`（`App/SelfTest`）：加载 → 等上传完成 → 再画 3 帧 → 读回 → 判定 → `Shutdown(exitCode)`，
  整体超时 180 s。
- 实测：16.19 M 点 PASS（非背景 29.7%、彩色 100%、1.9 s），32.46 M 点 PASS（24.3%、5.9 s），
  空目录 FAIL 退出码 1。
- 用法与判据写入 [`docs/testing.md`](testing.md) §4。

### 踩坑

1. 只设 `Environment.ExitCode` 再关窗口，Avalonia 会把进程退出码重置为 0；
   必须 `IClassicDesktopStyleApplicationLifetime.Shutdown(exitCode)`。
2. 上一轮冒烟残留的 `MyPcdEditor.exe` 进程会锁住 apphost，`dotnet build` 报 MSB3027，
   重跑前要先 `Stop-Process -Name MyPcdEditor`。

### 下一步

- `MapView` 2D 地图视图 + 点云叠加。
- 32 M 点 20 FPS 的抽样降载。

---

## 2026-11-20（三）测试策略：headless 能力边界实测

### 完成

- 新建 `tests/MyPcdEditor.App.Tests`（xunit **v3** + `Avalonia.Headless.XUnit 12.1.6`），6 个用例全绿：
  窗口装配、左键拖动改 yaw、滚轮改 distance、滑块同步点大小、Skia 截图可捕获、GL 控件在 headless 下无上下文。
- 结论写入 [`docs/testing.md`](testing.md)：headless 覆盖「窗口/布局/绑定/输入/命中测试/截图」，
  **不能**覆盖 GL 上下文与绘制。
- `PointCloudView` 暴露只读 `Camera` 属性，供交互断言使用。

### 踩坑

1. `Avalonia.Headless.XUnit 12.1.6` 依赖 **xunit v3**（`xunit.v3.extensibility.core 3.2.2`），
   沿用 xunit 2.x 会报 `Catastrophic failure: Could not find app host executable`；
   测试项目还必须是 `OutputType=Exe` + `UseAppHost=true`。
2. 想用 `CaptureRenderedFrame()` 截图，必须 `.UseSkia()` 且 `UseHeadlessDrawing = false`。
3. headless 鼠标事件用**窗口坐标**，控件中心要 `TranslatePoint(..., window)` 换算。

### 下一步

- `--selftest`：真窗口渲染后 `glReadPixels` 统计非背景像素占比，补上 GL 的自动化回归。
- `MapView` 落地后加 Skia 截图基线比对。

---

## 2026-11-20（二）M0 + M1 + 基础 3D 查看

### 完成

- 样例数据复制到 `testdata/`（两份包，620 MB，已在 `.gitignore` 排除）。
- 工程骨架落地：`MyPcdEditor.slnx` + `Directory.Build.props`（`net11.0` / `win-x64`）+ 四个项目
  （Core / Rendering.Gles3 / App / Core.Tests）。
- **M0 打通**：`GlControlBase` + `GlInfo` + Silk.NET 绑定可用。实机日志：
  `ANGLE (AMD, AMD Radeon(TM) Graphics, Direct3D11 ...) / OpenGL ES 3.0 / GLSL ES 3.00`。
- **M1 完成**：PCD 头通用解析、ascii/binary/binary_compressed(LZF)、零拷贝 MMF 路径、
  PGM P2/P5、`map.yaml`、`.projected_origin`、`*_id_map.toml`、`MapPackage` 目录聚合。
- **M2 基础版**：点云渲染（12 B/点 VBO、turbo 高度色带/单色、圆形点）、轨道相机
  （左键旋转 / 右键平移 / 滚轮缩放 / F 适配 / R 复位）、64 MB 分片上传 + 进度、
  状态栏（点数/耗时/包围盒/分辨率/FPS/Renderer）。
- 单元测试 9 个全绿，含真实数据对拍（点数、零拷贝、包围盒、栅格尺寸）。

### 修复记录（值得记住的坑）

1. **分片上传只传第一片**：`_pendingCloud` 在首帧被清空后，后续帧从 `_pendingCloud` 取不到点云直接返回，
   进度永远停在 17%。改为「`_pendingCloud` 只作触发信号，续传统一读 `_activeCloud`」。
2. **`dotnet sln add` 在 .slnx 上静默失败**（报告"已添加"但文件仍是空模板），改为手写 slnx。
3. 渲染回调里的异常会被 Avalonia 合成器吞掉 → 只看到黑屏。已在 `GlControlBase` 捕获并打印一次堆栈。

### 已知问题 / 下一步

- PGM 目前只显示尺寸读数，**2D 地图视图与点云叠加还没画**（`MapView` 未实现）。
- 32 M 点 2380×1312 只有 20 FPS（集显、Debug）。计划：索引抽样（1/2、1/4 抽稀）+ 分块视锥剔除，
  再复测；不要把时钟花在着色器上。
- 上传期间一帧一片 64 MB 会导致 5–7 FPS 的瞬时卡顿，需改为「每帧限时上传」。
- Release 构建的基线还没采（当前只有 Debug）。

---

## 2026-11-20（一）需求与文档初始化

### 完成

- 需求对齐：机器狗巡检 PCD + PGM 查看/优化/合并小工具。
- 技术栈定稿：`net11.0` + `win-x64` + `Avalonia 12.1.6` + `Silk.NET.OpenGLES 2.23.0`（ANGLE）。
- 验证关键假设（真实环境跑过，非推断）：
  - `Avalonia 12.1.6` 仍含 `OpenGlControlBase`，`GlInterface.GetProcAddress(string) -> IntPtr` 签名未变。
  - `Silk.NET.OpenGLES` 类型为 `Silk.NET.OpenGLES.GL`，存在 `GetApi(Func<string,nint>)` 重载，可与 Avalonia 直接对接。
  - `Avalonia 12.1.6` 资产只到 `net10.0`，net11 项目可消费。
- 阅读 `F:\github\MilkAvalon` 参考实现，锁定 6 个可移植要点（见 `docs/architecture.md` §6）。
- 实测两份样例数据，结论写入 `docs/data-format.md`。
- 文档初始化：`README.md` + `docs/{plan,architecture,data-format,decisions,performance,progress}.md`。
