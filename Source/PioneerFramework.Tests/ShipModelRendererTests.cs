using UnityEngine;
using Xunit;

namespace PioneerFramework.Tests
{
    /// <summary>
    /// D-1「船模型绘制」静态段（`ShipModelRenderer`）的**纯用例钉子**（2026-10-11）。
    ///
    /// 判据（重构验收的另一半 = 进游戏看 PFView 观感照旧）：零件盒体的**组装口径** ——
    ///   ① 位置 = 视图系位置 + 姿态 ×（局部偏移 × metersToView × scale）；
    ///   ② 尺寸 = 盒体尺寸 × metersToView × scale（各轴下限 0.1 视图单位，边界用严格小于）；
    ///   ③ "放大部件"（scale）对**偏移与尺寸同倍**放大。
    /// 这是"零行为变化"重构 ⇒ 能做精确相等的地方**不给容差**（换乘法结合顺序/搬错一个系数都会红）。
    ///
    /// ⚠ headless 纪律：只用 Vector3/Quaternion 的**构造与算术**（`new Quaternion(x,y,z,w)` 直接填分量、
    /// `q * v`、分量读写）。`Quaternion.Euler/LookRotation/AngleAxis` 这类 ECall 在 headless 一调即抛
    /// （渲染线交接 §4 第 2 条），**`Matrix4x4.TRS` 同族**（2026-10-11 本文件实测）⇒ 四元数选
    /// **精确可表示**的（180° 绕 Y = (0,1,0,0)），矩阵构造本身不断言（见文件末注释）；
    /// 断言消息里也不许塞 `Vector3`（`ToString` 要 SharedInternals 模块 ⇒ 冤红）。
    /// </summary>
    public class ShipModelRendererTests
    {
        const float M2V = 0.1f;   // PFView 的 MetersToView（1 m = 0.1 视图单位）

        /// <summary>绕 Y 轴 180°：q = (0, 1, 0, 0) —— 单位长且精确可表示，把 +X 轴映射到 −X 轴。</summary>
        static readonly Quaternion Yaw180 = new Quaternion(0f, 1f, 0f, 0f);

        static void Probe(Vector3 viewPos, Quaternion rot, DVector3 offsetShip, DVector3 sizeShip, float scale,
            out Vector3 pos, out Vector3 size)
        {
            ShipModelRenderer.PartViewTransform(viewPos, rot, offsetShip, sizeShip, M2V, scale,
                out pos, out size);
        }

        [Fact]
        public void PartView_IdentityRotation_IsPivotPlusScaledOffset()
        {
            Probe(new Vector3(1f, 2f, 3f), Quaternion.identity,
                new DVector3(10, 0, 0), new DVector3(4, 2, 1), 1f, out Vector3 pos, out Vector3 size);

            // 位置 = 枢轴 + 偏移×0.1×1 ⇒ (1+1, 2, 3)。这三个数在 float 下是精确值（10×0.1 = 1 精确）。
            Assert.Equal(2f, pos.x);
            Assert.Equal(2f, pos.y);
            Assert.Equal(3f, pos.z);

            // 尺寸 = 盒体×0.1×1 ⇒ (0.4, 0.2, 0.1)（4/2/1 乘 0.1f 全部精确）
            Assert.Equal(0.4f, size.x);
            Assert.Equal(0.2f, size.y);
            Assert.Equal(0.1f, size.z);
        }

        [Fact]
        public void PartView_RotatesTheOffset_ButNotTheSize()
        {
            Probe(Vector3.zero, Yaw180,
                new DVector3(10, 0, 0), new DVector3(4, 2, 1), 1f, out Vector3 pos, out Vector3 size);

            // 偏移 (10,0,0)m × 0.1 = (1,0,0) 视图单位；绕 Y 180° ⇒ (−1, 0, 0)
            Assert.Equal(-1f, pos.x);
            Assert.Equal(0f, pos.y);
            Assert.Equal(0f, pos.z);

            // 尺寸是 TRS 的**缩放**、不是要被姿态旋转的向量 ⇒ 仍是轴对齐的 (0.4, 0.2, 0.1)
            // （若实现把 sv 也乘了姿态，这里的 x/z 会变成 −0.4 / −0.1 ⇒ 本断言会红）
            Assert.Equal(0.4f, size.x);
            Assert.Equal(0.2f, size.y);
            Assert.Equal(0.1f, size.z);
        }

        [Fact]
        public void PartView_Scale_MultipliesOffsetAndSize_Together()
        {
            // "放大部件"（debugScaleParts = 200）对偏移与尺寸**同倍**放大
            Probe(Vector3.zero, Quaternion.identity,
                new DVector3(10, 0, 0), new DVector3(4, 2, 1), 200f, out Vector3 pos, out Vector3 size);

            Assert.Equal(200f, pos.x);   // 10 × 0.1 × 200
            Assert.Equal(80f, size.x);   // 4 × 0.1 × 200
            Assert.Equal(40f, size.y);
            Assert.Equal(20f, size.z);
        }

        [Fact]
        public void PartView_MinClamp_IsPerAxis_AndBoundaryIsNotClamped()
        {
            // 各轴独立：小的夹、大的不动（0.5 m ⇒ 0.05 视图单位 < 0.1 ⇒ 夹到 0.1）
            Probe(Vector3.zero, Quaternion.identity,
                DVector3.zero, new DVector3(0.5, 4, 0.5), 1f, out _, out Vector3 mixed);
            Assert.Equal(0.1f, mixed.x);
            Assert.Equal(0.4f, mixed.y);
            Assert.Equal(0.1f, mixed.z);

            // 恰好落在边界（1 m × 0.1 = 0.1）：判据是"严格小于"⇒ 不被再夹（0.9 m ⇒ 0.09 ⇒ 夹到 0.1）
            Probe(Vector3.zero, Quaternion.identity,
                DVector3.zero, new DVector3(1, 2, 0.9), 1f, out _, out Vector3 edge);
            Assert.Equal(0.1f, edge.x);
            Assert.Equal(0.2f, edge.y);
            Assert.Equal(0.1f, edge.z);
        }

        [Fact]
        public void PartView_ZeroOffset_KeepsPivot_AndStillScales()
        {
            // 退化输入：局部偏移为 0 ⇒ 位置就是视图系位置本身（不许加出 NaN/位移）；尺寸照常换算
            Probe(new Vector3(5f, -2f, 7f), Yaw180,
                DVector3.zero, new DVector3(2, 2, 2), 1f, out Vector3 pos, out Vector3 size);

            Assert.Equal(5f, pos.x);
            Assert.Equal(-2f, pos.y);
            Assert.Equal(7f, pos.z);
            Assert.Equal(0.2f, size.x);
            Assert.Equal(0.2f, size.y);
            Assert.Equal(0.2f, size.z);
        }

        // ⚠ 为什么这里不直接断言 `PartMatrix` 的平移列/列长：**Unity 的 `Matrix4x4.TRS` 是 ECall**
        //   —— headless 一调即抛 `SecurityException`（2026-10-11 实测，栈顶 = `Matrix4x4.TRS`；
        //   与 `Quaternion.Euler/LookRotation` 同族。这条已登记进渲染线交接 §4 的 ECall 清单）。
        //   ⇒ 矩阵构造本身只能在游戏里跑；**纯算术的接缝 = 上面钉的 `PartViewTransform`（位置 + 尺寸）**，
        //   `PartMatrix` 只是 `TRS(那两个读数)` 的一行包装（游戏内自会生效，观感验收 = PFView 照旧）。
    }
}
