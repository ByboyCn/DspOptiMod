# DspOptiMod

戴森球程序 BepInEx 优化 mod（net472，Harmony 补丁）。

## 功能

### 1. 戴森云渲染优化（Swarm）
- 原版 `DysonSwarm.DrawPost()` 对每个太阳帆做程序化绘制（`sailCursor * 12` 个四边形顶点，10 万帆时开销巨大）。
- 本 mod 整段替换远景渲染：**每条轨道只画一条半透明发光环带**（GL 立即模式 + `Hidden/Internal-Colored`），
  颜色取轨道自带的 `orbitColorsHSVA`，透明度随帆数增长。
- 发射中的太阳帆子弹（轨迹光点）仍按原版渲染，发射过程可见。
- 近处（2500m 内）帆的精细网格默认保留，可在配置里 `DisableNearPass=true` 一并关闭。

### 2. 小火箭建造顺序（Rocket）
- 原版 `DysonNode.ConstructSp()` 把火箭送来的结构点**先填节点本体，再分给框架**。
- 本 mod 反转优先级：**先把该节点连接的所有框架填满，框架全满后才建节点本体**。
- 火箭目标选择（`PickAutoNode`/`spReqOrder`）不需要改，它统计的是"节点+框架"总需求，天然兼容。

## 配置（BepInEx/config/byboy.dspopti.cfg）
| 键 | 默认 | 说明 |
|---|---|---|
| Swarm.OptimizeSwarm | true | 开启戴森云环带渲染 |
| Swarm.DisableNearPass | false | 连近处帆精细渲染也关掉 |
| Swarm.RingSegments | 128 | 环带分段数 |
| Swarm.RingWidthFactor | 0.025 | 环带宽度系数（×轨道半径）|
| Swarm.RingMinAlpha | 0.25 | 环带最低透明度 |
| Swarm.RingAlphaScale | 0.6 | 环带基础透明度 |
| Rocket.FramesFirst | true | 框架优先建造 |

游戏内 **F9** 可即时开关戴森云渲染优化。

## 构建
```
dotnet build -m:1 -nodeReuse:false -c Release
```
构建后自动复制到游戏 `BepInEx/plugins/`（可用 `-p:GameDir=...` 覆盖游戏目录）。

`ref/` 下是反编译参考代码（ilspycmd 导出），仅供查阅，不参与编译。
