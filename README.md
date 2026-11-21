# MyPcdEditor

机器狗巡检地图工具：查看、优化、合并 **PCD 点云** 与 **PGM 栅格地图**。

## 当前状态

**阶段 5：查看、优化、合并、替换、框选删点都已跑通（无锚点手工对齐、笔刷、栅格橡皮擦未做）。**

| 里程碑 | 内容 | 状态 |
|---|---|---|
| M0 | 工程骨架 + ANGLE/Silk.NET 打通 | 完成 |
| M1 | PCD / PGM / yaml 只读 IO | 完成 |
| M2 | 3D 点云查看 | 完成 |
| M2b | 2D 地图视图 + 与点云叠加 | 完成 |
| M2c | PGM 贴成 3D 地面纹理 | 完成 |
| M2d | 点云抽样降载（1/2 · 1/4 · 1/8） | 完成 |
| M2e | 分块 + 视锥剔除（只画视野内的块） | 完成 |
| M3 | 优化算子（点云 + 栅格）+ 结果导出 | 完成（首版） |
| M4 | 多包合并（GPS 锚点 → ICP 精配准 → 点云 + 栅格拼接） | 完成（自动对齐；无锚点手工对齐未做） |
| M5 | 替换原包（写回当前包 + 备份 + 整包回滚）+ 3D 框选删点 | 完成 |
| M6+ | 笔刷 / 栅格橡皮擦 / 撤销栈 / 参数预览 / 一次合多包 | 规划中 |

实机验证（AMD 集显 / ANGLE GLES 3.0 / Debug）：

| 场景 | 数据集 | 帧率 |
|---|---|---|
| 全图 · 全部 | 16.19 M 点 | 31.0 FPS |
| 全图 · 抽样 1/2 · 1/4 · 1/8 | 16.19 M 点 | 49.6 · 63.0 · 62.4 |
| 全图 · 全部 | 32.46 M 点 | 17.7 FPS |
| 全图 · 抽样 1/2 · 1/4 · 1/8 | 32.46 M 点 | 31.0 · 43.0 · 60.0 |
| 放大到局部 · 全部（视锥剔除开/关，窗口 2380×1312） | 32.46 M 点 | 33.4 vs 19.7 FPS |
| 放大到局部 · 抽样 1/4（同上） | 32.46 M 点 | 54.0 FPS |

371 MB PCD 零拷贝加载 ~400 ms；地面纹理构建 215 ms / 8 MB 显存 / 对帧率无影响。
瓶颈是顶点提交而非填充（视口放大 67% 帧率不变），所以提速手段是抽样（降密度）与视锥剔除（不画视野外）。
分块按连续点区间切（32.46 M → 496 块，每块 64 K 点），放大时只提交 34% 的点且画面像素统计与不剔除完全一致。
Release 构建复核结果与 Debug 一致（帧率由 GPU 顶点提交决定），打开 32 M 数据 2.9 s。

合并两趟巡检（16.19 M + 32.46 M，Debug）：ICP 配准 17.7 s（只修正 0.10 m，RMSE 0.548 → 0.113 m），
点云合并 36.0 s → 48,654,696 点（去重 0.05 m 则 78.7 s → 45.5 M 点），栅格合并 12.1 s（44.7 M px）。
详见 [`docs/performance.md`](docs/performance.md)。

详细计划见 [`docs/plan.md`](docs/plan.md)，进度留痕见 [`docs/progress.md`](docs/progress.md)。

## 技术栈

| 项 | 选择 | 说明 |
|---|---|---|
| TargetFramework | `net11.0` | Avalonia 12 无 net11 专用资产，向前兼容消费 net10.0 |
| 平台 | `win-x64` | 只做 Windows 64 位 |
| UI | Avalonia 12.1.6 | 所有 `Avalonia.*` 包版本必须一致 |
| 3D | `OpenGlControlBase` + `Silk.NET.OpenGLES` 2.23.0 | 走 Avalonia 提供的 **ANGLE** 上下文（GLES 3.x） |
| 2D | Skia（Avalonia 自带） | PGM 不进 GL |
| 点云 IO | 自研 | 不依赖 PCL / PclSharp（PclSharp 是 P/Invoke 包装） |

