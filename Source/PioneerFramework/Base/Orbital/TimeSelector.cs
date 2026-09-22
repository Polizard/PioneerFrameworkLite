using System;

namespace PioneerFramework
{
    /// <summary>机动时刻的选择方式（上游 `MechJeb2/Maneuver/TimeSelector.cs:9` 的同名枚举，值序一致）。</summary>
    public enum TimeReference
    {
        COMPUTED, X_FROM_NOW, APOAPSIS, PERIAPSIS, ALTITUDE, EQ_ASCENDING, EQ_DESCENDING,
        REL_ASCENDING, REL_DESCENDING, CLOSEST_APPROACH,
        EQ_HIGHEST_AD, EQ_NEAREST_AD, REL_HIGHEST_AD, REL_NEAREST_AD
    }

    /// <summary>
    /// "什么时候点火"的选择器——MJ 的 `TimeSelector` 的移植（KSP 层，语义移植）。
    /// **16/18 个 Operation 都用它**，所以它是 A4 的必要件。
    ///
    /// **与上游的三处偏离，逐条登记**：
    /// ① **`DoChooseTimeGUI` 变成空实现**（GUI 在我们的 UI 层另写）。`_timeRefNames` 改为公开只读属性
    ///    `TimeRefNames` 供 UI 使用——否则那是个只写不读的死字段。
    /// ② **`Localizer.Format("#MechJeb_...")` → 普通字符串**。上游的异常文案走 KSP 本地化；
    ///    我们的本地化是另一套，等接 UI 时一起接。**文案本身保留原意**，便于对照上游排错。
    /// ③ **目标接口**：`target.NormalTargetExists` + `target.TargetOrbit` → `target.TryOrbit(ut, out orbit)`。
    ///    这不是简化而是**语义升级**：`TryOrbit` 返回 false 覆盖"没有目标"**以及**"接触过期/无解算中心天体"
    ///    两种情形，而两者都必须让计划不可做（P-1 §4.2）。**绝不回退到真值。**
    /// </summary>
    public class TimeSelector
    {
        private double _universalTime;

        private readonly TimeReference[] _allowedTimeRef;

        /// <summary>当前选中的时间参考（对 <see cref="_allowedTimeRef" /> 的下标）。UI 写它。</summary>
        public int _currentTimeRef;

        public TimeReference TimeReference => _allowedTimeRef[_currentTimeRef];

        /// <summary>"从现在起 N 秒"用的时长。</summary>
        public readonly EditableTime LeadTime = new EditableTime(0);

        /// <summary>`ALTITUDE` 分支用的高度。</summary>
        public readonly EditableDoubleMult CircularizeAltitude = new EditableDoubleMult(150000, 1000);

        /// <summary>供 UI 显示的名字（与 `_allowedTimeRef` 同序）。上游用 `Localizer`，见类注释 ②。</summary>
        public string[] TimeRefNames { get; }

        public TimeSelector(TimeReference[] allowedTimeRef)
        {
            _allowedTimeRef = allowedTimeRef;
            _universalTime = 0;
            TimeRefNames = new string[allowedTimeRef.Length];
            for (int i = 0; i < allowedTimeRef.Length; ++i)
                TimeRefNames[i] = NameOf(allowedTimeRef[i]);
        }

        private static string NameOf(TimeReference r)
        {
            switch (r)
            {
                case TimeReference.COMPUTED: return "计算值";
                case TimeReference.APOAPSIS: return "远心点";
                case TimeReference.PERIAPSIS: return "近心点";
                case TimeReference.CLOSEST_APPROACH: return "最近接近";
                case TimeReference.EQ_ASCENDING: return "赤道升交点";
                case TimeReference.EQ_DESCENDING: return "赤道降交点";
                case TimeReference.REL_ASCENDING: return "相对升交点";
                case TimeReference.REL_DESCENDING: return "相对降交点";
                case TimeReference.X_FROM_NOW: return "从现在起";
                case TimeReference.ALTITUDE: return "到达高度";
                case TimeReference.EQ_NEAREST_AD: return "最近的赤道交点";
                case TimeReference.EQ_HIGHEST_AD: return "较快的赤道交点";
                case TimeReference.REL_NEAREST_AD: return "最近的相对交点";
                case TimeReference.REL_HIGHEST_AD: return "较快的相对交点";
                default: return r.ToString();
            }
        }

        /// <summary>画时间选择区。**空实现**——GUI 在我们的 UI 层（见类注释 ①）。</summary>
        public void DoChooseTimeGUI() { }

