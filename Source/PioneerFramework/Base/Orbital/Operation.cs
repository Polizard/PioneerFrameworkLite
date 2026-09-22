using System;
using System.Collections.Generic;
using MechJebLib.Utils;

namespace PioneerFramework
{
    /// <summary>
    /// 规划器**无法给出计划**时抛它；消息会出现在 `Operation.ErrorMessage` 上、且不产生节点。
    /// 与 MJ 的同名类型同义（`MechJeb2/Maneuver/Operation.cs:24`）。
    /// </summary>
    public class OperationException : Exception
    {
        public OperationException(string message) : base(message) { }
    }

    /// <summary>
    /// 一个**机动操作**——MJ 的 `MechJeb2/Maneuver/Operation.cs` 的移植（KSP 层，语义移植而非逐字）。
    ///
    /// **与上游的三处偏离，逐条登记**：
    /// ① **`DoParametersGUI` 从 `abstract` 降为 `virtual`（空实现）**。P-1 §1.1 的处置：
    ///    上游 18 个实现**全都**摸宿主/Unity 类型（IMGUI、`Localizer`、biome、地图点选），
    ///    硬搬进来等于把 GUI 拖进数值层。**GUI 留在我们那一侧另写。**
    /// ② **入参类型**：`Orbit o` → `OrbitView o`，`MechJebModuleTargetController target` → `ITargetView target`。
    ///    **函数体不改**——`OrbitView` 是按 `Orbit` 的成员名一比一搭的（P-1 §0.1）。
    /// ③ **返回值**：`List<ManeuverParameters>` → `List<PlanNode>`。
    ///    P-1 §3.4 已裁定 `PlanNode` 就是输出形状；§14.3『节点语义』行明令**不得新建 `MjNode`**、
    ///    且"`ManeuverParameters` → `PlanNode`"是**取代**而非并存（J2）。
    ///
    /// **没有改动的部分**：`MakeNodes` 的**错误处理契约**（`OperationException` → 写 `ErrorMessage` → 返回 `null`）
     /// 与**反射发现**（任何 public、非抽象、有无参构造的子类，按 `GetName()` 排序）都照搬 ——
     /// 后者是"加一个操作不用改注册表"的机制，值得保留。
    /// </summary>
    /// <summary>
    /// 一个**可编辑参数**的纯描述（标签 + 单位 + 读写闭包）——给 UI 层渲染用。
    ///
    /// 为什么需要它：上游每个 Operation 的 `DoParametersGUI` 直接用 `GUILayout`（KSP 的 IMGUI），
    /// 而我们的 `DoParametersGUI` 在移植时被摘成**空操作**（那一层不许进纯层、也不该进 Verse）⇒
    /// "玩家怎么改参数"这件事必须由别的东西承担。
    ///
    /// **读写一律用"显示单位"**（km / 度 / 比值）：换算写在每个 Operation 的闭包里
    /// ⇒ UI 不需要知道 `EditableDoubleMult` 的 scale，也不需要 `* 1000` 这类魔法数。
    /// </summary>
    public readonly struct PlanParam
    {
        public readonly string Label;
        public readonly string Unit;
        private readonly Func<double> _get;
        private readonly Action<double> _set;

        public PlanParam(string label, string unit, Func<double> get, Action<double> set)
        {
            Label = label;
            Unit = unit;
            _get = get;
            _set = set;
        }

        public double Get() => _get();
        public void Set(double displayValue) => _set(displayValue);
    }

    public abstract class Operation
    {
        protected string? ErrorMessage = "";

        public string? GetErrorMessage() => ErrorMessage;

        /// <summary>显示名。⚠ **发现期就会调用它**（用来排序），所以实现必须是纯的、不能有副作用。</summary>
        public abstract string GetName();

        /// <summary>
        /// 画参数区（问时间、高度等）。**默认空实现**——GUI 在我们的 UI 层另写。
        /// 上游是 `abstract`，见类注释 ①。
        /// </summary>
        public virtual void DoParametersGUI(OrbitView o, double universalTime, ITargetView target) { }

        /// <summary>
        /// 真正算计划的地方。**这是"一行不改"的那部分**（P-1 §0.1）。
        /// 出错抛 <see cref="OperationException" />。
        /// </summary>
        protected abstract List<PlanNode>? MakeNodesImpl(OrbitView o, double universalTime, ITargetView target);

        public List<PlanNode>? MakeNodes(OrbitView o, double universalTime, ITargetView target)
        {
            ErrorMessage = "";
            try
            {
                return MakeNodesImpl(o, universalTime, target);
            }
            catch (OperationException e)
            {
                ErrorMessage = e.Message;
                return null;
            }
            catch (Exception e)
            {
                Logger.Print($"[PFL/Operation] {GetName()} 抛出了非 OperationException 的异常：{e}");
                ErrorMessage = "创建节点时发生错误";
                return null;
            }
        }

