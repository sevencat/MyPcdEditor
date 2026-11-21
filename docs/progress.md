# 进度留痕

按时间倒序或正序追加，一条一个动作，写清楚「做了什么 / 卡在哪 / 下一步」。

---

## 2026-11-21（十七）撤销栈：编辑错了不用重新加载

### 改了什么

- `MainWindow` 长出撤销栈（8 步 / 2 GB 预算）：框选删除、框选保留、笔刷擦除、栅格擦除、
  点云优化（体素降采样 / SOR / 裁剪）、栅格形态学（去毛刺 / 补洞 / 去孤岛）动手前把「改动前」压栈，
  `Ctrl+Z` 或工具栏「撤销 XXX」换回去。换包与合并会清空历史（合并换了坐标系，旧快照没法还原）。
- 点云是不可变对象，压栈只拿引用；所有权从视图转到撤销栈（新增 `PointCloudView.DetachCloud`），
  撤销那一刻再交回视图。这一步是必须的：`SetCloud` 会在上传新云时释放旧云，不转移所有权就是 use-after-free。
- 零拷贝的旧云只是文件映射，按虚拟内存算不占实际内存；快照预算只统计「非零拷贝的点云 + 栅格像素」，
  超了就丢最旧的一步。撤到空栈时按钮变灰，状态栏报「没有可撤销的编辑」。
- 顺带修了两处：编辑后 `_cloudPointCount` 没更新（绘制抽样条一直显示加载时的点数）；
  栅格算子一个像素都没改时也照样「成功」，现在直接报「没有可改的像素」且不入栈。

### 实测（真实包 `map-20261116-180314`，6811×4503 栅格，Release）

擦一笔栅格后撤销（含地面纹理重建 38 ms）：**49 ms**。点云撤销是换引用，代价在重新上传（异步，不阻塞 UI）。

### 验证

`dotnet build -warnaserror` 0 警告 0 错误；`dotnet test` Core 100 + App 33 = **133 全绿**。
新增 App 2 条：框选删除 → 撤销恢复点数与按钮状态，栈空再撤报「没有可撤销的编辑」；
栅格擦除 → 撤销把像素换回 0。两条都断言「撤销后所有权回到视图」（`CurrentCloud` 可读、点数正确）。

### 下一步

- 重做（redo）：现在撤销之后栈就断了，和大多数编辑器一致，够用
- 无锚点手工对齐（跨 zone 的包目前直接拒绝合并）

---

## 2026-11-21（十六）2D 栅格橡皮擦：把误判的像素刷回未知

### 改了什么

- Core 新增 `GridEraser.Erase(image, meta, path[], radius, target)`：与 3D 笔刷同一套折线胶囊判定，
  只是坐标换成栅格像素。单点笔迹 = 圆、折线 = 胶囊，`target` 取 `Unknown(205)` / `Free(254)`。
  返回 `GridMorphResult`（新图 + `ChangedPixels`），一个像素都没改时（本来就刷成目标值）由调用方跳过。
- 2D 面板 `MapView` 长出擦除手势：左键涂抹（右键平移、滚轮缩放照旧）、`G` 键切模式、
  松手把「栅格像素折线 + 半径」交给 MainWindow。半径按「DIP → 物理像素 → 米 → 栅格像素」换算，
  放大看细节时笔刷跟着画面一起变大，所见即所擦。
- 顺带给 `MapView` 加了位图脏标记：以前每次 `InvalidateVisual` 都重填整张视口（几百万像素），
  涂抹时每帧一次根本受不了；现在只有栅格/叠加层/视口变了才重填，笔迹和圆环画在位图之上。
- `SetMap(image, meta, fit: false)`：每涂一笔都把画面缩回全图的话没法连续作业。
- 工具栏：「栅格擦除 (G)」开关 + 「擦成空闲」复选框；半径滑块与 3D 笔刷共用（本来就是同一件事）。
  工具栏已经长到放不下一行，包了 `ScrollViewer` 横向滚动。

### 实测（真实包 `map-20261116-180314`，6811×4503 栅格，Release）

