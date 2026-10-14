// 批 B（控制流表达化）工具：把"每 section 单条 `return 值;`、且 switch 是方法体唯一语句"的
// switch **语句**转成 switch **表达式**（方法变表达式体）。
//   public static T M(args) { switch (s) { case A: return V1; default: return Vn; } }
// ⇒ public static T M(args) => s switch { A => V1, _ => Vn, };
// 严格校验（不满足一律跳过并报告）：
//   · switch 必须是**所在 Block 的唯一语句**，且该 Block 属于**方法/访问器/局部函数**（带体）；
//   · 每个 section 恰好一条语句且是 `return <expr>;`；多标签（`case A: case B:`）合并为 `A or B =>`；
//   · 守卫（`case A when cond:`）⇒ `A when cond =>`（语法上即 `=>` 模式带 when ✓ 原样搬运）；
//   · 有 `break`/`goto case`/块体/变量声明/穿透的 section 一律整体跳过（不动）。
// 值（含中文串）全部**从原文切片搬**，绝不重敲。
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

var rootDir = args[0];
var dry = args.Contains("--dry-run");

var edits = new List<(string file, int start, int end, string text)>();
var stats = new Dictionary<string, int>();
void Bump(string k) => stats[k] = stats.GetValueOrDefault(k) + 1;

foreach (var file in Directory.EnumerateFiles(rootDir, "*.cs", SearchOption.AllDirectories))
{
    if (file.Contains("ThirdParty") || file.Contains("已弃置") || file.Contains("/obj/") || file.Contains("/bin/")) continue;
    var text = File.ReadAllText(file);
    if (!text.Contains("switch (")) continue;
    var tree = CSharpSyntaxTree.ParseText(text, new CSharpParseOptions(LanguageVersion.Latest), file);
    var src = tree.GetText();
    foreach (var sw in tree.GetRoot().DescendantNodes().OfType<SwitchStatementSyntax>())
    {
        // ① switch 必须是所在 Block 的唯一语句
        if (sw.Parent is not BlockSyntax blk || blk.Statements.Count != 1) { Bump("switch 不是块内唯一语句"); continue; }
        // ② 该块必须属于方法/访问器/局部函数，且带体（是块体而非表达式体）
        SyntaxNode? owner = blk.Parent;
        string? declHead = null;
        if (owner is MethodDeclarationSyntax md) declHead = md.Identifier.ValueText;
        else if (owner is LocalFunctionStatementSyntax lf) declHead = lf.Identifier.ValueText;
        else if (owner is AccessorDeclarationSyntax) { Bump("访问器（留人工）"); continue; }
        else { Bump("块不是方法体"); continue; }
        // ③ 每个 section：单条 return
        var arms = new List<string>();
        bool ok = true;
        foreach (var sec in sw.Sections)
        {
            if (sec.Statements.Count != 1 || sec.Statements[0] is not ReturnStatementSyntax ret || ret.Expression == null)
            { ok = false; break; }
            string label = sec.Labels.Count == 0 ? "_" : string.Join(" or ", sec.Labels.Select(l => l switch
            {
                CasePatternSwitchLabelSyntax p => p.Pattern.ToString() + (p.WhenClause != null ? " when " + p.WhenClause.Condition : ""),
                CaseSwitchLabelSyntax c => c.Value.ToString(),
                DefaultSwitchLabelSyntax _ => "_",
                _ => "/*?*/"
            }));
            arms.Add("            " + label + " => " + ret.Expression.ToString() + ",");
        }
        if (!ok) { Bump("有 section 非单条 return（留人工）"); continue; }
        if (!sw.Sections.Any(s => s.Labels.Any(l => l is DefaultSwitchLabelSyntax)))
            arms.Add("            _ => throw new System.InvalidOperationException(\"unreachable\"),");   // 保守兜底（原语句无 default 时会抛）
        var indent = new string(' ', blk.SpanStart - src.Lines.GetLinePosition(blk.SpanStart).Character);
        var body = "\n" + indent + "    => " + sw.Expression.ToString() + " switch\n" + indent + "    {\n"
                 + string.Join("\n", arms) + "\n" + indent + "    };";
        edits.Add((file, blk.Span.Start, blk.Span.End, body));
        Bump("待改（" + declHead + "）");
    }
}

Console.WriteLine($"待改：{edits.Count} 处");
foreach (var g in stats.GroupBy(kv => kv.Key.StartsWith("待改") ? "待改" : kv.Key))
    Console.WriteLine($"  {g.Key,-34} {g.Sum(kv => kv.Value)}");

foreach (var g in edits.GroupBy(e => e.file))
{
    var text = File.ReadAllText(g.Key);
    var hadBom = text.StartsWith('\ufeff');
    if (hadBom) text = text[1..];
    var sb = new System.Text.StringBuilder(text);
    foreach (var (_, start, end, ins) in g.OrderByDescending(e => e.start))
    {
        int s = hadBom ? start + 1 : start, e2 = hadBom ? end + 1 : end;
        sb.Remove(s, e2 - s);
        sb.Insert(s, ins);
    }
    if (!dry) File.WriteAllText(g.Key, sb.ToString(), new System.Text.UTF8Encoding(hadBom));
    Console.WriteLine($"  {(dry ? "[dry] " : "")}{Path.GetFileName(g.Key)}: {g.Count()} 处");
}
