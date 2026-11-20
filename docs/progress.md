# 进度留痕

按时间倒序或正序追加，一条一个动作，写清楚「做了什么 / 卡在哪 / 下一步」。

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
