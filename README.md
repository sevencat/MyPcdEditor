# MyPcdEditor

机器狗巡检地图工具：查看、优化、合并 **PCD 点云** 与 **PGM 栅格地图**。

## 当前状态

**阶段 0：文档与计划就绪，代码尚未开始。**

| 里程碑 | 内容 | 状态 |
|---|---|---|
| M0 | 工程骨架 + ANGLE/Silk.NET 打通 | 未开始 |
| M1 | PCD / PGM / yaml 只读 IO | 未开始 |
| M2 | 3D 点云查看 + 2D 地图查看（只读） | 未开始 |
| M3+ | 编辑 / 优化算子 / 多图合并 | 规划中 |

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
```

要求 .NET SDK 11.x（本机 `11.0.100`）。运行需要 GPU 支持 OpenGL ES 3.0（ANGLE 会落到 D3D11）。

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
| [docs/progress.md](docs/progress.md) | 进度时间线 |

## 许可

GPLv3，见 [LICENSE](LICENSE)。
