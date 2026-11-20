# 进度留痕

按时间倒序或正序追加，一条一个动作，写清楚「做了什么 / 卡在哪 / 下一步」。

---

## 2026-11-20

### 完成

- 需求对齐：机器狗巡检 PCD + PGM 查看/优化/合并小工具。
- 技术栈定稿：`net11.0` + `win-x64` + `Avalonia 12.1.6` + `Silk.NET.OpenGLES 2.23.0`（ANGLE）。
- 验证关键假设（真实环境跑过，非推断）：
  - `Avalonia 12.1.6` 仍含 `OpenGlControlBase`，`GlInterface.GetProcAddress(string) -> IntPtr` 签名未变。
  - `Silk.NET.OpenGLES` 类型为 `Silk.NET.OpenGLES.GL`，存在 `GetApi(Func<string,nint>)` 重载，可与 Avalonia 直接对接。
  - `Avalonia 12.1.6` 资产只到 `net10.0`，net11 项目可消费。
- 阅读 `F:\github\MilkAvalon` 参考实现，锁定 6 个可移植要点（见 `docs/architecture.md` §6）。
- 实测两份样例数据，结论写入 `docs/data-format.md`：PCD 仅 `x y z`/binary、16.19M 与 32.46M 点、PGM 6811×4503、yaml 的 `image` 为绝对 Linux 路径。
- 文档初始化：`README.md` + `docs/{plan,architecture,data-format,decisions,performance,progress}.md`。

### 待办 / 未完成

- **样例数据复制未执行**：原复制命令被取消，`testdata/` 仍为空。需要把
  `F:\code\pcdview1\map-20261116-180314` 与 `...-210819` 复制到 `testdata/`（约 620 MB，`testdata/` 已在 `.gitignore` 中排除）。
- M0 未开始：工程骨架、`Directory.Build.props`、项目分层尚未创建。

### 下一步

1. 复制样例数据到 `testdata/`。
2. M0：建 sln 与四个项目，移植 `GlControlBase`/`GlInfo`/`Shader`，跑通 ANGLE 打印 `GLES 3.x`。
3. M1：PCD/PGM/yaml 只读 IO + `MapPackage`，用真实数据写单元测试。