一条横穿画面的笔迹（半径 20 栅格像素）：改 178,933 px，**13 ms**；
地面纹理（30.7 M px → 顶点）重建 **38 ms**。两笔加起来还不到一帧的时间，
所以没做「重建期间合并多次擦除」的排队逻辑——直接等重建，用版本号保证只有最后那笔的结果上屏。

### 验证

`dotnet build -warnaserror` 0 警告 0 错误；`dotnet test` Core 100 + App 31 = **131 全绿**。
新增 Core 3 条（折线覆盖范围 / 单点为圆 / 目标值与不变像素）、
App 3 条（涂抹采笔迹且不动视图 / 工具栏开关与 G 键同步 / 落笔改像素后二次擦除报「已经是未知」）。

### 下一步

- 撤销栈（框选/笔刷/栅格擦除目前都只能靠「重新加载」回退）
- 无锚点手工对齐（跨 zone 的包目前直接拒绝合并）

---

## 2026-11-21（十五）3D 笔刷擦除：擦成片的噪声

### 改了什么

- Core 新增 `PointCloudOps.CropScreenBrush(cloud, viewProjection, centers[], radius, aspect, ...)`：
  判定是「点到折线段的距离 ≤ 半径」，两端天然是圆头（`t` 夹在 [0,1]）。
  折线在第 `i` 段上的几何，与 UI 上那条 `StrokeLineCap=Round` 的粗折线完全一致——所见即所删。
  v 方向按 `1/aspect` 折算到 u 的尺度，于是「半径」在像素空间是各向同性的圆（不是随窗口变形的椭圆）。
  逐点先过笔迹包围盒，盒外直接保留，省掉逐段距离计算。
- 标记 + 打包逻辑从 `CropScreenBox` 抽成私有 `Compact`，两个算子共用（都保持原始点序）。
- App：工具栏「笔刷 (E)」+ 半径滑块（4~160 px，默认 32），`E` 键切换。与框选**互斥**——
  两者抢同一个左键，同时开着的话拖一下既画框又画笔迹，说不清。
- 拖动中**不动数据**，只把笔迹画在 Avalonia 叠加层：一条 `Polyline`，`StrokeThickness = 2r`、
  圆头圆角。每帧重建 48 M 点的云不现实，所以松手才一次性删。重采样间距「半个笔刷」，
  顶点上限 256（绕圈涂太久会截断，否则逐段判定变慢）。
- 光标圆环用 `Ellipse` 跟随鼠标（`OnPointerMoved` / `OnPointerExited`），半径改滑块立即重画。
- 松手后走与框选同一个 `ApplyCloudEditAsync`（busy 闸门 / 进度 / 全删保护 / 换云上屏），
  所以「刷满全屏把点刷光」同样会被拒绝。

### 实测（真实两包合并 → 48,654,696 点，Release）

屏幕中间一条横线（40 段、半径 0.02 归一化 ≈ 25 px）：删 3,320,304 点、剩 45,334,392 点，**892 ms**。
比框选（483 ms）慢约 1.9 倍：每点要遍历 40 段折线。再快就得给笔迹建屏幕格栅索引，
当前一笔 1 秒内落地的体验已经够用，先不加。

### 验证

`dotnet build -warnaserror` 0 警告 0 错误；`dotnet test` Core 92 + App 28 = **120 全绿**。
新增 Core 5 条（沿折线删 / 半径边界 / 单点为圆 / 相机后方不删 / 参数校验）、
App 3 条（与框选互斥 / 拖动采笔迹且不动相机 / 落笔删点与状态栏）。

### 下一步

- 2D 栅格橡皮擦（刷成未知 205 / 空闲）
- 撤销栈（框选/笔刷目前都只能靠「重新加载」回退）

---

### 改了什么

- Core 新增 `ScreenBox`（归一化 0..1、左上原点、`Min ≤ Max`）与
  `PointCloudOps.CropScreenBox(cloud, viewProjection, box, keepInside, progress, ct)`：
  逐点投影判断是否落在屏幕矩形内，删除（默认）或只保留框内。
