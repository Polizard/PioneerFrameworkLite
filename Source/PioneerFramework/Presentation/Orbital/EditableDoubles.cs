using System.Globalization;

namespace PioneerFramework
{
    /// <summary>
    /// 一个可编辑的 double：存值 + 存它的**文本**表示，并带一个乘数（用户看到的数 × 乘数 = 存的值）。
    /// 移植自 MJ 的 `MechJeb2/GuiUtils.cs:23`（KSP 层，语义移植）。
    ///
    /// **为什么规划器需要它**：上游的 `Operation` **没有 settings 对象**——参数就是实例字段
    /// （`OperationApoapsis.NewApA` 这类），`MakeNodesImpl` 直接读它们。
    /// 这正是 `MakeNodesImpl` **可以脱离 GUI 被驱动**的原因（P-1 §1.1），也是它值得保留的原因。
    ///
    /// **与上游的两处偏离，逐条登记**：
    /// ① **去掉 `IEditable` 接口、正则清洗与 `[Persistent]`**——那是 UI/序列化关注点，留在我们那一侧。
    ///    （上游 `Text` 的 setter 会 `Regex.Replace` 掉非数字字符；这里由调用方保证输入已经是纯数字。）
    /// ② **解析固定用 `InvariantCulture`**。上游用环境文化 ⇒ 同一段配置文本在 zh-CN 与 en-US 机器上
    ///    可能解析出不同结果（小数点/千分位）。**我们不要这种不确定性**——
    ///    与本项目 `tools/test-all.ps1` 强制 `DOTNET_CLI_UI_LANGUAGE=en` 同源。
    /// </summary>
    public class EditableDoubleMult
    {
        /// <summary>存下来的值（= 用户看到的数 × 乘数）。</summary>
        public double ValConfig;

        /// <summary>用户看到的文本。</summary>
        public string TextConfig;

        /// <summary>
        /// 上一次给 `Text` 赋值是否解析成功。
        /// ⚠ 上游是 `protected`；这里**公开**：替代上游 GUI 的那个编辑器需要它来标"输入非法"。
        /// </summary>
        public bool Parsed;

        private readonly double _multiplier;

        public virtual double Val
        {
            get => ValConfig;
            set
            {
                ValConfig = value;
                TextConfig = (ValConfig / _multiplier).ToString(CultureInfo.InvariantCulture);
            }
        }

        public virtual string Text
        {
            get => TextConfig;
            set
            {
                TextConfig = value;
                Parsed = double.TryParse(TextConfig, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsedValue);
                if (Parsed) ValConfig = parsedValue * _multiplier;
            }
        }

        public EditableDoubleMult() : this(0) { }

        public EditableDoubleMult(double val, double multiplier = 1)
        {
            _multiplier = multiplier;
            ValConfig = val;
            TextConfig = (val / multiplier).ToString(CultureInfo.InvariantCulture);
        }

        public static implicit operator double(EditableDoubleMult x) => x.Val;

        public override string ToString() => Val.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// 可编辑的整数（上游 `EditableInt`）。用途单一：共振比这类**计数**参数（分子/分母）。
    /// 与 <see cref="EditableDoubleMult" /> 同款：解析固定 `InvariantCulture`、失败时存值不变。
    /// </summary>
    public class EditableInt
    {
        public int ValConfig;

        public string TextConfig;

        public bool Parsed;

        public virtual int Val
        {
            get => ValConfig;
            set
            {
                ValConfig = value;
                TextConfig = value.ToString(CultureInfo.InvariantCulture);
            }
        }

        public virtual string Text
        {
            get => TextConfig;
            set
            {
                TextConfig = value;
                Parsed = int.TryParse(TextConfig, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed);
                if (Parsed) ValConfig = parsed;
            }
        }

        public EditableInt() : this(0) { }

        public EditableInt(int val)
        {
            ValConfig = val;
            TextConfig = val.ToString(CultureInfo.InvariantCulture);
        }

        public static implicit operator int(EditableInt x) => x.Val;

        public override string ToString() => Val.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>乘数为 1 的 <see cref="EditableDoubleMult" />（上游同名类型）。</summary>
    public class EditableDouble : EditableDoubleMult
    {
        public EditableDouble(double val) : base(val) { }

        public static implicit operator EditableDouble(double x) => new(x);
    }

    /// <summary>
    /// 以**秒**为单位的可编辑时长（上游同名类型）。
    ///
    /// ⚠ 上游的 `Val` setter 会把文本格式化成 DHMS（`GuiUtils.TimeToDHMS`）——那是**显示**关注点，
    /// 已随 GUI 一起剥离：这里 `Text` 就是秒数的数字串，格式化由我们的 UI 层做。
    /// </summary>
    public class EditableTime : EditableDouble
    {
        public EditableTime() : this(0) { }

        public EditableTime(double seconds) : base(seconds) { }
    }
}
