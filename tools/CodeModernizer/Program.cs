// 语句/算法现代化批（N2③）工具：**按 IDE 分析的精确位置**做等价改写。
//   IDE0090：`new List<X>(args)` → `new(args)`（目标类型 new；保留参数表与初始化器）
//   IDE0300：`new List<X> { a, b }` → `[a, b]`（集合表达式）
//   IDE0305：`new X[] { a, b }` → `[a, b]`
//   IDE0301：`Array.Empty<X>()` → `[]`
//   IDE0063：`using (var x = …) { … }` → `using var x = …;`（本批只报告、不自动改——块体作用域变短会动语义）
// 判据：分析器已证明可简化 ⇒ 我们的工作是**精确落到该节点**（Roslyn 语法节点 span，不做文本猜测）。
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

var logPath = args[0];
var dry = args.Contains("--dry-run");
var rule = args.FirstOrDefault(a => a.StartsWith("IDE")) ?? "IDE0090";
var refPaths = args.Skip(1).Where(a => !a.StartsWith("IDE") && a != "--dry-run").ToArray();

var refs = new List<MetadataReference>();
foreach (var p in refPaths)
    if (File.Exists(p)) refs.Add(MetadataReference.CreateFromFile(p));
foreach (var p in ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")).Split(Path.PathSeparator))
    if (File.Exists(p)) refs.Add(MetadataReference.CreateFromFile(p));

var warnRe = new System.Text.RegularExpressions.Regex(
    @"^([A-Za-z]:\\[^(]+\.cs)\((\d+),(\d+)\): warning " + rule + @": ");
var sites = new List<(string file, int line, int col)>();
foreach (var line in File.ReadLines(logPath))
{
    var m = warnRe.Match(line);
    if (m.Success && !m.Groups[1].Value.Contains("ThirdParty"))
        sites.Add((m.Groups[1].Value, int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value)));
}
sites = sites.Distinct().OrderBy(s => s.file).ThenBy(s => s.line).ToList();
Console.WriteLine($"{rule} 站点（去重后）：{sites.Count}");

var trees = new Dictionary<string, SyntaxTree>();
SyntaxTree Tree(string f)
{
    if (!trees.TryGetValue(f, out var t))
    {
        t = CSharpSyntaxTree.ParseText(File.ReadAllText(f), new CSharpParseOptions(LanguageVersion.Latest), f);
        trees[f] = t;
    }
    return t;
}

var edits = new List<(string file, int start, int end, string text)>();
var stats = new Dictionary<string, int>();
void Bump(string k) => stats[k] = stats.GetValueOrDefault(k) + 1;

static bool IsCollLike(InitializerExpressionSyntax? init)
    // ⚠ 只认**集合/数组**初始化器（`{ a, b }`）；对象初始化器（`Prop = …`）**不许**转 `[…]`（2026-10-15 实测：
    //   贪心回退抓到外层 `new PlanIntent { Prop = … }` ⇒ 转成 `[Prop = …]` ⇒ 16 个编译错，当场手修）。
    => init != null && (init.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.CollectionInitializerExpression)
                     || init.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.ArrayInitializerExpression));