- 为什么必须逐点投影：屏幕矩形在世界空间里是一个**四棱锥**，用 `CropBox` 那种 AABB 根本表达不了
  （远处的悬空黑点、近处的人影恰恰是最常见的噪声，它们在盒子里、但不在框里）。
  判定用与渲染同一个 `view × projection`（行向量约定，`Vector4.Transform` 与着色器 `uViewProj * v` 等价），
  宽高比取视图当下的值，所以框和画面严丝合缝。相机后方（w ≤ 0）与 NaN/Inf 点一律不算框内、并被丢弃。
- 性能：并行标记（每块 1 M 点，每点只写自己的 1 字节）+ 单线程打包；48 M 点并行度足够，不需要前缀和。
- App：工具栏「框选 (B)」`ToggleButton` 与 `B` 键共享一个开关（视图发 `BoxSelectModeChanged` 回来同步按钮）。
  框选模式下左键拖动画框、不再旋转视角（右键平移、滚轮缩放照旧）；`Shift` + 拖 = 只保留框内。
- 矩形视觉用 Avalonia 的 `Rectangle` 叠在 GL 视图上面一层（`Canvas` + `IsHitTestVisible=false`），
  比在 GLES 里另写一套 2D 着色器省事，也不受渲染后端影响。
- 保护：小于 4 px 的框按误点忽略；`ApplySelectionAsync` 在「会删光所有点」时拒绝执行（那基本是手滑）；
  删完 `SetCloud` 会清掉旧框（对应的是旧数据）。撤销仍是「优化 ▾ → 撤销全部优化（重新加载）」，
  原始文件从头到尾不动。

### 踩到的坑

- `in Matrix4x4` 参数不能进 `Parallel.For` 的 lambda（CLR-23），拷一份 64 字节的局部变量传进去。
- 插值字符串的 `{}` 里不能再写 `\"` 转义，先取到局部变量再拼。

### 实测（真实两包合并 → 48,654,696 点，Release）

框住画面中间 40%×40%：删掉 23,765,391 点、剩 24,889,305 点，**483 ms**（Debug 同样操作 5.4 s）。
同一次会话里的替换仍是 0.3 s 量级，没受影响。

### 验证

`dotnet build -warnaserror` 0 警告 0 错误；`dotnet test` Core 87 + App 25 = **112 全绿**。
新增 Core 5 条（删框内 / 只留框内 / 相机后方不算 / NaN 丢弃 / 空框报错）、
App 4 条（框选模式拖动只画框不动相机 / 4 px 以下忽略 / 框内删点与状态栏 / 全删光拒绝）。

### 下一步

- 笔刷（圆形笔刷连续删，比框选更适合成片噪声），与框选共用投影判定、按屏幕半径筛点
- 2D 栅格橡皮擦（刷成未知 205 / 空闲）

---

### 改了什么

- `MapPackageWriter` 拆成两阶段：`Prepare` 只写 `<name>.tmp`，`MapPackageWriteBatch.Commit` 才动目标目录；
  导出 `Write` 与新增的 `Replace` 共用它，区别只是提交参数（`Replace` 保留备份）。
- `Replace` 的提交顺序：每个原文件先改名成 `<name>.bak`（元数据操作，584 MB 也不额外拷一遍），
  再把 `.tmp` 改名就位；任何一步失败都把 `.bak` 改回去、删掉已就位的新文件——**整包还原**，
  不会留下「pcd 是新的、pgm 还是旧的」这种半成品。失败抛 `IOException` 且带「已回滚」字样。
- App：`优化 ▾ → 替换当前地图包（覆盖原文件）…` → 确认框（列出要写什么、备份说明）→ 后台写 →
  重新 `MapPackage.Open` 指向磁盘真实状态。确认默认弹 `ReportDialog`（「替换 / 取消」），
  测试用 `ConfirmReplace` 替换成自动确认/取消。
- 打开后**没动过点云**时跳过 pcd：零拷贝的云就是包里那份文件（内容没变），而且映射着、改不了名；
  只写栅格与 yaml，状态栏说明「点云没改过，未重写 pcd」。

