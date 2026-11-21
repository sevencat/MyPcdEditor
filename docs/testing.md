# 测试策略

结论先行：**Avalonia headless 能覆盖除 GL 之外的全部 UI 逻辑**（2026-11-20 本仓库实测），
GL 渲染要靠「真窗口 + `glReadPixels` 自检」和 FPS 基线，不要指望 headless。

## 一、能力矩阵（实测）

`tests/MyPcdEditor.App.Tests` 里 25 个用例已经坐实了下表前四行。

| 能力 | Avalonia headless | 真窗口（本机 ANGLE） |
|---|---|---|
| 窗口装配 / 布局 / 数据绑定 / 样式 | ✅ | ✅ |
| 输入注入（鼠标按下/移动/滚轮/键盘） | ✅ `window.MouseDown/MouseMove/MouseWheel` | ✅ |
| `ICustomHitTest` 命中、指针归一化、相机响应 | ✅ 已验证（拖动改 yaw、滚轮改 distance） | ✅ |
| Skia 截图（`CaptureRenderedFrame`） | ✅ 已验证（需 `.UseSkia()`） | ✅ |
| GL 上下文 / 着色器编译 / 实际绘制 | ❌ 无 `IPlatformGraphics` | ✅ |
| `GlControlBase` 的 `OnGlReady` / `PixelWidth` | ❌ 永远不初始化（=0） | ✅ |
| 像素级 GL 断言（`glReadPixels`） | ❌ | ✅ `--selftest`（已实现） |
| FPS / 显存 / 上传耗时 | ❌ | ✅ `PGV_TRACE_FPS=1` |

关键点：headless 下 `PointCloudView.Info == null`、`PixelWidth == 0` 是**预期行为**，不是 bug；
已有用例 `GlControl_HasNoContext_UnderHeadless` 把这个契约钉住了，防止以后误以为在 headless 里测到了渲染。

## 二、工程配置（踩过的坑）

```xml
<!-- tests/MyPcdEditor.App.Tests/MyPcdEditor.App.Tests.csproj -->
<OutputType>Exe</OutputType>       <!-- 必须：headless 测试跑在独立 app host 里 -->
<UseAppHost>true</UseAppHost>      <!-- 否则报 Could not find app host executable -->
<PackageReference Include="Avalonia.Headless.XUnit" Version="12.1.6" />
<PackageReference Include="xunit.v3" Version="3.2.2" />   <!-- 不能用 xunit 2.x -->
```

```csharp
[assembly: AvaloniaTestApplication(typeof(TestAppBuilder))]

AppBuilder.Configure<App>()
    .UseSkia()                                        // 想截图就必须加
    .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
```

其他注意：

1. `Avalonia.Headless.XUnit 12.1.6` 依赖 `xunit.v3.extensibility.core 3.2.2`；用 xunit 2.x 会直接
   `Catastrophic failure: Could not find app host executable`。
2. 用例特性是 `[AvaloniaFact]`（命名空间 `Avalonia.Headless.XUnit`），不是 `[Fact]`。
3. 鼠标事件坐标是**窗口坐标**，控件内部坐标必须 `control.TranslatePoint(point, window)` 换算。
4. `UseHeadlessDrawing = true` 时 `CaptureRenderedFrame()` 拿不到有效位图，截图要关掉它并 `.UseSkia()`。
5. headless 走 UI 线程调度，改完状态要 `Dispatcher.UIThread.RunJobs()` 再断言。

## 三、分层策略

| 层 | 测什么 | 工具 | 现状 |
|---|---|---|---|
| Core | PCD/PGM/yaml 解析、LZF、包围盒、零拷贝路径、视口变换、occupancy 语义、地面纹理降采样/膨胀、视锥剔除/分块包围盒、优化算子（体素降采样/SOR/裁剪/形态学）、体素哈希分布、框选投影判定、合并（锚点初值/ICP/点云拼接/栅格合成）、写包（文件名/锚点/toml 沿用参考包）、替换（两阶段提交/备份/回滚） | 普通 xunit（v2） | 87 个用例 ✅ |
| App 逻辑 | 窗口装配、按钮/滑块/下拉绑定、指针桥接、相机响应、状态栏文案、抽样点数换算、抽样索引按块分区、合并入口接线（合并→确认→导出，放弃→保持原样）、导出到当前包目录被拒绝、替换（写回/取消/跳过未变 pcd）、框选（画框不动相机/小框忽略/删点/全删光拒绝） | headless + xunit v3 | 25 个用例 ✅ |
| 2D 地图视觉 | `MapView` 绘制、栅格配色、缩放采样 | headless + Skia 截图读像素 | 已实现 ✅（见 §5） |
| 地面纹理 | `GroundTexture` 数据含墙、构建耗时上限、PNG 落盘核对 | headless（真实包，无数据跳过） | 已实现 ✅（见 §5） |
| GL 渲染 | 着色器编译、点云画出来没有、颜色/裁剪是否正常 | 真窗口 `--selftest`：渲染后 `glReadPixels` 统计非背景像素比例 | 已实现 ✅ |
| 性能 | 打开耗时 / 上传 / FPS / 显存 | `PGV_TRACE_FPS=1` + 采样脚本，手工记录到 `docs/performance.md` | 已有基线 |

