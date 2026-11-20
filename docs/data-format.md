# 数据格式与解析约定

## 1. 实测样例（2026-11-16 两趟巡检）

| 项 | `map-20261116-180314` | `map-20261116-210819` |
|---|---|---|
| PCD 体积 | 185.32 MB | 371.49 MB |
| PCD 点数 | 16,193,746 | 32,460,950 |
| PCD 字段 | `FIELDS x y z`，`TYPE F F F`，`SIZE 4 4 4`，`COUNT 1 1 1`，`HEIGHT 1`，`DATA binary` | 同左 |
| PCD 头部长度 | 178 字节 | 178 字节 |
| 坐标范围（抽样 20 万点） | x −268.12~455.94，y −265.27~186.41，z −1.84~35.03 | x −100.37~599.49，y −30.23~459.65，z −0.94~36.22 |
| PGM | `P5`，6811×4503，maxval 255（30.67 M px，29.25 MB） | `P5`，6894×4751，maxval 255（32.76 M px，31.24 MB） |
| yaml `resolution` | 0.1 | 0.1 |
| yaml `origin` | `[-275.9, -259.0, 0.00]` | `[-107.6, -32.4, 0.00]` |
| `.projected_origin` | `[342607.96, 3432976.78, 14.06]`，`zone: 21` | 同格式 |

## 2. PCD

### 2.1 头部解析

按规范逐字段解析，**不硬编码 xyzrgb**：

- `FIELDS` × `COUNT` 展开成列，与 `SIZE`、`TYPE`（`F`/`I`/`U`）一一对应
- `WIDTH`/`HEIGHT`/`POINTS`：`POINTS` 为准，`HEIGHT=1` 时视为无序点云
- `DATA`：`ascii` / `binary` / `binary_compressed`

### 2.2 三种 DATA

| 类型 | 处理 |
|---|---|
| `ascii` | 逐行 `split`，按列类型 `Parse` |
| `binary` | 行宽 = Σ(`SIZE`×`COUNT`)，按列偏移 `BitConverter` 读取 |
| `binary_compressed` | 先读 `uint32 compressedSize`、`uint32 uncompressedSize`，再 **LZF 解压**；解压后是 SOA 布局（每列连续），需重排成 AOS |

> LZF 解压器自己实现（约 60 行），不引第三方包。

### 2.3 零拷贝快路径

当且仅当满足：`FIELDS` 恰为 `x y z`、`SIZE` 全 4、`TYPE` 全 `F`、`COUNT` 全 1、`DATA binary`、`HEIGHT 1`。

此时数据区就是连续的 `float32 xyz`，与 GPU 顶点布局一致：
`PcdReader.TryOpenRawXyz(path, out view, out pointCount)` 返回 MMF 视图，直接 `glBufferData`/`BufferSubData`，**不构造托管数组**。样例数据正好命中该路径。

### 2.4 内存组织

命中快路径 → MMF；否则 SOA（`float[] X, Y, Z` + 可选 `Intensity` / `Rgb`）。
SOA 是为了后续体素化、ICP 的 SIMD 友好；查看阶段不做重排。

## 3. PGM

- `P2`（ASCII）/ `P5`（binary），`maxval` 可能是 15 或 255
- 头部 token 间可能是**一个或多个空白**（实测为单个空格），按规范允许 `#` 注释行
- 数据从头部 token 结束后的**单个空白字符之后**开始，注意不要多吃字节
- 存储：单通道 `byte[]`，**不转 RGBA**

## 4. yaml（ROS map_server 格式）

```yaml
image: /home/ysc/jy_cog/system/maps/map-20261116-180314/jueying.pgm   # ← 绝对 Linux 路径，必须忽略
resolution: 0.100000
origin: [-275.9, -259.0, 0.00]
negate: 0
occupied_thresh: 0.65
free_thresh: 0.196
```

约定：

- `image` **忽略**，按 yaml 同目录、同名 stem 找 `.pgm`
- `origin` 是地图**左下角**在世界系的位置（`x, y, yaw`）；图像行号向下增长，世界 y 向上，转换时 `worldY = origin.y + (height - row - 0.5) * resolution`
- `negate: 1` 时像素语义反转（ROS 会做 `255 - p`），解析时统一转成内部语义：`0 未知 / 100 占用 / 100 以下空闲`（内部另存原始灰度以便无损回写）
- 阈值（`occupied_thresh`/`free_thresh`）用于从灰度算概率：`occ = (255 - p) / 255`（`negate=0`）

## 5. toml / projected_origin

```toml
# jueying_id_map.toml
path = "/home/ysc/jy_cog/system/maps/map-20261116-180314/"
[[grids]]
id = 0
name = "jueying"
```

```
# .projected_origin
origin: [342607.9584009703, 3432976.7766647968, 14.062799999956042]
zone: 21
```

- `projected_origin` = 地图局部原点在 UTM（zone 21）中的绝对坐标
- 本轮**只读入模型不参与逻辑**；后续多图合并用它把各包对齐到同一 UTM 系，避免 ICP
- toml 里 `grids` 支持一包多图（`id` + `name`），当前实现按单图处理但保留结构

## 6. 点云与栅格图的坐标系关系（实测结论）

样例中 `yaml.origin` 与 PCD 最小角基本吻合（y 方向误差 < 10 m），说明二者**同一局部坐标系**，可直接叠加显示：

- 栅格覆盖范围 = `origin ± (width|height) × resolution`
- 样例：栅格到 x≈405 就截止，点云到 456 → **栅格只是局部裁片**，叠加显示必须按栅格范围裁剪，不能当成完整地图
- 叠加时不要对点云做任何变换（这是同一坐标系，不是两个坐标系）