### 顺带修掉的两个真 bug

1. `PointCloudView.SetCloud` 覆盖 `_pendingCloud` 时**不释放旧的**：连着换两次云（打开 → 合并）而中间
   没有渲染帧，旧云泄漏；零拷贝的话它一直占着 pcd，替换时改名直接失败。
2. `View.Cloud ?? View.PendingCloud` 的**顺序反了**：换云之后、渲染帧之前 `Cloud` 是旧的那份，
   优化/合并完立刻导出会拿到上一次的数据。统一成 `View.CurrentCloud`（`PendingCloud ?? Cloud`）。

### 实测（真实两包 16.19 M + 32.46 M，合并到「副本目录」，原样例不动）

合并 35.2 s → 替换 0.3 s。`jueying.pcd` 48,654,696 点；`jueying.pcd.bak` 16,193,746 点；
`.projected_origin` 与 `jueying_id_map.toml` 仍在；5 个文件各留一份 `.bak`；原样例 pcd 点数未变。

### 验证

`dotnet build -warnaserror` 0 警告 0 错误；`dotnet test` Core 82 + App 21 = **103 全绿**。
新增 Core 2 个（替换成功且备份里是旧内容 / 目标被占用时整包回滚）、
App 3 个（合并→替换写回 / 取消不动原包 / 未变的 pcd 跳过不写）。

### 下一步

「手工选择去除」黑点与噪声：3D 框选/笔刷删点 + 2D 橡皮擦，见 `docs/plan.md` M5。

---

## 2026-11-21（十二）M4 收尾：保存成完整地图包（合并结果能再打开、再合并）

### 改了什么

- 新增 `Core/MapPackageWriter`：把当前数据写成一个**结构对齐 `MapPackage` 读取约定的完整包目录**。
  - 文件名沿用当前打开的包（样例是 `jueying.pcd` / `jueying.pgm` / `jueying.yaml`），不再另起
    `optimized.pcd` / `merged.pcd`；
  - 带上 `.projected_origin`（合并结果的局部系就是参考包的局部系）与 `*_id_map.toml`；
  - yaml 的 `image` 改写成同目录文件名（原包写的是另一台机器的 Linux 绝对路径）；
  - 写盘走「先写 `.tmp` 再改名」，pcd 几百 MB 写到一半失败不会毁掉目标目录里的旧包；
  - 没有任何可写数据时不建目录（空目录会让人误以为导出成功）。
- App 导出改为调用它，并在两种情况下明确拒绝/提示：
  - 没打开包 → 「导出的文件名与锚点都参照它」；
  - 导出目录 == 当前包目录 → 拒绝并提示去用「替换」（下一步功能），避免写一半毁掉原始数据。
- 合并完成后的状态栏补一句「结果只在内存里，按「导出」存成新的地图包目录」。

### 为什么以前不行

旧导出只写 pcd/pgm/yaml。导出的合并件一打开就**没有锚点**，再拿去和第三个包合并时
`CloudAligner.TryFromAnchors` 会直接拒绝（而手工对齐还没做）——等于链路走两包就到头了。

### 实测（真实两包 16.19 M + 32.46 M，Debug）

合并 37.0 s → 48,654,696 点；导出 0.4 s，写出 `jueying.pcd / jueying.pgm / jueying.yaml /
.projected_origin / jueying_id_map.toml`；`MapPackage.Open` 重新打开：48,654,696 点、
锚点 (342607.958, 3432976.777, 14.063) zone 21、yaml 0.1 m/px、`image=jueying.pgm`。

### 验证

`dotnet build` 0 警告 0 错误；`dotnet test` Core 80 + App 18 = **98 全绿**。
新增 Core 用例 4 个（写出的包能原样打开 / 文件名沿用参考包 / 拒绝覆盖时旧文件不被破坏 / 无可写数据不建目录）、
App 用例 1 个（导出到当前包目录被拒绝）。

### 下一步