性能测量与 A/B 归因用的环境变量（只影响启动初值，正常用界面开关）：

| 变量 | 作用 |
|---|---|
| `PGV_TRACE_FPS=1` | 控制台按秒打印 `[FPS] xx.x WxH`（含 `块=可见/总数 绘制=N`） |
| `PGV_NO_GROUND=1` / `PGV_NO_MAP=1` | 启动即关掉地面纹理 / 收起 2D 面板，用来做 A/B 归因 |
| `PGV_DRAW_STEP=N` | 抽样步长（界面「细节」下拉的等价物） |
| `PGV_NO_CULL=1` | 关掉分块视锥剔除（剔除开/关 A/B，性能结论都靠它） |
| `PGV_CAM_ZOOM=0.08` | 拟合后把相机距离乘该倍数，用来模拟"放大看局部"的剔除收益 |
| `PGV_CHUNK_POINTS=N` | 每块点数（默认 64 K；设成 33554432 就是"单块"，即不分块对照） |

不做的事：不把 620 MB 样例数据塞进单测（有数据才跑、没数据 return，已经是现在的写法）；
不在 CI 里跑 GL 用例（无 GPU 会误报）。

### 优化算子的用例要点

算子全是纯函数（输入云/图 → 输出新的云/图），所以能在普通 xunit 里直接断言数值：

| 覆盖点 | 用例 |
|---|---|
| 体素合并：同格多点 → 1 个质心点 | `VoxelDownsample_MergesPointsInSameVoxel` |
| 跨格不合并、非有限点丢弃、原点取包围盒最小角、空云、非法边长 | `VoxelDownsample_SplitsAcrossVoxelBoundary` / `_SkipsNonFinitePoints` / `_AssignsVoxelsRelativeToBounds` / `_EmptyCloudStaysEmpty` / `_RejectsInvalidVoxelSize` |
| 裁剪内/外两侧、边界点归属 | `CropBox_KeepsOnlyInside` / `CropBox_CanInvertToExclude` / `CropBox_IncludesPointsOnBoundary` |
| SOR 删孤点、无邻居必删、均匀云不误删、非有限点、非法参数 | `OutlierRemoval_DropsIsolatedPoint` / `_LoneFarPointIsDroppedWithNoNeighbors` / `_KeepsUniformCloudIntactWithLooseThreshold` / `_DropsNonFinitePointsRegardless` / `_RejectsBadParameters` |
| 体素哈希值域（防 24 位退化重演） | `VoxelHash_SpreadsAcrossFullIntRange` |
| 开运算去孤点/保实心块，以及**会抹掉 1 px 薄墙**这条已知局限 | `Open_RemovesSinglePixelSpur` / `Open_KeepsSolidBlock` / `Open_ErasesOnePixelWideLine_KnownLimitation` |
| 去毛刺保薄墙/保对角、闭运算补洞且不侵入空白、去孤岛按面积与 8 连通 | `Despeckle_RemovesSpeckleButKeepsThinWall` / `Despeckle_KeepsDiagonalWall` / `Close_FillsSinglePixelHole` / `Close_DoesNotGrowIntoSolidFreeArea` / `RemoveIslands_*` |
| PGM 写出后读回一致 | `PgmWriter_RoundTripsThroughReader` |

时间不写进断言（机器差异大），性能只在 `docs/performance.md` 里手工记录。

### 合并的用例要点

合成场景用的是**随机撒点**的房间（地面 + 四墙 + 柱子），不能用规则栅格：
规则栅格的对应点会周期性重合，ICP 会收敛到「错一排」，测出来的是夹具的周期性而不是算法。

