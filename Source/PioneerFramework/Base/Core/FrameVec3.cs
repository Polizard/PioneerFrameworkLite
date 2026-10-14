using System;

namespace PioneerFramework
{
    /// <summary>
    /// **系载体（薄 struct）**：给同一个 `DVector3` 贴上"这在哪一个系里"的类型标签。
    ///
    /// ## 为什么有它（§11.3 Stage 3「类型承载系」）
    /// `DVector3` 同时扛着**系统系**（根行星中心、根钉住）· **天体局部系**（相对中心天体）·
    /// **适配层系**三个含义，而**编译器零帮助** —— 本库 6 次混系事故全出自这一类
    /// （最典型的是 `GetBodyWorldPosition`=绝对 vs `GetBodyWorldOffset`=系统"差一个词、系正好相反"）。
    /// ⇒ 把"系"放进**类型**：同一个系的量才能直接做运算，**跨系必须显式**（转换函数/口）。
    ///
    /// ## 硬规则（三件事都做对了才叫"系进了类型"）
    /// 1. **只有一个 `DVector3` 字段、零额外状态**（薄）；一切运算**逐位委托**给 `DVector3` 自己的算子
    ///    （不在这里重写数学，否则就是"第二份权威"）。
    /// 2. **跨系转换必须是 `explicit`**：`(DVector3)sv` 看得见、可 grep。**不许给隐式转换** ——
    ///    隐式化会把"看得见的转换"降级成"看不见的转换"，比现在更糟（§11.4 撤回 C-1 的同一条理由）。
    /// 3. **不同系的量之间没有算子**：`SystemVec3 + BodyVec3` **编译不过** —— 这就是本类型的全部价值。
    ///
    /// ## 现状（2026-09-30 起）
    /// **适配层系暂不建类型**（实测：`OrbitView` 的 r/v 有 173 处引用，且它们几乎全部直接喂给
    /// **逐字移植的 MJ 数值库**——那边的形参是 `V3`/`DVector3`，按 J1 不许改 ⇒ 给适配层加类型等于
    /// 在每个调用点插一次 cast，噪声大而护栏弱）。适配层与游戏世界的跨界**已经**由 S2 的两个口收口
    /// （`OrbitView.FromGameStateVectors` / `ov.GameFrameStateAtUT`）。本文件先承载**宿主侧**的两个系。
    /// </summary>
    public readonly struct SystemVec3
    {
        /// <summary>原始三分量（**同一个系内部**的算术请直接用本类型的算子；要跨系才 cast）。</summary>
        public readonly DVector3 v;

        public SystemVec3(DVector3 value) => v = value;

        // ── 跨系：**唯一**的进出方式，两条都显式（见文件头规则 2） ──────────────────
        public static explicit operator DVector3(SystemVec3 a) => a.v;
        public static explicit operator SystemVec3(DVector3 a) => new(a);

        // ── 同系算子（逐位委托 `DVector3`） ────────────────────────────────────────
        public static SystemVec3 operator +(SystemVec3 a, SystemVec3 b) => new(a.v + b.v);
        public static SystemVec3 operator -(SystemVec3 a, SystemVec3 b) => new(a.v - b.v);
        public static SystemVec3 operator -(SystemVec3 a) => new(-a.v);
        public static SystemVec3 operator *(SystemVec3 a, double d) => new(a.v * d);
        public static SystemVec3 operator *(double d, SystemVec3 a) => new(a.v * d);
        public static SystemVec3 operator /(SystemVec3 a, double d) => new(a.v / d);

        public static double Dot(SystemVec3 a, SystemVec3 b) => DVector3.Dot(a.v, b.v);
        public static SystemVec3 Cross(SystemVec3 a, SystemVec3 b) => new(DVector3.Cross(a.v, b.v));

        // 相等/不等：**同系**比较（语义委托 `DVector3` 的 `==`，不在这里重写）
        public static bool operator ==(SystemVec3 a, SystemVec3 b) => a.v == b.v;
        public static bool operator !=(SystemVec3 a, SystemVec3 b) => !(a == b);

        // 语义委托 `DVector3`（含上游那套：`Equals` 按位 ⇒ NaN 判相等；`GetHashCode` 同一口径），
        // 否则 CS0660/CS0661 两条警告（基线 539 会被打破）
        public override bool Equals(object? obj) => obj is SystemVec3 other && v.Equals(other.v);
        public override int GetHashCode() => v.GetHashCode();

        public double magnitude => v.magnitude;
        public double sqrMagnitude => v.sqrMagnitude;
        public SystemVec3 normalized => new(v.normalized);
        public bool IsFinite() => v.IsFinite();

        // 分量读取（**与系无关**：读一个坐标不会混系 ⇒ 不破坏护栏，但省掉大量 `.v.x` 噪声）
        public double x => v.x;
        public double y => v.y;
        public double z => v.z;

        public static readonly SystemVec3 zero = new(DVector3.zero);

#if !PURE_MATH
        /// <summary>
        /// **存档往返**（批6 S3-3c 第三片）：`OrbitState` 是存档结构 ⇒ 系类型必须有 Scribe。
        ///
        /// ⚠ **刻意委托给 `DVector3.Scribe`**：这样"标签 scheme"（`label_x/_y/_z` 三个 double +
        /// `LoadSaveMode.LoadingVars` 才回填）**全库只有一份实现**；本类型不复制那份逻辑，
        /// 也就不可能写出一个"看起来对、读档时差一个字段"的第二版。
        /// （守卫与 `DVector3.cs` 同：#if !PURE_MATH —— 纯测试工程没有 Verse。）
        /// </summary>
        public static void Scribe(string label, ref SystemVec3 value)
        {
            DVector3 v = value.v;
            DVector3.Scribe(label, ref v);
            if (Verse.Scribe.mode == Verse.LoadSaveMode.LoadingVars)
                value = new SystemVec3(v);
        }
#endif

        public override string ToString() => v.ToString();
    }