        /// <summary>
        /// 按枚举把当前项**钉到指定口径**上（供 `PlanIntent` 的活重解用）。
        /// 返回 false 表示本操作不允许那个口径（例如"改倾角"的候选表里没有 `X_FROM_NOW` 之外的绝对口径）。
        ///
        /// 【为什么需要】`X_FROM_NOW` 是**相对**口径（"从现在起 N 秒"）⇒ 计划每被重解一次就会往后漂一次 ✗。
        /// 活重解必须把"**哪一刻点火**"钉成绝对时刻：记下当时的绝对 tick，重解时用 `X_FROM_NOW` + 差值把它还原。
        /// </summary>
        public bool TrySetTimeRef(TimeReference want)
        {
            for (int i = 0; i < _allowedTimeRef.Length; i++)
            {
                if (_allowedTimeRef[i] != want) continue;
                _currentTimeRef = i;
                return true;
            }
            return false;
        }

        /// <summary>候选表里有没有这一项（`PlanIntent` 判断能不能把时刻钉成绝对口径）。</summary>
        public bool Allows(TimeReference want)
        {
            foreach (TimeReference t in _allowedTimeRef)
                if (t == want) return true;
            return false;
        }

        /// <summary>
        /// 算出"这次机动该在什么时候点火"。
        /// **函数体逻辑逐条照搬上游**，只有类型与目标接口换过（见类注释 ③）。
        /// </summary>
        public double ComputeManeuverTime(OrbitView o, double ut, ITargetView target)
        {
            switch (_allowedTimeRef[_currentTimeRef])
            {
                case TimeReference.X_FROM_NOW:
                    ut += LeadTime.Val;
                    break;

                case TimeReference.APOAPSIS:
                    if (!(o.eccentricity < 1))
                        throw new OperationException("轨道是双曲的，没有远心点。");
                    ut = o.NextApoapsisTime(ut);
                    break;

                case TimeReference.PERIAPSIS:
                    ut = o.NextPeriapsisTime(ut);
                    break;

                case TimeReference.CLOSEST_APPROACH:
                    // ⚠ 拿不到目标轨道（没有目标 / 接触过期 / 无解算中心天体）⇒ 计划不可做，不回退真值
                    if (!target.TryOrbit(ut, out OrbitView targetOrbit))
                        throw new OperationException("没有可用目标。");
                    ut = o.NextClosestApproachTime(targetOrbit, ut);
                    break;

                case TimeReference.ALTITUDE:
                    if (!(CircularizeAltitude > o.PeA) || (!(CircularizeAltitude < o.ApA) && !(o.eccentricity >= 1)))
                        throw new OperationException("当前轨道到不了这个高度，无法在此圆化。");
                    ut = o.NextTimeOfRadius(ut, o.bodyRadius + CircularizeAltitude);
                    break;

                case TimeReference.EQ_ASCENDING:
                    if (!o.AscendingNodeEquatorialExists())
                        throw new OperationException("赤道升交点不存在。");
                    ut = o.TimeOfAscendingNodeEquatorial(ut);
                    break;

                case TimeReference.EQ_DESCENDING:
                    if (!o.DescendingNodeEquatorialExists())
                        throw new OperationException("赤道降交点不存在。");
                    ut = o.TimeOfDescendingNodeEquatorial(ut);
                    break;

                case TimeReference.EQ_NEAREST_AD:
                    if (o.AscendingNodeEquatorialExists() && o.DescendingNodeEquatorialExists())
                        ut = Math.Min(o.TimeOfAscendingNodeEquatorial(ut), o.TimeOfDescendingNodeEquatorial(ut));
                    else if (o.AscendingNodeEquatorialExists())
                        ut = o.TimeOfAscendingNodeEquatorial(ut);
                    else if (o.DescendingNodeEquatorialExists())
                        ut = o.TimeOfDescendingNodeEquatorial(ut);
                    else
                        throw new OperationException("赤道升交点与降交点都不存在。");
                    break;

                case TimeReference.EQ_HIGHEST_AD:
                    if (o.AscendingNodeEquatorialExists() && o.DescendingNodeEquatorialExists())
                    {
                        double anTime = o.TimeOfAscendingNodeEquatorial(ut);
                        double dnTime = o.TimeOfDescendingNodeEquatorial(ut);
                        ut = o.RightHandedOrbitalVelocityAtUT(anTime).magnitude
                             <= o.RightHandedOrbitalVelocityAtUT(dnTime).magnitude
                            ? anTime
                            : dnTime;
                    }
                    else if (o.AscendingNodeEquatorialExists())
                        ut = o.TimeOfAscendingNodeEquatorial(ut);
                    else if (o.DescendingNodeEquatorialExists())
                        ut = o.TimeOfDescendingNodeEquatorial(ut);
                    else
                        throw new OperationException("赤道升交点与降交点都不存在。");

                    break;
            }

            _universalTime = ut;
            return _universalTime;
        }
    }
}
