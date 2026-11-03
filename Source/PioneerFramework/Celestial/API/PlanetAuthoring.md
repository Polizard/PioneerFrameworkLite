# 行星参数作者契约

适用 RimWorld 1.7，框架外层存档 v2。入口见 [API](README.md)、[六类模板与测试面板](PlanetKinds.md)。本页对应实际组件消费者；category/tags/bodyType 是开放元数据，能力来自显式组件。省略 atmosphere、environment、surface、biomes、maps 不会补出类地环境、地图或伤害。视觉球体可以存在于无 surface 能力的巨行星。

## 解析、范围和替换

`CelestialDoubleParameterXml` 的固定值为 `<value>123</value>`，范围为 `<min>100</min><max>200</max>`；二者互斥，端点必须有限且 min≤max。整数规格还要求 Int32 整数端点。解析复用原有种子 `(worldSeed, generationPath, compKey, parameterKey)` 与采样器，同输入稳定；每实例只解析一次。实例保存解析后的值、原始配置字节与版本。修改 XML 不重采样旧实例。原始抽样区间属于本次解析元数据，不写入存档；检查窗口的当前 Def 参考与实例解析值分别标明。

原生数字规格字段直接填写 value/min/max。其他新增数字通过所在对象的 `parameters` 列表填写；列表 key 相对于所在对象。创建请求的 `ParameterOverrides` 使用下表完整路径，`CompKey` 选择该组件，如 `motion`、`environment`、`biomes`、`maps`、`clouds`。参数覆盖不提供字符串、布尔、枚举、引用或列表修改。完整有效区间在覆盖后校验；任一端点无效、全区间关系冲突、未知/重复 key 或未消费的覆盖均拒绝。

```xml
<li Class="PioneerFramework.Celestial.CompProperties_ExternalPhysicsBinding">
  <key>motion</key><providerId>PFL.ExistingEphemeris</providerId>
  <meanDensityKilogramsPerCubicMeter><min>4000</min><max>6000</max></meanDensityKilogramsPerCubicMeter>
  <motion>
    <orbitRadiusMeters>120000000</orbitRadiusMeters><orbitPeriodSeconds>58400</orbitPeriodSeconds>
    <frameOriginReference>frameOrigin</frameOriginReference>
    <rotation><periodSeconds>60000</periodSeconds></rotation>
    <parameters><li><key>rotation.phaseRadians</key><value><min>0</min><max>6.28</max></value></li></parameters>
  </motion>
</li>
```

保留无参数的 `CelestialMotionXml.Compile()`；新增 `Compile(CelestialGenerationContext? context)` 在所属物理组件作用域解析，输出相同 `existing.circular.v2` 提供者载荷。固定 v2 字段保持原有结果；v1 不经过新解析。参数列表位于 `motion.parameters`，旋转数字用 `rotation.FIELD` key，覆盖路径用 `motion.rotation.FIELD`。

```csharp
new CelestialParameterOverride("motion", "motion.rotation.phaseRadians", Value: .7);
new CelestialParameterOverride("biomes", "terrain.oceanCoverage", Min: .45, Max: .7);
new CelestialParameterOverride("maps", "diversity.weather.Clear.weight", Value: 3);
```

XML 继承的列表用 `Inherit="False"` 完整替换，例如 `<weather Inherit="False"><li><def>Clear</def><weight>3</weight></li><li><def>Rain</def><weight>1</weight></li></weather>`。系统条目的 `compOverrides` 以及 `CelestialBodyTemplate.WithOverrides` 按稳定组件 key 作类型完整替换，未知 key 仍拒绝，不按列表内容合并。改变可选模块的存在性时显式构造组合：

```csharp
var air = new CompProperties_Atmosphere {
    key = "air", pressurePascals = new() { value = 600 },
    temperatureKelvin = new() { value = 230 }, topAltitude = new() { value = 100000 },
    composition = new() { new() { gas = "Nitrogen", moleFraction = 1 } } };
var composed = CelestialBodyTemplate.Create(template.DefinitionId, template.Label,
    template.Category, template.Tags,
    template.Components.Where(c => c.Descriptor.Key != "air").Append(air.ToDefinition()));
```

