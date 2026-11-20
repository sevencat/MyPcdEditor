# 开发计划

## 1. 产品定位

面向机器狗巡检场景的桌面小工具，处理巡检产物中成对出现的两类文件：

- **PCD**：SLAM 建出的三维点云（场景几何、障碍物、立面）
- **PGM + yaml**：同一区域的二维栅格地图（导航用 occupancy grid）

三轮能力递进：

1. **查看**（当前）：点云与栅格地图同屏对照，快速判断地图质量。
2. **优化**：点云降采样/去噪/裁剪；栅格图去毛刺、补洞、去孤岛。
3. **合并**：多趟巡检的点云与栅格图合成一份（优先用 `.projected_origin` 的 UTM 锚点对齐，而非 ICP）。

## 2. 第一轮范围（只做查看）

### In

- 打开「地图包目录」（PCD + PGM + yaml + toml + projected_origin）或单个文件
- PCD 读取：`ascii` / `binary` / `binary_compressed`（LZF），含通用字段表
- 3D 点云渲染：轨道相机、高度色带 / 单色、点大小、圆形点、抽样点一键适配
- PGM(P5/P2) + yaml 的 2D 只读查看：缩放、平移、像素→世界坐标读数
- 2D 面板与 3D 视图并排，点云按世界 XY 投影叠加到栅格上（开关）
- 栅格图铺进 3D 场景当地面（开关），用来核对点云与地图的贴合
- 状态栏：点数、FPS、GL 渲染器、包围盒

### Out（本轮明确不做）

- SLAM 位姿读入、ICP/NDT 配准
- 点云合并、栅格图拼接
- 编辑（笔刷/补洞/裁剪落盘）
- 跨平台（Linux/macOS）
- 命令行批处理

## 3. 里程碑

### M0 工程骨架与 GL 打通（0.5 d）

- `net11.0` + `win-x64`，`Directory.Build.props` 统一配置
- 项目分层落地（Core / Rendering.Gles3 / App / Tests）
- 从 MilkAvalon 移植 `GlControlBase`、`GlInfo`、`Shader`（保留出处注释）
- 交付：窗口内 GL 渐变三角正常，控制台打印 `OpenGL ES 3.x` / Renderer / 扩展表

### M1 只读 IO（1.5 d）

- `PcdReader`：头解析（`FIELDS/SIZE/TYPE/COUNT` 通用展开）+ 三种 DATA
- `PcdReader.TryOpenRawXyz`：`x y z`/`float32`/`binary` 的零拷贝快路径（返回 MMF 视图 + 点数）
- `PgmReader`：P2/P5，头部空白容错
- `MapYaml`：`resolution/origin/negate/*_thresh`，`image` 字段忽略
- `MapPackage`：目录级聚合 + `projected_origin` / `jueying_id_map.toml`
- 交付：两份样例数据可读，单元测试对拍通过

### M2 查看器（3 d）

- `PointCloudView : GlControlBase`：分片上传（64 MB/片）、12 B/点、单次 DrawArrays
- 着色：高度 turbo 色带 / 单色；`gl_PointCoord` 圆形裁剪
- `OrbitCamera`：默认斜 45° 俯视、左键微调、右键平移、滚轮缩放、`F` 适配、`R` 复位
- `MapView`：WriteableBitmap 视口采样（缩小时按覆盖范围 max-pooling，不整图转 RGBA）+ 点云 XY 叠加
- 交付：16M / 32M 点数据流畅旋转，状态栏数据正确
- 地面纹理（M2c，已补做）：PGM 降采样成单张 2048 级 RGBA8 纹理铺在点云下方，
  墙线与点云墙体直接对位；实测 215 ms 构建、8 MB 显存、对帧率无影响（见 ADR-010）
- 抽样降载（M2d，已补做）：索引抽样 1/2·1/4·1/8，32 M 点 17.7 → 43 FPS（见 ADR-011）
- 未做（可选后续）：分块 + 视锥剔除，让 32 M 点在全量档也流畅

### 收尾

- `docs/performance.md` 记录首轮基准
- `docs/progress.md` 留痕

**合计约 5.5 个工作日。**

## 4. 后续阶段（概要，待细化）

### M3 优化算子

- 点云：体素降采样、SOR/ROR 去噪、Box/高度裁剪
- 栅格：形态学开闭、连通域去孤岛、孔洞填充、重采样裁剪
- 交互：参数面板 + 预览 + 可撤销（命令栈）

### M4 合并

- 点云：同源坐标系直拼 + 体素去重；跨区包按 `projected_origin` + UTM zone 做刚体变换后拼接
- 栅格：多图按 origin 放置 + 冲突优先级（占用 > 空闲 > 未知）
- 兜底：无锚点时提供手工对齐（两点/两线）

## 5. 风险

| 风险 | 影响 | 处置 |
|---|---|---|
| ANGLE 的 GLES 实现与桌面 GL 行为差异 | 着色器/点大小不符预期 | 只用 ES3 核心特性；`GlInfo` 打印能力表 |
| 32M 点单次上传卡顿 | 打开时 UI 冻结 | 分片上传 + 进度条；必要时先降采样预览 |
| 大 PGM（30M px）内存 | 整图 RGBA 会到 120 MB+ | 保持单通道 + 视口采样 |
| yaml `image` 为绝对路径 | 加载失败 | 按同目录 stem 匹配，忽略该字段 |
| Avalonia 包版本混用 | `TypeLoadException` | 所有 `Avalonia.*` 锁定同一版本号 |