- 「替换」：写回当前包目录（备份 + 二次确认 + 全部写好再动手）。
- 「手工选择去除」（黑点/噪声）：3D 框选/笔刷删点 + 2D 橡皮擦，详见 `docs/plan.md` M5。

---

## 2026-11-21（十一）M4：多包合并（GPS 锚点 → ICP 精配准 → 拼接）

### 实现

- Core/Merge 新增（`App → Core` 单向，无 GL/Avalonia 依赖）：
  - `AlignmentTransform`：4 自由度刚体（yaw + xyz 平移）。`Apply/Inverse/Then/FromTranslation`、
    `Difference` 求两点之间平移量与 yaw 差、`Describe` 出中文串。
  - `CloudAligner.TryFromAnchors`：两包 `.projected_origin` 相减得初值（实测 `world = projected_origin + local`）；
    同 zone 才允许，异 zone 或缺锚点直接说清原因。
  - `CloudAligner.Align`：两级加权 ICP（粗 1.5 m 体素 / 对应距离 1.5 m → 精 0.5 m / 0.6 m），
    每级用 `VoxelGridIndex` 做 3×3×3 近邻、闭式 2D Procrustes 解 yaw+平移（`atan2(Σ(p×q), Σ(p·q))`）。
  - `PointCloudMerger.Merge`：多份点（各带自己的变换）按目标大小 `XyzCloud.Create` 输出，
    可选体素去重（`VoxelAccumulator`，保留格内首点）；进度按份数分摊。
  - `GridMerger.Merge`：范围取并集、按各自 yaml 放置后旋转四角求 bbox；冲突优先级
    占用 > 未知 > 空闲，统计重叠像素（`coverage > 1`）。
  - `MapMerger.Merge`：编排层，出 `MergeOutcome`（云 + 对齐 + 栅格 + 中文报告）。
    栅格合成失败只丢栅格、不拖垮点云。
- App：工具栏「合并 ▾」→「合并另一个地图包…」：选目录 → 问去重体素 → 后台合并（进度 + 「取消合并」）
  → `ReportDialog` 展示对齐报告 →「采用结果」才替换视图（点云 + 合并栅格 + 地面纹理 + 2D 叠加），
  导出文件名改为 `merged.pcd`。「查看上次对齐报告…」可回看。
- 顺带修掉的两处会踩到的空窗：
  - `_gridYaml`：合并后栅格范围变了，不能继续读包里的 yaml（导出/栅格算子都改用它）。
  - `PointCloudView.PendingCloud` + `View.Cloud ?? PendingCloud`：刚换完云还没渲染帧时
    `Cloud` 还是空的，导出与后续算子会误报「没有点云」。

### 卡住并修掉的问题：ICP 的 X 平移收敛到中途（0.6 m 只走了 0.155 m）

诊断（临时用例对比参数组合）结论：**不是变换组合的问题，是硬截断（trimmed ICP）把唯一有效的约束裁掉了**。

- 纯平移用例里，X 方向唯一能给出梯度的只有两道 x 向墙；地面是自相似平面，错位时也能在附近找到
  几乎同样好的邻居（残差 0.09 m），y 向墙的点沿切向滑动残差也是 0.40 m。
- x 向墙点的残差是全场最大的（0.445 m）→ 「丢掉最差 20%」精确地丢掉它们，留下残差小但没信息量的点。
  迭代于是停在一个「残差很小、其实没对齐」的假解上。把 trim 设成 0 立刻精确恢复到 `(0.600, -0.400)`。
- 改为 **Huber 权重**（`HuberCutoffRatio=0.6`，拐点 = 0.6 × 对应距离，管外 `w = 拐点/距离`）：
  远点只降权不丢弃，既压得住少量错误对应，又不扔约束。同一用例 40 轮内精确收敛；
  实测 0.3/0.6/1.0 三种拐点都能收敛到真值（0.3 更慢，1.0 等于不降权）。
- 顺带把内点率的分母从「有对应的点」改成「全部源点」：40% 重叠的包不该算出 95% 的漂亮内点率。

