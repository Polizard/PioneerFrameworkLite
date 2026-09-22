// 这个枚举**不是我们的发明**：上游把它定义在 `MechJebLib/Lambert/Gooding.cs` 里（`MechJebLib.Lambert` 命名空间）。
//
// 为什么单独一个文件：我们**故意不移植上游的 `Gooding.cs`**（我们那份 `PioneerFramework.Gooding`
// 比上游多两个护栏，见该文件注释）⇒ 这个被 `Izzo` 共用的枚举就没了着落。
// 逐字移植的 `Izzo.cs`（在 `ThirdParty/MechJebLib/Lambert/`）要求它出现在**上游的命名空间**里，
// 否则就得去改移植物 ✗（那会破坏"与上游 diff 为空"这条唯一不变式）。
//
// ⇒ 所以：把它按上游的原样放在上游的位置（命名空间），我们自己的 `Gooding` 改为 `using` 它。
// **权威只有一份**（V2）：本文件是 `TransferGeometry` 在全库的唯一定义处。

namespace MechJebLib.Lambert
{
    public enum TransferGeometry
    {
        ShortWay, // absolute: transfer angle in [0, pi]
        LongWay, // absolute: transfer angle in [pi, 2pi]
        Prograde, // relative to v1: arc that goes with v1's angular momentum
        Retrograde // relative to v1: arc that opposes v1's angular momentum
    }
}
