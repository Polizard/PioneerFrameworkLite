# PFView 极简资产约定

极简模式使用“前缀 + 原文件名”查找运行时资产。默认前缀为 `MIN_`，可在
`先驱框架Lite` 的 Mod 设置中修改。

## 命名

以 X15 为例：

```text
Models/X15/PFL_X15_Hypersonic.obj
Models/X15/MIN_PFL_X15_Hypersonic.obj

Textures/PFView/Ships/X15/X15Albedo.png
Textures/PFView/Ships/X15/MIN_X15Albedo.png
```

规则：

- 前缀只加在文件名最前面，不改目录；
- 目录分隔符和扩展名保持不变；
- 前缀资产缺失或校验失败时，PFView 使用 Box/Cylinder/Sphere 原语；
- 极简模式不会回退到基础高模；
- 极简网格必须是居中的 `1×1×1` Unit OBJ；
- 极简模式默认只使用 albedo，不加载法线贴图；
- 实体、碰撞和物理数据不随视觉变体改变。

## 当前极简资产

| 资产 | 极简 OBJ | 三角面 | Albedo |
|---|---|---:|---:|
| X-15 | `MIN_PFL_X15_Hypersonic.obj` | 922 | 512×512 |
| Crew Dragon | `MIN_PFL_CrewDragon.obj` | 5,148 | 512×512 |
| Soyuz Prism | `MIN_PFL_Soyuz_Prism.obj` | 1,014 | 512×512 |

Crew Dragon 极简版保留基础几何，只缩小贴图并关闭极简模式法线贴图；这避免了对
多组件硬表面做激进 Decimate 后产生退化 UV。

## 游戏内实测

2026-10-25 的 `Player.log` 已确认三套资产均进入极简分支：

```text
[PFView·极简] 资产模式已切换为开启，前缀="MIN_"；旧材质缓存已释放。
[PFView·联盟号] mesh loaded ... variant=minimal
[PFView·X-15] mesh loaded ... variant=minimal
[PFView·龙飞船] mesh loaded ... variant=minimal
[PFView·X-15] asset material ready ... normalMap=off, variant=minimal
[PFView·龙飞船] asset material ready ... normalMap=off, variant=minimal
[PFView·联盟号] asset material ready ... normalMap=off, variant=minimal
```

注意：极简版不保证外形明显变化。当前 X15 仅小幅减面；Crew Dragon 和 Soyuz
保留基础几何，主要收益来自关闭 2K 法线/运行时 RGBA32 法线副本，以及 albedo
降到 512。若需要肉眼明显的轮廓变化，应另行制作更激进的远距离 LOD，而不是把
现有极简资产继续大幅 Decimate。

## 后续建议

- 为数百实例做 GPU Instancing / draw call 合并；
- 将极简 albedo 管线改为 DDS/BC 压缩并实测显存；
- 接入 CapsuleSegment 极简资产；
- 用 GPU Profiler 验证 30MB 常驻显存红线，而不是只按文件体积估算。

## 校验要求

每次新增极简资产后至少检查：

- OBJ 三角面数；
- `1×1×1` 包围盒与 `(0,0,0)` 中心；
- 所有面都有 `vt` 和 `vn`；
- 单一材质槽；
- UV 三角形不出现零面积或反向；
- Albedo 尺寸和文件大小；
- 极简资产存在时，运行时日志不应出现基础资产加载路径。
