# MyPcdEditor

机器狗巡检地图工具：查看、优化、合并 **PCD 点云** 与 **PGM 栅格地图**。

## 当前状态

**阶段 2：3D 点云 + 2D 栅格地图同时可见，点云投影已叠加，栅格图已铺成 3D 地面。**

| 里程碑 | 内容 | 状态 |
|---|---|---|
| M0 | 工程骨架 + ANGLE/Silk.NET 打通 | 完成 |
| M1 | PCD / PGM / yaml 只读 IO | 完成 |
| M2 | 3D 点云查看 | 完成 |
| M2b | 2D 地图视图 + 与点云叠加 | 完成 |
| M2c | PGM 贴成 3D 地面纹理 | 完成 |
| M2d | 点云抽样降载（1/2 · 1/4 · 1/8） | 完成 |
| M2e | 分块 + 视锥剔除（只画视野内的块） | 完成 |
| M3+ | 编辑 / 优化算子 / 多图合并 | 规划中 |

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
- **2D 地图面板**：默认与 3D 视图左右并排（可拖分隔条或点「2D 地图」收起）。滚轮以光标为锚点缩放、
  左键拖动平移、`F`/`R` 适配整图；标题栏实时显示光标处世界坐标、栅格行列与 occupancy 语义。
  「叠加点云」勾选后按世界 XY 投影点云（20 万抽样点，橙色）。
- **地面纹理**：栅格图铺在点云下方（比最低点再低 2~8 cm，避免 z-fighting）。整片灰蓝底板是地图覆盖范围，
  上面的白色细线是占用栅格（墙体），可直接与点云墙体对位核对。`PGV_NO_GROUND=1` 启动即关闭。

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