    /// <summary>
    /// **天体局部系**（相对中心天体）的三矢量。见 <see cref="SystemVec3" /> 的文件头规则。
    ///
    /// ⚠ 与系统系的换算**只有一条合法路径**：减去（或加上）中心天体的**系统系偏移**
    /// —— 见 `SpaceEntityGroup.GetCentralBodyOffset` 的口径（行星 → 零；卫星 → 绕父天体偏移）。
    /// 本类型**不提供**这条换算：它需要"是哪个天体"，属于持有该信息的那一层（`SpaceEntityGroup`）。
    /// </summary>
    public readonly struct BodyVec3
    {
        public readonly DVector3 v;

        public BodyVec3(DVector3 value) => v = value;

        public static explicit operator DVector3(BodyVec3 a) => a.v;
        public static explicit operator BodyVec3(DVector3 a) => new(a);

        public static BodyVec3 operator +(BodyVec3 a, BodyVec3 b) => new(a.v + b.v);
        public static BodyVec3 operator -(BodyVec3 a, BodyVec3 b) => new(a.v - b.v);
        public static BodyVec3 operator -(BodyVec3 a) => new(-a.v);
        public static BodyVec3 operator *(BodyVec3 a, double d) => new(a.v * d);
        public static BodyVec3 operator *(double d, BodyVec3 a) => new(a.v * d);
        public static BodyVec3 operator /(BodyVec3 a, double d) => new(a.v / d);

        public static double Dot(BodyVec3 a, BodyVec3 b) => DVector3.Dot(a.v, b.v);
        public static BodyVec3 Cross(BodyVec3 a, BodyVec3 b) => new(DVector3.Cross(a.v, b.v));

        public static bool operator ==(BodyVec3 a, BodyVec3 b) => a.v == b.v;
        public static bool operator !=(BodyVec3 a, BodyVec3 b) => !(a == b);

        public override bool Equals(object? obj) => obj is BodyVec3 other && v.Equals(other.v);
        public override int GetHashCode() => v.GetHashCode();

        public double magnitude => v.magnitude;
        public double sqrMagnitude => v.sqrMagnitude;
        public BodyVec3 normalized => new(v.normalized);
        public bool IsFinite() => v.IsFinite();

        // 分量读取（与系无关；见 `SystemVec3` 的同名成员）
        public double x => v.x;
        public double y => v.y;
        public double z => v.z;

        public static readonly BodyVec3 zero = new(DVector3.zero);

        public override string ToString() => v.ToString();
    }

    /// <summary>
    /// **跨系换算的唯一实现**（U-7：一个概念一个权威）。所有"系统系 ⇄ 天体局部系"的换算都走这里，
    /// 别在调用点手写 `pos - offset`（历史上这对换算散在 3+ 处，见 §11.1 A4 的账）。
    ///
    /// 语义（`GetCentralBodyOffset` 的口径）：中心天体**是行星（= 系统系原点所在）时偏移为零** ⇒
    /// 天体局部系与系统系在该情形下**数值相同** —— 这正是"看起来都一样、错了看不出"的来源，
    /// 所以类型必须由这里给出，而不是由调用点各自判断。
    /// </summary>
    public static class FrameConvert
    {
        /// <summary>**系统系位置 → 天体局部系位置**（减去中心天体的系统系偏移）。</summary>
        public static BodyVec3 SystemToBody(DVector3 systemPos, SystemVec3 centralBodyOffset) =>
            new(systemPos - centralBodyOffset.v);

        /// <summary>带系版本（过渡期用：读 `positionSystem` 时不必先 `.v`）。语义与上面那条**同一份**。</summary>
        public static BodyVec3 SystemToBody(SystemVec3 systemPos, SystemVec3 centralBodyOffset) =>
            SystemToBody(systemPos.v, centralBodyOffset);

        /// <summary>**系统系速度 → 天体局部系速度**（减去中心天体的系统系速度）。</summary>
        public static BodyVec3 SystemToBodyVelocity(DVector3 systemVel, SystemVec3 centralBodyVelocity) =>
            new(systemVel - centralBodyVelocity.v);

        /// <summary>带系版本（过渡期用）。语义与上面那条**同一份**。</summary>
        public static BodyVec3 SystemToBodyVelocity(SystemVec3 systemVel, SystemVec3 centralBodyVelocity) =>
            SystemToBodyVelocity(systemVel.v, centralBodyVelocity);

        /// <summary>**天体局部系位置 → 系统系位置**（加回中心天体的系统系偏移）。</summary>
        public static DVector3 BodyToSystem(BodyVec3 bodyPos, SystemVec3 centralBodyOffset) =>
            bodyPos.v + centralBodyOffset.v;

        /// <summary>**天体局部系速度 → 系统系速度**（加回中心天体的系统系速度）。</summary>
        public static DVector3 BodyToSystemVelocity(BodyVec3 bodyVel, SystemVec3 centralBodyVelocity) =>
            bodyVel.v + centralBodyVelocity.v;
    }
}