| 覆盖点 | 用例 |
|---|---|
| 锚点相减得初值（含 float32 精度容差，UTM 绝对坐标只有厘米级精度） | `Anchors_*`（同 zone / 异 zone / 缺锚点） |
| 纯平移、已知 yaw+平移能精确恢复 | `Align_RecoversPureTranslation` / `Align_RecoversKnownYawAndTranslation` |
| 无重叠时保留初值并给出说明（而不是乱配） | `Align_ReportsFailureWhenNoOverlap` |
| 场景外多余点不把外推带偏，且内点率如实降低 | `Align_IgnoresPointsOutsideOverlap` |
| 栅格：范围并集 / 冲突优先级 / 重叠计数 / 旋转后 bbox | `GridMerge_*` |
| 点云：拼接点数、每份各自的变换、去重 | `CloudMerge_*` |
| 端到端（真实文件往返：打开两包 → 对齐 → 拼云 + 拼图 → 读回） | `MapMerger_EndToEndAlignsTwoPackages` |

「硬截断会裁掉唯一有效约束」这个坑（ADR-016）就是被 `Align_RecoversPureTranslation` 抓到的：
它当时停在 0.155/0.6 m 且 `Converged=true`。**收敛标志只能说明「不再动了」，不能说明「对上了」**——
所以对齐质量的断言要落在**解本身**和**内点率**上，不能只看 `Converged`。

App 侧 `MergeFlowTests` 用「锚点差精确等于场景平移」的合成包（初值即真值，ICP 不动），只验接线：
采用路径断言合并后 2000 点、合并栅格 200×100 px（证明用的是合并后的 meta 而不是包里的 yaml）、
导出文件名沿用参考包（`map.pcd`）且带 `.projected_origin`；放弃路径断言视图与导出仍是原始包那一份；
另有「导出到当前包目录被拒绝」一条守住原始数据。
无窗口下 `PointCloudView.Cloud` 永远不就绪（没有渲染帧），所以导出/算子统一取 `View.CurrentCloud`
（`PendingCloud ?? Cloud`：换云之后、渲染帧之前 `Cloud` 还是旧的那份，顺序反了会导出上一次的数据）。
替换另有三条：合并后替换写回原目录（`.bak` 里是旧的 1000 点）、确认框取消则一个字节不动、
打开后没动过点云则跳过 pcd（零拷贝的云映射着文件，硬写必然失败）。

写包器（`MapPackageWriterTests`）的关键断言是**读回**而不是「文件存在」：
`MapPackage.Open` 重新打开导出目录，点云点数、yaml 分辨率、锚点 X/zone、toml 的 `[[grids]]` 都要对得上。
只断言「文件写出来了」会漏掉最要命的一种失败——文件写了但锚点没带，导出件再也不能参与合并。
另外两条边界：`overwrite:false` 时旧文件必须原样（写 `.tmp` 再改名的意义），以及无可写数据时不建空目录。

替换的用例同理，断言的是「`.bak` 里是替换前的内容」——只断言「目标变了」会漏掉备份写错
（备份成新内容等于没备份）。回滚用例（`Replace_RollsBackWhenTargetIsLocked`）故意锁住 yaml：
此时 pcd、pgm 已经改名让位，必须一起还原，否则包就是「一半新一半旧」。这条用真文件锁模拟、
不 mock 文件系统，才能测到 `File.Move` 在文件被占用时真的会失败。

框选的用例避开相机状态：投影矩阵取 `Matrix4x4.Identity`，世界坐标直接就是 NDC（x&lt;0 落屏幕左半），
于是「框右半」有确定答案，不用去核对相机四元数。`M44 = -1` 造出 w&lt;0（相机后方）：
屏幕位置算不出来，必须当「不在框内」。App 侧用 headless 鼠标真画一次框，断言「相机 yaw 没变、
事件带出的矩形非空」——框选模式最容易犯的错是拖框的同时把视角转了，松手时框和画面已经错位。

## 四、渲染自检 `--selftest`（GL 的自动化回归）

```powershell
# 真窗口渲染一帧后读回像素：退出码 0 通过 / 1 失败
dotnet run --project src/MyPcdEditor.App -- --selftest testdata\map-20261116-180314

# 顺便把读回的帧存成 PNG 肉眼核对（桌面截图抓不到 GL 内容）
$env:PGV_SELFTEST_PNG="$env:TEMP\frame.png"
```