### 实测（两套真实样例：16.19 M + 32.46 M）

| 步骤 | 耗时 | 结果 |
|---|---|---|
| 锚点初值 | — | 平移 (−191.844, −66.500, −0.199)，初值 RMSE 0.548 m |
| ICP 精配准 | 17.7 s | 修正 0.10 m，RMSE 0.113 m，内点率 20.4%，9 轮收敛 |
| 点云合并（不去重） | 36.0 s | 48,654,696 点 |
| 点云合并（去重 0.05 m） | 78.7 s | 45,471,398 点（93.5%） |
| 栅格合并 | 12.1 s | 7047×6353 @0.1 m/px（44.7 M px），占用 394 k / 空闲 43.7 M / 未知 667 k / 重叠 19.3 M px |

- 降采样体素敏感性：目标体素 0.1/0.25/0.5 m 时解分别偏 13 cm / 0 / 9 cm —— 差异量级和 ICP
  自身采样噪声相当，说明这两包**锚点本身已经是准的**，ICP 只是小幅修正 + 给出可信度。
- 内点率 20.4% 不是对齐坏，而是待合包覆盖面积约为参考包两倍（重叠区只占源点的一小部分）。

### 验证

- `dotnet build`：0 错误 0 警告。`dotnet test`：Core 76 + App 17 = **93 全绿**。
- 新增 Core 用例 14 个（对齐 6 / 栅格合成 3 / 点云合成 3 / 端到端 2），含「场景外多余点不拖偏配准」。
- 新增 App 用例 2 个：`MergeFlowTests` 走「加载 → 合并 → 采用 → 导出」与「放弃合并保持原样」两条路径，
  用合成包 + 锚点差精确等于场景平移，断言 2000 点 / 合并图 200×100 px（验证栅格 meta 换成了合并后的那份）。
- 为了让无窗口测试能驱动这些入口：App 项目加 `InternalsVisibleTo("MyPcdEditor.App.Tests")`。

### 下一步

- M4 剩下的：无锚点时的**手工对齐兜底**（两点/两线）没做；当前无锚点直接拒绝并提示原因。
- 合并结果的「撤销」目前仍靠「撤销全部优化 = 重新加载参考包」。
- 后续可考虑：一次合多个包（当前一次一个）、按 GPS 时间插值消除分段漂移（现在只有整体 4 自由度）。

---

## 2026-11-21（十）M3：优化算子（点云 + 栅格）+ 结果导出

### 实现

- Core/PointCloud 新增：
  - `VoxelHash`：体素键打包（±2^21 每轴）+ 链式哈希表；`VoxelAccumulator` 按点数预分配、质心累加，
    降采样一遍过、不建中间点表。
  - `VoxelGridIndex`：SOR 的近邻网格，持有 `Vector3[]`（避免逐点解字节），3×3×3 凑不够 k 才扩到 5×5×5；
    容量按**实际占用格数**翻倍（估算偏小也不退化）。
  - `PointCloudOps`：`VoxelDownsample` / `RemoveStatisticalOutliers` / `CropBox`，纯函数，接 `IProgress<double>` 与 `CancellationToken`。
    输出一律走 `XyzCloud.Create` 建的新托管数组，不引用 MMF（可脱离原文件存活与导出）。
  - `PcdWriter.WriteBinaryXyz`：只写 float32 xyz binary，4 MB 分片写。
- Core/Grid 新增：`GridMorphology`（`Despeckle` / `Open` / `Close` / `RemoveIslands`）、`PgmWriter.Write`；
  `MapYaml.ToYaml/Save`（`G9` + InvariantCulture，复算出的图能配新 yaml 直接喂导航）。
- 语义决定：抹掉的占用像素填 **未知(205)**，不替用户断言"可通行"；新增像素按 `meta.Negate` 取占用值；
  图外不算"非占用"，否则会把地图边缘整圈削掉。
- **不用开运算做默认去毛刺**：0.05 m/px 下 1 像素宽 ≈ 5 cm 的薄墙/门框是真实结构，r=1 开运算会整条抹掉。
  改用 `Despeckle`（8 邻域占用数 ≤ 阈值才删），并保留 `Open` 供明确需要的场景。