调用者仍需同步群落与环境的真空声明。只加物理大气不会修改原生真空群落；只加云图不会获得大气能力。创建、地表请求和地图请求是分别显式执行的步骤。

## 输入与派生物理量

`固定/范围` 表示原生数字规格；`参数` 表示 parameters 与 ParameterOverrides 均可用。表中 shape./air. 表示组件 key，直接规格的 ParameterKey 为裸字段名，如 `radiusMeters`、`gravitationalParameter`、`pressurePascals`。readable运动的 `motion.` 属于 ParameterKey 本身，其 CompKey 通常也叫motion。所有数字都要求有限值。

| 字段/路径 | 默认、单位和有效域 | 角色与实际消费者 | 配置版本 |
| --- | --- | --- | --- |
| shape.radiusMeters，固定/范围 | 5e6 m，>0 | 物理球半径、球面坐标、原生层头；原生显示半径独立 | shape v1 |
| motion.gravitationalParameter，固定/范围 | 省略=null，m³/s²，≥0，沿用旧契约 | 物理绑定/轨道和重力 | binding v1 |
| motion.meanDensityKilogramsPerCubicMeter，固定/范围 | 省略；kg/m³，>0；与直接 GM 互斥 | 用同次 geometry 半径计算 GM；半径/密度区间所有极值组合的 GM 必须有限且>0 | binding v1 |
| 派生 GM、重力、逃逸速度 | G=6.67430e-11；GM=G·4πr³ρ/3；g=GM/r²；v=√(2GM/r) | 物理、气压大气、可选重力属性；检查窗口展示派生量；GM 未知则显示未知 | 不单独新增配置 |
| motion.orbitRadiusMeters / orbitPeriodSeconds，参数 | 0 m / 0 s；≥0，任何正半径要求完整周期区间>0 | 现有圆轨道初始化；覆盖的 ParameterKey 为 `motion.orbitRadiusMeters` / `motion.orbitPeriodSeconds` | initialization v2 |
| motion.orbitPhaseRadians，参数 | 0 rad，有限 | 同提供者轨道相位 | initialization v2 |
| motion.orbitNormalX/Y/Z，参数 | (0,1,0)；完整笛卡尔区间不含零向量，最大平方模有限 | 轨道平面归一化轴 | initialization v2 |
| motion.bodyType / frameOriginReference | Unclassified / 空串，typed string | 开放元数据 / 显式初始化引用 key | initialization v2 |
| motion.rotation.axisX/Y/Z，参数 | (0,1,0)，同轨道轴完整区间规则 | 权威北轴 | initialization v2 |
| motion.rotation.periodSeconds，参数 | 省略；自由自转必须>0 s | 现有提供者的权威自转；物理秒沿用现有每物理 tick 一秒约定 | initialization v2 |
| motion.rotation.phaseRadians，参数 | 0 rad，有限 | 本初子午线相位 | initialization v2 |
| motion.rotation.retrograde / synchronousTargetReference | false / 空串，typed bool/string | 顺逆转或锁定目标；同步模式禁止周期字段/参数/覆盖与 retrograde。目标须在创建引用字典中存在，不能重合或与北轴共线 | initialization v2 |
| air.rho0 / scaleHeight / topAltitude，固定/范围 | 省略0 / 省略0 / 0；kg/m³ / m / m，≥0 | 旧指数阻力 profile；未知组分，保留原模式契约 | air v1 |
| air.pressurePascals / temperatureKelvin / topAltitude，固定/范围 | 无默认 pressure/T；Pa≥0，K>0，top m>0 | 显式 pressure 模式；要求 geometry+physics.binding，正有限重力；完整有效区间角点计算的密度/尺度高度必须有限且H>0 | air v2 |
| air.composition/li.gas / moleFraction | typed 已注册字符串 / 固定[0,1]，gas唯一；总和误差≤1e-12 | 冻结摩尔质量，ρ=PM/RT、H=RT/Mg，指数气压/O2/CO2；不自动归一化。pressure 模式禁止 rho0/scaleHeight | air v2 |