流程：加载地图包 → 等 GPU 分片上传完成 → **等后台分块包围盒就绪（上限 15 s）** →
用 `cloud.SamplePoints(8192)` 投到 NDC 检查视野 → 再画 3 帧 → `glReadPixels` 读回整帧 →
统计 → 判定 → `lifetime.Shutdown(exitCode)`。

等分块这一步不能省：分块扫描在后台与上传并行（32 M 点约 1.9 s），上传先结束时
`TotalChunkCount` 还是 0，读回测到的就是"不分块、整块画"那条路径，剔除等于没验。

判据：

| 检查 | 含义 | 判定 |
|---|---|---|
| 抽样点 NDC 范围 | 点云是否完整落在视野内（±1 为可见范围） | 越界超过 2% 即失败 |
| `NonBackgroundRatio` | 与清屏色 `(0.06,0.07,0.09)` 不同的像素占比 | ≥ 2% |
| `DistinctColors` | 量化到 5 bit/通道后的颜色数 | ≥ 8 |
| `ColorfulRatio` | 非背景像素中通道最大差 > 24 的占比（turbo 色带是否生效） | 只记录 |
| `MeanLuminance` | 全图平均亮度（0..255） | 只记录 |
| 2D 地图已加载 | 包内有 PGM 但 `MapView` 为空 | 失败 |
| 地面纹理已上传 | 包内有 PGM 但 `_groundRenderer.HasTexture` 为 false | 失败 |
| 分块剔除未全剔 | `TotalChunkCount > 0` 但 `VisibleChunkCount == 0` | 失败 |

NDC 检查用**抽样点**而不是包围盒 8 角点：斜置 / 细长的点云，角点常常落在数据之外，
按角点判定会误报"超出视野"（实测角点范围 x=[-0.79, 1.04]、y=[-2.76, 0.75]，而抽样点只在
x=[-0.69, 0.88]、y=[-0.89, 0.47]）。角点范围仍会打印出来作参考。

本机实测（AMD 集显 / ANGLE GLES 3.0 / 窗口 1280×800，2D 面板占 2/5 宽，GL 视口 1340×1233）：

| 数据集 | 非背景 | 唯一色 | 彩色 | 总耗时 | 结果 |
|---|---|---|---|---|---|
| 16.19 M 点 `180314` | 36.9% | 214 | 25.4% | 2.2 s | PASS（0） |
| 32.46 M 点 `210819` | 49.7% | 234 | 30.1% | 6.4 s | PASS（0） |
| 空目录 | — | — | — | 0.4 s | FAIL（1） |

（耗时含等待后台分块扫描：16 M 上传本身就 0.7 s，但要等到扫描结束才读回。
Release 下同样 PASS，总计 1.12 s / 2.90 s，后台扫描降到 56 ms / 132 ms。）

### 剔除正确性对拍（放大到局部）

```powershell
$env:PGV_CAM_ZOOM="0.08"; $env:PGV_SELFTEST_PARTIAL_VIEW="1"
$env:PGV_SELFTEST_PNG="$env:TEMP\zoom_cull.png"
dotnet run --project src/MyPcdEditor.App -- --selftest testdata\map-20261116-210819

$env:PGV_NO_CULL="1"; $env:PGV_SELFTEST_PNG="$env:TEMP\zoom_nocull.png"
dotnet run --project src/MyPcdEditor.App -- --selftest testdata\map-20261116-210819
```

放大后点云必然出视野，所以要 `PGV_SELFTEST_PARTIAL_VIEW=1` 跳过"完整在视野内"那条断言。
两次的**像素统计必须逐项一致**（剔除只能少画看不见的块）：实测非背景 100.0%、唯一色 132、
平均亮度 68.5、彩色 6.6% 完全相同，而 `本帧提交` 从 496 块/32.46 M 点降到 170 块/11.10 M 点。

同时打印 2D 地图状态（`已加载 · 1.26 px/m · 叠加 202,422 点`）与地面纹理尺寸
（`已上传 1703×1126`）；包内有 PGM 但 `MapView` 没加载、或地面纹理没上传到 GL，都直接判失败。

非背景从 9.6%/15.0% 跳到 37.7%/49.7%、彩色占比从 100% 掉到 25.4%/30.1%，是地面纹理
（浅灰蓝底板）铺满地图范围后的正常变化——判据看的是"有没有内容"，不受影响。

