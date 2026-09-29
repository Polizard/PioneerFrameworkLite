// ⚠ **本文件不是上游代码**（是我们自己的适配件）。放在这里只为让三个编译
//   `ThirdParty/MechJebLib/**` 的工程（主工程 / MechJebLibTest / PioneerFramework.Pure.Tests）
//   共享**同一份**别名定义，避免三份拷贝各自漂移（J2「一个概念一个权威」）。
//
// 批5 U3（2026-09-28）：`Primitives/V3.cs` 已删除，`V3` 由本别名指向宿主类型 `DVector3`
//   ⇒ 移植面的源码（含上游那 6207 条测试）**一个字都不用改**，而全库的双精度三矢量类型从两个变成一个。
//   撞名的 6 个 NED 船体常量已在 U1' 改名为 `bodyUp/…`（值未动）；世界系那 6 个（`up/down/left/right/forward/back`）
//   是 `DVector3` 的原值 —— 两者在合并后的类型里并存，语义由名字区分。
global using V3 = PioneerFramework.DVector3;