内建气体：Nitrogen、Oxygen、CarbonDioxide、Argon、Hydrogen、Helium、Methane、WaterVapor。扩展在新实例解析前调用 `CelestialAtmosphereGasRegistry.Global.Register(stableId, molarMassKilogramsPerMole)`；重复/非法 ID、非正或非有限质量拒绝。旧实例从冻结质量恢复，不依赖当前注册表。高度<0取海平面，高度≥top为零压力/密度；没有大气、仅有 v1 密度大气时组分保持未知。

## 环境、暴露与室内保护

`CelestialEnvironmentProfileDef` 的 `dayLengthTicks=60000`、`primaryLightReference="primaryLight"`、`vacuumBiomes=[]` 保留原字段；dayLength 必须能表示为正有限 float。添加 climate 或 exposure 显式启用 environment 配置 v2，省略两者保留 v1 字节和原生日历。下列数字通过 `climate.FIELD` / `exposure.FIELD` 参数路径覆盖；曲线、模式和布尔保持 typed。

| climate 字段 | 默认；支持域/单位 | 消费者 |
| --- | --- | --- |
| mode | Authored；Authored / CommittedIllumination | 温度模式选择。存在物理绑定的显式 climate 在两模式下均以同一已提交快照取得光照 |
| meanTemperatureCelsius | 15；[-273.15,10000] °C | 原生当前地表/季节温度 |
| latitudeCurve/li.latitudeDegrees / offsetCelsius | (0,0),(90,-40)；严格递增[0,90]绝对纬度，偏移[-10000,10000] °C；至少2点 | 纬度插值与端点夹取 |
| lapseCelsiusPerMeter | .0065；[-1,1] °C/m | 海拔偏移 |
| diurnalAmplitudeCelsius / seasonalAmplitudeCelsius | 0 / 0；[0,10000] °C | 日/季节周期，有限解析热响应 |
| yearLengthTicks | 3600000；[1e-6,1e20] | Authored用显示tick，CommittedIllumination用已提交物理秒的既有周期口径 |
| thermalResponseSeconds | 0；[0,1e12] s | 有界解析相位响应；零响应直接取谐波，大频率用稳定倒数表达式 |
| illuminationTemperatureCelsius | 50；[0,10000] °C | 已提交方向光加热 |
| lightMultiplier | 1；[0,1e12] | 光强/加热；原生光照输出夹到[0,1] |
| windSpeed | 1；[0,1e6]原生强度 | 活跃大气才有风；进入原生WindManager基准，天气/条件/风源修正仍保留 |
| rainfallMillimeters / humidity | 1000 / .5；[0,1e6/1.5] mm / [0,1] | 地表湿度/群落；地形湿度可乘(.5+moisture)，原生雨量写入保持有限 |

查询 `SampleSurface(new CelestialSurfaceEnvironmentQuery(displayTicks, longitudeDegrees, latitudeDegrees, altitudeMeters, biomeId))` 的全部数值必须有限，纬度[-90,90]；暴露单独开启、没有气候/大气的实例也检查这些条件。原生桥按实际层头的 instanceId 查询，查询不会创建地表或地图。结果的 nullable 压强/O2/温度/重力表示未知；不得填入类地默认值。坐标、群落、显示时间和已提交物理时刻应与样本一起记录。

| exposure 字段 | 默认；支持域 | 消费者 |
| --- | --- | --- |
| minimumOxygenPascals | 16000；(0,1e12] Pa | 缺氧阈值 |
| minimumPressurePascals / maximumPressurePascals | 20000 / 300000；[0,1e12] / (0,1e12] Pa；整个低阈值区间严格低于高阈值区间 | 低/高压独立剂量 |
| hypoxiaPerDay / pressurePerDay | 2 / 2；[0,1e12]严重度/60000游戏tick | 原生缺氧/压力健康效果 |
| toxicityPerDay / radiationPerDay | 0 / 0；[0,1e12]严重度/60000游戏tick | ToxicBuildup / 辐射效果；CO2超过阈值还增加毒性 |
| carbonDioxideThresholdPascals | 1000；(0,1e12] Pa | CO2毒性比例阈值 |
| gravityStats | false；bool，开启要求物理绑定 | MoveSpeed/CarryingCapacity，各有界且只应用一次 |