foreach (var (file, line, col) in sites)
{
    var tree = Tree(file);
    var text = tree.GetText();
    if (line - 1 >= text.Lines.Count) { Bump("行号越界"); continue; }
    var pos = text.Lines[line - 1].Start + Math.Max(0, col - 1);
    // ⚠ 事故教训（2026-10-14）：`new[] {…}` 是 **ImplicitArrayCreation**（不是 ObjectCreation）⇒ 旧的
    //   "向上找 ObjectCreation"会越过它、抓到外层 creation、把类型剥错。现在改为：
    //   **按 `new` 关键字截距选节点**（诊断位置通常就压在 `new` 上）——超出 16 字符的一律不认；
    //   命中的节点若是 ImplicitArrayCreation ⇒ **跳过**（不是本规则的靶子）。
    var tok = tree.GetRoot().FindToken(pos);
    SyntaxNode? pick = null;
    int bestDist = int.MaxValue;
    for (var n = tok.Parent; n != null; n = n.Parent)
    {
        SyntaxToken newKw;
        switch (n)
        {
            case ObjectCreationExpressionSyntax oc0: newKw = oc0.NewKeyword; break;
            case ImplicitArrayCreationExpressionSyntax ia0: newKw = ia0.NewKeyword; break;
            case ArrayCreationExpressionSyntax ac0: newKw = ac0.NewKeyword; break;
            default: continue;
        }
        int d = Math.Abs(newKw.SpanStart - pos);
        // 2026-10-15：窗口 16 → 64（IDE0305 的诊断位置压在初始化器花括号上，长类型名超 16 就漏；
        //   "最近关键字优先"本身就防了"抓外层"——外层 new 只会更远）。
        if (d <= 64 && d < bestDist)
        {
            bestDist = d;
            pick = n;
        }
        if (n.SpanStart < pos - 160) break;   // 走太远就别再找了
    }
    bool isImplicitArray = pick is ImplicitArrayCreationExpressionSyntax;
    var oc = pick as ObjectCreationExpressionSyntax;
    var ac = pick as ArrayCreationExpressionSyntax;
    var ia = pick as ImplicitArrayCreationExpressionSyntax;
    InvocationExpressionSyntax? inv = null;
    // 补充定位（2026-10-15 实测）：
    //  · IDE0301（`Array.Empty<X>()`）**没有 `new` 关键字** ⇒ 按"实参位置"的调用节点定位；
    //  · IDE0300/0305 的诊断位置压在**初始化器的花括号**上（离 `new` 关键字常超 16 字符）⇒
    //    按"哪个 creation 的 Initializer 含住 pos"定位（精确且不受窗口限制）。
    if (pick == null && rule == "IDE0301")
    {
        inv = tok.Parent?.AncestorsAndSelf().OfType<InvocationExpressionSyntax>()
            .FirstOrDefault(n => n.Expression.ToString().StartsWith("Array.Empty"));
        if (inv == null) { Bump("找不到 Array.Empty 调用"); continue; }
        edits.Add((file, inv.Span.Start, inv.Span.End, "[]"));
        Bump("改写（Array.Empty → []）");
        continue;
    }
    if (pick == null)
    {
        pick = tok.Parent?.AncestorsAndSelf().FirstOrDefault(n =>
            (n is ObjectCreationExpressionSyntax oc1 && IsCollLike(oc1.Initializer) && oc1.Initializer!.Span.Contains(pos)) ||
            (n is ImplicitArrayCreationExpressionSyntax ia1 && IsCollLike(ia1.Initializer) && ia1.Initializer!.Span.Contains(pos)) ||
            (n is ArrayCreationExpressionSyntax ac1 && IsCollLike(ac1.Initializer) && ac1.Initializer!.Span.Contains(pos)));
        if (pick != null)
        {
            isImplicitArray = pick is ImplicitArrayCreationExpressionSyntax;
            oc = pick as ObjectCreationExpressionSyntax;
            ac = pick as ArrayCreationExpressionSyntax;
            ia = pick as ImplicitArrayCreationExpressionSyntax;
        }
    }

    // 补充定位之三（2026-10-15）：**裸初始化器**——数组类型字段写成 `Type[] X = { … };`（没有 `new`！）
    // ⇒ 直接把 Initializer 节点换成 `[…]`（集合表达式靠声明类型定靶）。
    var bareInit = tok.Parent?.AncestorsAndSelf().OfType<InitializerExpressionSyntax>().FirstOrDefault(n =>
        (n.IsKind(SyntaxKind.CollectionInitializerExpression) || n.IsKind(SyntaxKind.ArrayInitializerExpression))
        && n.Span.Contains(pos)
        && n.Parent is not ObjectCreationExpressionSyntax
        && n.Parent is not ArrayCreationExpressionSyntax
        && n.Parent is not ImplicitArrayCreationExpressionSyntax);

    if (rule == "IDE0090" && oc != null)
    {
        // ⚠ 带初始化器时必须写 `new() { … }`（目标类型 new **加括号**），写成 `new { … }` 会变成匿名类型 ✗
        string txt;
        if (oc.Initializer != null)
            txt = (oc.ArgumentList == null ? "new()" : "new" + oc.ArgumentList.ToString()) + " " + oc.Initializer.ToString();
        else
            txt = "new" + (oc.ArgumentList?.ToString() ?? "()");
        edits.Add((file, oc.Span.Start, oc.Span.End, txt));
        Bump("改写");
        continue;
    }
    if (rule == "IDE0090" && isImplicitArray)
    {
        Bump("隐式数组（IDE0090 非靶子）");
        continue;
    }
    if ((rule == "IDE0300" || rule == "IDE0305"))
    {
        var init = oc?.Initializer ?? ac?.Initializer ?? ia?.Initializer ?? bareInit;
        if (init == null)
        {
            // IDE0300 偶尔报在无初始化器的目标类型 new 上 ⇒ 按 IDE0090 处理
            if (oc != null)
            {
                var txt0 = "new" + (oc.ArgumentList?.ToString() ?? "()");
                edits.Add((file, oc.Span.Start, oc.Span.End, txt0));
                Bump("改写（无初始化器 ⇒ 目标类型 new）");
                continue;
            }
            Bump("找不到初始化器");
            continue;
        }
        var inner = init.ToString();
        inner = inner.Substring(1, inner.Length - 2);
        // ⚠ 只 Trim 头（2026-10-15 踩到）：跨行初始化器尾部有换行+行尾注释 ⇒ Trim() 会把 `]` 粘进注释里
        //（`… 4, 7  // -X];`）。多头、尾随空白照留。
        inner = inner.Contains('\n') ? inner.TrimStart() : inner.Trim();
        var target = (SyntaxNode?)oc ?? (SyntaxNode?)ac ?? (SyntaxNode?)ia ?? bareInit;
        edits.Add((file, target!.Span.Start, target.Span.End, "[" + inner + "]"));
        Bump("改写");
        continue;
    }
    if (rule == "IDE0301" && inv != null)
    {
        edits.Add((file, inv.Span.Start, inv.Span.End, "[]"));
        Bump("改写");
        continue;
    }
    Bump("未定位到节点");
}

