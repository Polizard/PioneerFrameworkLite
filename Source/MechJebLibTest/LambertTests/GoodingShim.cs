// ⚠ **本文件只存在于测试程序集里**，生产代码（主工程 / RimWorld 那侧）没有这个名字。
//
// 上游的 `MechJebLib/Lambert/Gooding.cs` 我们**故意不移植**——我们那份
// `PioneerFramework.Gooding`（`Base/Orbital/Gooding.cs`）比上游多两个护栏（见该文件注释）。
// 但上游的 `IzzoTests`（**逐字移植、不得就地修改**）要拿 `Gooding` 做交叉验证：
// 它对同一组随机输入同时跑 `Izzo.Solve` 与 `Gooding.Solve`，两者必须同解 ⇒ 这是一条**白拿的硬 oracle**。
//
// ⇒ 这里提供一个**同名同签名的转发壳**，把那 6 处调用接到我们自己的 `Gooding` 上：
// 于是上游那条"两个独立 Lambert 求解器互证"的判据**照样成立**，只是参考解由我们那份带护栏的实现给出。
// 这不是影子类型（J3 禁的）：它**不实现任何数学**，只是 `V3 ↔ DVector3` 的类型转换，
// 且只在测试程序集里存在、与生产代码不共存。

using MechJebLib.Lambert;
using MechJebLib.Primitives;

namespace MechJebLibTest.LambertTests
{
    public static class Gooding
    {
        public static (V3 Vi, V3 Vf) Solve(double mu, V3 r1, V3 r2, double tof,
            TransferGeometry direction = TransferGeometry.ShortWay, int nrev = 0, V3 h = default)
        {
            // 我们的 `Solve` 用 `DVector3? h = null` 表示"未提供"⇒ 零矢量要显式映射成 null
            PioneerFramework.DVector3? hh = h.sqrMagnitude > 0.0
                ? (PioneerFramework.DVector3?)new PioneerFramework.DVector3(h.x, h.y, h.z)
                : null;

            (PioneerFramework.DVector3 vi, PioneerFramework.DVector3 vf) = PioneerFramework.Gooding.Solve(
                mu, new PioneerFramework.DVector3(r1.x, r1.y, r1.z), new PioneerFramework.DVector3(r2.x, r2.y, r2.z),
                tof, direction, nrev, hh);

            return (new V3(vi.x, vi.y, vi.z), new V3(vf.x, vf.y, vf.z));
        }
    }
}