        private static readonly object _discoveryLock = new object();

        /// <summary>发现到的类型表。**只在 `_discoveryLock` 里构建、一次性发布**（引用赋值是原子的），
        /// 这样并发调用者要么看到 null、要么看到完整数组，看不到"半个列表"。见 `GetAvailableOperations`。</summary>
        private static Type[]? _opTypes;

        private static void AddTypes(List<Type> into, Type[] types)
        {
            foreach (Type t in types)
            {
                if (t != null
                    && !t.IsAbstract
                    && typeof(Operation).IsAssignableFrom(t)
                    && t.GetConstructor(Type.EmptyTypes) != null)
                    into.Add(t);
            }
        }

        /// <summary>
        /// 反射发现全部可用操作并按名字排序（上游机制，照搬）。
        /// ⚠ 只在**第一次**调用时扫一遍并缓存；发现期会调用每个实现的 `GetName()`。
        ///
        /// ## ⚠ 与上游的偏离（治本）：**发现过程加锁 + 一次性发布**（2026-09-19）
        /// 上游写的是 `if (_operations.Count == 0) { … _operations.Add(…) }` —— 一个**非线程安全的惰性缓存**。
        /// 两个调用者同时进来时，`List&lt;T&gt;` 会被并发写坏，`ConvertAll` 会读出 `null` 元素
        /// ⇒ `t.GetConstructor(...)` 抛 `NullReferenceException`（**实测**：同一批用例单独跑绿、
        /// 与更多测试类并行跑红，失败点正是这句）。
        /// 上游在 KSP 里是单线程 UI 调用所以没暴露；我们的测试与 UI 都可能并发碰它。
        /// ⇒ 改成"在锁里构建到本地表、再一次性发布"，**实例仍然每次新建**（保住上游语义：
        /// 每个调用者拿到的是自己的那份，参数不会互相踩）。
        /// </summary>
        public static Operation[] GetAvailableOperations()
        {
            Type[]? types = _opTypes;

            if (types == null)
            {
                lock (_discoveryLock)
                {
                    if (_opTypes == null)
                    {
                        var found = new List<Type>();

                        foreach (System.Reflection.Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
                        {
                            try
                            {
                                AddTypes(found, assembly.GetTypes());
                            }
                            catch (System.Reflection.ReflectionTypeLoadException e)
                            {
                                AddTypes(found, e.Types);
                            }
                            catch (InvalidOperationException)
                            {
                                // 上游注释同款：有些程序集加载不了（多半缺依赖），静默跳过
                            }
                        }

                        // 发布：别的线程只会看到 null 或这个完整数组
                        _opTypes = found.ToArray();
                        // ⚠ 这里**不能**调 `Logger.Print`（2026-09-21 实测：它会把并行测试搅崩 ✗）：
                        //   移植的 `MechJebLib.Utils.Logger` 持有**全局可变**的输出处理器，
                        //   而 xUnit 的 `ITestOutputHelper` 是**每个测试自己**的 ⇒ 并行时会写到
                        //   已结束的那个测试的 helper 上 ⇒ `InvalidOperationException: There is no
                        //   currently active test`（实测：`PlanParamTests.EveryDiscoveredOperation_…`
                        //   与 `IzzoTests` 并行时偶发 ✗）。这个数量对测试/生产都无用
                        //   （`GetAvailableOperations().Length` 随时可读）⇒ 不打。
                    }

                    types = _opTypes!;
                }
            }

            var res = new List<Operation>(types?.Length ?? 0);
            if (types == null) return res.ToArray(); // 不可达（锁里已发布），只为让可空性检查闭合
            foreach (Type t in types)
            {
                var ctor = t.GetConstructor(Type.EmptyTypes);
                if (ctor != null) // 防御：类型表发布后不该变，但别让一句 NRE 把整个规划面板炸掉
                    res.Add((Operation)ctor.Invoke(null));
            }

            res.Sort((x, y) => string.Compare(x.GetName(), y.GetName(), StringComparison.Ordinal));
            return res.ToArray();
        }

        public virtual bool Draggable => true;

        /// <summary>本操作可编辑的参数（**默认空**）。UI 逐项渲染；新增 Operation 必须覆盖它，
        /// 否则界面上看不到参数（有 `PlanParamTests` 机械地兜这件事）。</summary>
        public virtual IReadOnlyList<PlanParam> GetParams() => EmptyParams;

        private static readonly PlanParam[] EmptyParams = new PlanParam[0];

        /// <summary>本操作的时间选择器（没有则 `null`）。</summary>
        public virtual TimeSelector? GetTimeSelector() => null;

        /// <summary>本操作是否需要"目标"（需要时 UI 必须先把目标接上才让它计算）。</summary>
        public virtual bool RequiresTarget => false;
    }
}