它能发现的问题：GL 上下文起不来、着色器编译失败（渲染回调异常 + 画面只剩背景）、点云没画出来
（非背景 ≈ 0）、点云飞出视野（NDC 越界）、色带失效（彩色占比 ≈ 0）、上传卡死（180 s 超时）、
分块视锥剔除误杀（块全被剔 → 画面空；或在放大场景下与 `PGV_NO_CULL=1` 的像素统计不一致）。

限制：需要可用的桌面会话与 GPU（会真的开一个窗口）；无桌面/无 GPU 的 CI 不要跑这条，用 `--load` 代替。

实现位置：`src/MyPcdEditor.App/SelfTest/SelfTestSession.cs`、`src/MyPcdEditor.Rendering.Gles3/FramePixelStats.cs`。

## 五、2D 地图视图的像素级验证（Skia 路线）

`MapView` 不碰 GL，走 Skia，所以能直接 headless 断言像素：

```csharp
var map = new MapView { Width = 320, Height = 240 };
var window = new Window { Content = map };
window.Show();
map.SetMap(pgm, yaml);            // 合成的 64×64 半占用图
Dispatcher.UIThread.RunJobs();     // 等布局/渲染
WriteableBitmap frame = window.CaptureRenderedFrame()!;
// 锁帧缓冲按 BGRA 读回，统计亮像素（占用 #E8ECF2）/ 暗像素（空闲 #1E2228）
```

两条用例：

| 用例 | 内容 | 判据 |
|---|---|---|
| `MapView_RendersOccupiedAndFreeCells` | 合成图（左半占用 / 右半空闲） | 亮、暗像素各 > 500 |
| `MapView_RealPackage_RendersWalls` | 真实包 `180314` 的 6811×4503 PGM（无数据则 return） | 亮 > 200、暗 > 5000；快照落 `%TEMP%\mypcd-mapview.png` |

真实包用例抓到过一个真问题：**缩小时纯最近邻会把 1~2 px 的墙整段漏掉**（640×480 视口里只剩
78 个采样亮点）。改成"每个屏幕像素覆盖的源矩形内取 ≤4×4 采样点、按 占用>未知>空闲 取最大"后
升到 484（×16 采样 ≈ 7.7 k 实际像素），墙面才完整可见。

注意 `Render` 里不能 `InvalidateVisual()`：headless 下会抛「InvalidateVisual() called inside
the measure/arrange/render pass」直接崩掉进程，适配计算要拆成纯函数在渲染中调用。

### 地面纹理的验证

GL 侧纹理采样没法 headless 断言，所以拆成两段：**数据对不对**用 headless 测，
**画出来对不对**用 `--selftest` + 截图肉眼核对。

| 用例 | 内容 | 判据 |
|---|---|---|
| `GroundTexture_DownsamplesWithMaxPooling`（Core） | 8×8 合成图（左上 2×2 墙）降到 4×4 | 左上为占用色+不透明、膨胀到的 (1,1) 变墙、离墙 3 texel 的 (3,3) 仍是空闲半透明 |
| `GroundTexture_UnknownKeepsDistinctAlpha`（Core） | 全未知图 | block=1、alpha = `GlUnknownA` |
| `GroundTexture_AlphaOrderIsWallThenUnknownThenFree`（Core） | 常量关系 | 墙 > 未知 > 空闲 > 0 |
| `GroundTexture_RealPackage_KeepsWalls`（App） | 真实包 `180314`（无数据则 return） | 墙占比 > 1%、构建 < 2 s；快照落 `%TEMP%\mypcd-groundtexture.png` |

`--selftest` 侧再加一条判据：包内有 PGM 但 `GroundTextureRenderer.HasTexture` 为 false 即失败
（`[SELFTEST] 地面纹理：已上传 1703×1126`）。

**教训**：截图统计只能证明"非背景像素占比正常"，证明不了"墙看得见"。本轮一度以为纹理上传坏了
（真实数据渲染成一片均匀灰、棋盘纹理却正常），最后一层层拆解才发现墙确实画出来了，只是
1 px 细白线。细线类特征要靠**裁剪放大 + 近白像素计数**才看得出来，别只看整帧缩略图。

## 六、后续 TODO

1. 2D 地图补截图基线：首帧截图存 `tests/baselines/`，比较时给容差（±2/255）。
2. headless 用例补「打开真实目录」路径：`MapPackage.Open` 在 headless 下可跑，只是不上传 GPU，
   可以在无 GPU 环境验证 UI 状态栏文案。
3. `--selftest` 增加着色模式参数，把单色模式也纳入判定（当前只验证高度色带）。