var uniq = edits.Distinct().ToList();
Console.WriteLine($"待改：{uniq.Count} 处");
foreach (var g in stats.GroupBy(kv => kv.Key))
    Console.WriteLine($"  {g.Key,-32} {g.Sum(kv => kv.Value)}");

foreach (var g in uniq.GroupBy(e => e.file))
{
    // ⚠ 嵌套护栏（2026-10-15）：外层替换的 span **含住**内层 ⇒ 同一轮只改"互不重叠"的，且**外层优先**
    //   （按 start 升序、跳过任何 start < 已保留项 end 的项）；内层留到下一轮（分析器会再报）。
    var keep = new List<(string file, int start, int end, string text)>();
    foreach (var e in g.OrderBy(x => x.start).ThenByDescending(x => x.end))
    {
        if (keep.Count > 0 && e.start < keep[^1].end)
        {
            Console.WriteLine($"  （跳过嵌套：{Path.GetFileName(e.file)} offset {e.start}，下一轮处理）");
            continue;
        }
        keep.Add(e);
    }
    var text = File.ReadAllText(g.Key);
    var hadBom = text.StartsWith('\ufeff');
    if (hadBom) text = text[1..];
    var sb = new System.Text.StringBuilder(text);
    foreach (var (_, start, end, ins) in keep.OrderByDescending(e => e.start))
    {
        int s = hadBom ? start + 1 : start, e2 = hadBom ? end + 1 : end;
        sb.Remove(s, e2 - s);
        sb.Insert(s, ins);
    }
    if (!dry)
        File.WriteAllText(g.Key, sb.ToString(), new System.Text.UTF8Encoding(hadBom));
    Console.WriteLine($"  {(dry ? "[dry] " : "")}{Path.GetFileName(g.Key)}: {keep.Count} 处");
}
if (dry) Console.WriteLine("（dry-run：未写盘）");
