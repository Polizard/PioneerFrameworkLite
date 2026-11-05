using System.Reflection;
using PioneerFramework;
using Xunit;

namespace PioneerFramework.Tests
{
    /// <summary>
    /// **概念状态进预演副本的前提**（2026-10-31 类重组批 3b）。
    ///
    /// 预演副本靠 `MemberwiseClone` + 字段赋值：值类型天然深拷贝，引用类型会被副本与真身**共享**
    /// ⇒ 预演步会污染实时状态。所以概念状态里只许放值类型（或 string）。
    /// 将来把 PowerThermal 换成类、或往里加 List/缓存引用，这条会直接红。
    /// </summary>
    public class ConceptStateCopySafetyTests
    {
        [Fact]
        public void PowerThermalState_HasOnlyValueTypeOrStringFields()
        {
            foreach (var field in typeof(PowerThermalState).GetFields(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                Assert.True(
                    field.FieldType.IsValueType || field.FieldType == typeof(string),
                    $"{field.Name} is {field.FieldType}; the prepared copy would share a reference");
            }
        }
    }
}