生物/机械体分支、供氧、低压、高压、毒性与辐射抗性独立。原生 StatWorker 聚合装备/基因；VacuumResistance 不提供高压/毒性/辐射抗性。真实 Odyssey 真空的缺氧/低压伤害与免疫归原生系统，补充模型不重复施加。无 Odyssey 时走补充模型。房间温度、热设备与室内真空仍由原生维护。

现有 `PF_AtmosphereRegulator` 使用原生电力/开关/故障，供氧/调压渐近到21200/101325 Pa；屋顶本身不提供安全空气。原生房间成员在分裂合并时按格子体积重映射；开放房间2秒交换、失能封闭房间120秒交换。滤毒采用有界游戏近似：每秒移除 `ToxinConditioningPerSecond` 个 toxicityPerDay 单位，负值截到精确0，速率按设备与房间大小折算。足额工作的密封过滤可到达严格安全；不足能力、断电或破口重新危险。没有数值 epsilon、安全阈值放宽或额外室内状态。辐射不由设备过滤。原生安全目的地与警报复用同一 `IsDangerous`；间隔先认领再伤害，重试不重复剂量。

## 地形、地图、候选与外观

`CelestialBiomeProfileDef.mode=Parameterized` 需要显式 terrain 与一个无条件 fallback；组件设置 `useEnvironmentClimate=true`，绑定同次冻结的 environment climate。旧 LatitudeBands/VanillaSurface 和旧实例保留。`terrain` 启用 biomes v2，`diversity` 启用 maps v2；surface 配置仍v1，环境/空气/视觉组件状态schema均保持原版本1。

| 字段组 | 默认、域、覆盖路径 | 实际消费者 |
| --- | --- | --- |
| terrain.continentScale | 2，[.01,64] | 球面连续方向场 |
| terrain.elevationAmplitudeMeters / elevationOffsetMeters | 3000 / 0，[0,100000] / [-100000,100000] m | 原生tile物理高度与地形 |
| terrain.roughness / mountainWeight | .35 / .5，[0,1] | 起伏/原生hilliness |
| terrain.seaLevelMeters 或 oceanCoverage | 均省略；[-200000,200000] m 或[0,1]；二者互斥 | 海平面或稳定高度排名选海洋；覆盖率按tile数取整 |
| terrain.allowRivers / allowLakes / oceanBiome | false / false / Ocean，typed | 开启水步骤要求原生Ocean；河流另要求整个温度/海拔区间处于[-100,100]°C，不能仅检查抽样值 |
| terrain.biomeRules/li.key / biome / priority / fallback / weight | 必需稳定key/原生BiomeDef，0/false/1；权重(0,1e6] | 同优先级可用群落加权；一个fallback；weight与以下条件均用`terrain.biomeRules.KEY.FIELD` |
| minimum/maximumTemperatureCelsius | -273.15 /10000，[-273.15,10000] °C | 包含边界的筛选 |
| minimum/maximumRainfallMillimeters | 0 /1e6，[0,1e6] mm | 同上 |
| minimum/maximumElevationMeters | -200000 /200000，[-200000,200000] m | 同上 |
| minimum/maximumAbsoluteLatitude | 0 /90，[0,90]度 | 同上 |
| minimum/maximumRoughness | 0 /1，[0,1] | 同上；完整min/max区间保持有序；fallback条件须完整无条件 |
| terrain.landmarks/li.mutator / weight | typed现有TileMutatorDef+worker /1，(0,1e6]；`terrain.landmarks.DEFNAME.weight` | 原生Mutators/Landmarks步骤；DLC专用引用需MayRequire |
| diversity.mineralAbundance / soilFertility / plantDensity / animalDensity | 1，[0,10]；`diversity.FIELD` | 原生矿物数量/有效土壤肥力/野生植物与动物生成 |
| diversity.geologyVariation / allowCaves | 0，[0,1] / true bool | 共同地质场因子`1+variation*(2*Geology-1)`；洞穴许可本身不产生洞穴 |
| diversity.rocks / minerals / plants | 空列表；typed ThingDef+weight | 非资源天然岩 / 可开采建筑 / Plant；原生岩体、矿物、植物候选 |
| diversity.soils / animals | 空列表；typed TerrainDef / PawnKindDef+weight | 非水非岩有限肥力土壤 / 原生Animal种族候选 |
| diversity.weather / conditions / incidents | 空列表；typed WeatherDef / GameConditionDef / IncidentDef+weight | 原生天气与事件候选；条件事件、海岸事件的原生适用性仍检查；无适用项保留原生回退规则 |