## 目录结构（规划）

```
src/
  MyPcdEditor.Core/            数据模型与 IO（不依赖 GL / Avalonia）
  MyPcdEditor.Rendering.Gles3/ GL 资源层（Shader / 点云缓冲 / 相机）
  MyPcdEditor.App/             Avalonia 应用（GL 控件、窗口、VM）
tests/
  MyPcdEditor.Core.Tests/
docs/                          开发文档
testdata/                      样例数据（不入库，见 .gitignore）
```

## 构建

```powershell
dotnet restore
dotnet build -c Debug
dotnet test

# 启动（可带目录直接打开）
dotnet run --project src/MyPcdEditor.App -- testdata\map-20261116-180314

# 无界面自检：只跑 IO 层，打印地图包与点云摘要
dotnet run --project src/MyPcdEditor.App -- --load testdata\map-20261116-210819

# 基准采集：把 FPS 打到控制台
$env:PGV_TRACE_FPS="1"
dotnet run --project src/MyPcdEditor.App -- testdata\map-20261116-210819

# 渲染自检：真窗口渲染一帧并读回像素，退出码 0 通过 / 1 失败
dotnet run --project src/MyPcdEditor.App -- --selftest testdata\map-20261116-210819

# 自检顺带把读回的帧存成 PNG（PGV_SELFTEST_PNG 可指定路径）
$env:PGV_SELFTEST_PNG="$env:TEMP\frame.png"
```

要求 .NET SDK 11.x（本机 `11.0.100`）。运行需要 GPU 支持 OpenGL ES 3.0（ANGLE 会落到 D3D11）。

操作：

- **3D 视图**：默认斜 45° 俯视点云中心；左键拖动只能小幅微调视角（水平 ±25°、俯仰 ±20°），
  右键拖动平移、滚轮缩放、`F` 适配、`R` 复位。工具栏可切着色模式、点大小与「地面纹理」开关。
- **细节**：点数多时用工具栏「细节」降密度换帧率（全部 / 1/2 / 1/4 / 1/8）。抽样按索引均匀覆盖
  整片点云（不是只画一部分），状态栏会显示当前绘制点数；32 M 点选 1/4 可到 43 FPS。
- **视锥剔除**：默认开启，只绘制进入视野的分块（32.46 M 切 496 块，算完前先不分块）。
  缩放/平移到局部时状态栏显示 `· 块 170/496`；`PGV_NO_CULL=1` 可关闭做对比。
- **框选删点**：工具栏「框选 (B)」打开后左键拖出矩形，松手即把框内的点删掉（远处的悬空黑点、近处的人影）；
  `Shift` + 拖 = 只保留框内。框是屏幕空间的矩形，在世界空间里是一个四棱锥，所以不必先把视角摆成正交。
  框小于 4 px 当误点忽略；会把点全删光时拒绝执行；原始文件不动，撤销 = 「优化 ▾ → 撤销全部优化」（重新加载）。
  48.65 M 点框掉 23.77 M 用时 0.48 s（Release）。
- **2D 地图面板**：默认与 3D 视图左右并排（可拖分隔条或点「2D 地图」收起）。滚轮以光标为锚点缩放、
  左键拖动平移、`F`/`R` 适配整图；标题栏实时显示光标处世界坐标、栅格行列与 occupancy 语义。
  「叠加点云」勾选后按世界 XY 投影点云（20 万抽样点，橙色）。
- **地面纹理**：栅格图铺在点云下方（比最低点再低 2~8 cm，避免 z-fighting）。整片灰蓝底板是地图覆盖范围，
  上面的白色细线是占用栅格（墙体），可直接与点云墙体对位核对。`PGV_NO_GROUND=1` 启动即关闭。
