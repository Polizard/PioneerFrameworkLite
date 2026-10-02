using Xunit;

// ⚠ **关闭纯测试的跨 collection 并行**（2026-10-02 实测踩到）：
// `ParallelOps.InParallelRegion` 与 `ForceSerial` 都是**进程级**状态，而批 7 并行化过的纯核心
// （`RendezvousCore` / `TwoImpulse` / `Ladder` 一族）**内部就会调 `ParallelOps.For`**（≥ 阈值即走并行路）
// ⇒ 别的 collection 正在跑它们时，`ParallelOpsTests` 的 `Assert.False(InParallelRegion)` 会**偶发假红**
//（本轮闸门实测：单独跑必过、整轮跑才红 —— 典型的跨 collection 竞态）。
// 修法与 `PioneerFramework.Tests` 一致：整工程串行。代价 = 几秒墙钟，换"闸门不悬"。
[assembly: CollectionBehavior(DisableTestParallelization = true)]
