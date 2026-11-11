using RimWorld;

namespace PioneerFramework
{
    /// <summary>
    /// DefOf 校验锚点（tools/validate_config.py 按本文件声明的字段核对同名 Def）。
    /// 当前没有需要强类型引用的 Def；新增 [DefOf] 字段放这里，同时保证 Defs 里存在同名 defName。
    /// </summary>
    [DefOf]
    public static class PFDefs
    {
        static PFDefs()
        {
            DefOfHelper.EnsureInitializedInCtor(typeof(PFDefs));
        }
    }
}