- **优化 ▾**：算子都在后台线程跑，带进度、可取消，原始文件一律不动（结果只在内存里，需要时手动导出）。
  - 点云 · 体素降采样：每体素输出一个质心点（0.05 m 保留 93.7%，0.20 m 保留 16.7%，见 performance.md）。
  - 点云 · 统计去噪 (SOR)：按「最近 k 邻居平均距离」的均值 + 倍数 × 标准差剔除离群点（5.4 M 点 k=8 约 8 s）。
  - 点云 · 按范围裁剪：输入 min/max，可保留或剔除盒内点；输出点云变成可脱离原文件的托管数组。
  - 栅格 · 去毛刺：按 8 邻域占用数删掉 1~2 像素的散点，**保住 1 像素宽的薄墙**（不用开运算，开运算会把它抹掉）。
  - 栅格 · 补洞：形态学闭运算填 1 像素裂缝/针孔。
  - 栅格 · 去孤岛：删掉面积小于阈值的 8 连通小块。
  - 抹掉的栅格像素填 **未知(205)**，不替用户断言「可通行」。
  - 导出当前结果：写出**一个完整的地图包目录**，文件名、锚点、toml 都沿用当前打开的包
    （样例是 `jueying.pcd` / `jueying.pgm` / `jueying.yaml` / `.projected_origin` / `jueying_id_map.toml`），
    导出的目录能被重新打开、也能继续参与下一次合并。写盘走「.tmp + 改名」，写一半失败不会毁掉目标目录里的旧包。
    导出拒绝指向当前打开的包目录——覆盖原包用下面的「替换」。
  - 替换当前地图包：把结果**写回原目录**（先弹确认框列出要写什么）。原文件改名 `*.bak` 备份（只留最近一份），
    `.tmp` 全部写好后才改名就位；任何一步失败整包回滚，不会出现「pcd 是新的、pgm 还是旧的」。
    打开后没动过点云时跳过 pcd（零拷贝的云映射着文件，改不了名，内容也没变），只写栅格与 yaml。
  - 撤销全部优化：重新打开原包。
- **合并 ▾**：把另一趟巡检的包并进当前包（当前包作参考）。
  - 对齐：先按两包的 `.projected_origin` UTM 锚点相减定初值，再跑 4 自由度（yaw + xyz）加权 ICP 精配准。
    两包必须同 UTM 带号；锚点差直接是初值时（实测 0.548 m），ICP 通常只再修正 10 cm 量级。
  - 拼接：点云按各自变换合成一份（可选体素去重，默认 0.05 m，可填 0 关掉）；
    栅格按 origin 放置后取并集，冲突优先级 占用 > 未知 > 空闲，并统计重叠像素。
  - 合并前会问去重体素，合并中状态栏显示进度、「取消合并」可随时中断（数据不变），
    合并后**先出对齐报告**（初值来源 / 修正量 / RMSE / 内点率），点「采用结果」才替换视图；
    报告可在「查看上次对齐报告…」回看。结果只在内存里，按「优化 ▾ → 导出当前结果」存成新包
    （与参考包同名，含 `.projected_origin`，可直接再打开或继续和第三个包合并）。
  - 界面上一次合一个包；两包必须同 UTM 带号，无锚点的包当前会被拒绝并提示原因。

## 样例数据

不入库，需手工放置到 `testdata/`，目录形态为「一个地图包 = 一个目录」：

```
testdata/
  map-20261116-180314/   jueying.pcd  jueying.pgm  jueying.yaml  jueying_id_map.toml  .projected_origin
  map-20261116-210819/   ...
```

数据规格见 [`docs/data-format.md`](docs/data-format.md)。

## 文档索引

| 文档 | 内容 |
|---|---|
| [docs/plan.md](docs/plan.md) | 范围界定与里程碑 |
| [docs/architecture.md](docs/architecture.md) | 分层、渲染栈、线程与 GL 约束 |
| [docs/data-format.md](docs/data-format.md) | PCD / PGM / yaml / toml 解析约定与实测数据 |
| [docs/decisions.md](docs/decisions.md) | 关键技术决策记录（ADR） |
| [docs/performance.md](docs/performance.md) | 性能基准与复测记录 |
| [docs/testing.md](docs/testing.md) | 测试分层：headless 能测什么、GL 怎么测 |
| [docs/progress.md](docs/progress.md) | 进度时间线 |

## 许可

GPLv3，见 [LICENSE](LICENSE)。