- App：工具栏「优化 ▾」+ `ParameterDialog`（代码搭建的数值输入，校验失败留在框里报错）+ 8 个菜单动作 +
  `RunCloudOpAsync` / `RunGridOpAsync`（后台跑、进度、取消、跑空则放弃替换并提示）+ `ExportAsync`
  （写 `optimized.pcd` / `map.pgm` / `map.yaml`）+ 「撤销全部优化 = 重新加载原包」。
- `PointCloudView.Cloud` 暴露当前云（只读借用，换云即失效），供算子取输入。

### 修掉的两个 O(n²) 退化（本次新代码里）

1. 体素哈希只留 24 位：`hash & (capacity-1)` 在容量涨到 2^26 后，一半以上槽永远用不到 →
   32.46 M 点 0.05 m 降采样 90 s 还没完。改 32 位全值域后 6.8 s。加了分布回归测试。
2. SOR 用包围盒**体积**估网格边长：705×493×37 m 的空心场景估出 2.3 m 格子（每格上千点）→
   16.19 M 点 SOR 140 s 未完成。改按**表面积**估（21 点/m² → 0.22 m），并并行化查询：
   5.41 M 点 k=8 从 71.6 s 降到 7.8 s。

### 实测（Release，8 核，两套样例）

| 算子 | 32.46 M | 16.19 M |
|---|---|---|
| 体素 0.05 m | 30.42 M（93.7%）· 6.8 s | 15.97 M（98.6%）· 3.8 s |
| 体素 0.20 m | 5.41 M（16.7%）· 5.1 s | 2.87 M（17.7%）· 2.7 s |
| Box 裁剪（保中央） | 2.39 M · 0.33 s | 2.14 M · 0.23 s |
| SOR k=8 @0.20 m | 5.41 → 5.05 M（删 6.7%）· 7.8 s | 2.87 → 2.70 M（删 5.8%）· 4.8 s |
| 去毛刺 | 改 12,595 px · 0.39 s | 改 8,648 px · 0.18 s |
| 补洞 r=1 | 改 48,502 px · 1.15 s | 改 22,823 px · 0.89 s |
| 去孤岛 ≥256 px | 删 9,999 块 · 0.18 s | 删 7,033 块 · 0.17 s |

裁剪保留 + 剔除点数与源点数逐点相等，边界不漏不重。明细见 `docs/performance.md`。

### 验证

- `dotnet build MyPcdEditor.slnx -c Debug`：0 错误 0 警告。
- `dotnet test MyPcdEditor.slnx`：Core 62（新增 `PointCloudOpsTests` 14 + `GridMorphologyTests` 11，含哈希分布回归）、App 15 全绿。
- `--selftest` 两套数据均 PASS（渲染路径未受影响）。
- 导出往返：`XyzCloud.Open` 读回点数/字节数与写出前一致。

### 下一步

- 参数预览（先看效果再决定落不落）、撤销栈（当前只有整体重载）、高度裁剪与重采样裁剪。
- 栅格图上手工笔刷修正（M3 之后的第一优先级编辑能力）。

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

### Release 复核（2026-11-21）

- 帧率与 Debug 基本一致：全图 16.7 / `zoom 0.08` 29.5（关 17.7）/ `zoom 0.25` 9.1（关 8.4）/
  16 M 29.0；`zoom 0.08` + 1/4 抽样 47.5。再次证实瓶颈是 GPU 顶点提交。
- 后台分块扫描 Release 只要 132 ms（32 M）/ 56 ms（16 M），Debug 是 1406~1911 ms。
  即"必须后台扫"这个约束主要是 Debug 现象；保留后台实现仍然值得（开发体验 + 慢机器 + 以后并行化）。
- 打开耗时：32 M 总计 2.90 s（上传完成 2.25 s）、16 M 1.12 s（0.67 s），比 Debug 快一倍。
- 两套自检 Release 同样 PASS（exit=0）。

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