各候选列表的 ID 唯一、默认weight=1，权重(0,1e6]，覆盖`diversity.GROUP.DEFNAME.weight`；空列表保留原生候选。共同字段来自同一冻结实例与tile上下文，不按共享Def或tile号共用状态。不新增资源、植物、动物或事件Def。

外观 profile 的 `visualVersion=2` 与 `appearance` 必须成对。v1和原11参数公开构造函数保留。每个视觉组件独立以 `appearance.FIELD` 覆盖数字，列表位于profile的appearance.parameters；style是typed枚举。

| 字段 | 默认/支持域 | 消费者 |
| --- | --- | --- |
| appearance.style | Descriptive；GasBands仅Surface slot | 既有PFView球体材质；不授予surface能力 |
| red/green/blue / alpha | profile.color对应通道，通常1；[0,1] | RGB调色；alpha仅Clouds/Atmosphere，Surface须不透明1 |
| shellHeightMeters | 0，[0,1e9] m | Clouds/Atmosphere壳；Surface须0 |
| fadeRangeMeters | 5000，[1e-6,1e9] m | 仅Clouds高度淡出 |
| driftPeriodTicks / distortionPeriodTicks | 600000 /6000，[1,1e15] | 仅Clouds漂移/噪声时间 |
| cloudCoverage | 1，[0,1] | 仅Clouds程序云图贡献乘数，非保证地理面积比例 |
| bandFrequency / bandContrast | 12 /.25，[1,128]纬度周期 /[0,1] | 仅GasBands；原生shader参数 |

以上 appearance 数字无前缀的XML标量 fallback 位于appearance或profile原字段；非活跃参数列表/覆盖拒绝，即使与默认相同。Surface不支持v2 texture/biome，Atmosphere不支持texture/biome，Clouds可用原World/CloudMap；v2 backgroundBiome拒绝。Stellar保留v1。已生成地表patch优先，远景无patch时用描述/条带球体。缓存按世界会话/实例/描述修订/层/配置与patch修订失效；不连带其他实例，不隐式建层。

## 兼容与人工验收

默认太阳/地球/月球的定义与共享profile输入保持原样。六个可选模板显式升级；历史实例、地图及其v1/v2配置从原字节恢复。外层存档仍v2；v1框架测试世界的原有拒绝规则保留。旧v1 motion/air/visual/environment/biomes/maps解码和旧公开签名保留；不修改冻结fixtures。所有DLC专有Def用MayRequire/原生条件patch；没有Odyssey不阻止加载。仅原生支持的温度、天气、候选、群落和地图生成行为承接结果。

以下各项当前均 **UNCHECKED（需要真实游戏观察）**：

- [ ] 荒芜无气与ThinAir：真空分支、缺氧、风、可登陆地图。
- [ ] 温带Ocean/Arid：海陆比例、生态/矿物/天气差异，同Def双实例及同号tile隔离。
- [ ] 温室：高温/高压/CO2危险，防护装备/机械体和原生伤害各通道。
- [ ] 冰封无气/Atmospheric：冰原与露岩、非真空氮气仍缺氧。
- [ ] Synchronous：朝向目标、倾轴/逆转、光照与太阳能、暂停/快进。
- [ ] 两巨行星：实际条带/云/大气壳；地表/地图按钮不可用。
- [ ] 原生调节器密封滤毒房间安全、失能/开门/破顶危险，警报与避险目的地；Odyssey真空无重复伤害。
- [ ] 面板文字/滚动/关闭重开、pending继续、GUI状态；检查不生成层/地图。
- [ ] 保存重载与XML修改后旧实例不变；移除/缓存重开不影响其他实例。
- [ ] GPU外观/资源释放、完整地图生成、实际设备感受及有/无Odyssey整程。扩展性能矩阵与新benchmark本轮未做。
