// 可空族工作流 · 批 2 工具：CS8603 的返回类型批（见 csproj 头注释）。
// 只做一件事：把"确实可能返回 null 的**引用类型**返回"的方法，签名改成 `T?`。
// 判据全部来自 Roslyn 语义模型（不再猜）：包住站点的符号必须是**普通方法**（lambda/局部函数/访问器一律跳过），
// 返回类型必须是引用类型/类型参数、且当前未标注 `?`。
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

var logPath = args[0];
var dry = args.Contains("--dry-run");
var mode = args.Contains("cs8619") ? "cs8619"
         : args.Contains("cs8601") ? "cs8601"
         : args.Contains("cs8604") ? "cs8604"
         : args.Contains("cs8625f") ? "cs8625f"
         : args.Contains("cs8618") ? "cs8618"
         : args.Contains("cs8625") ? "cs8625"
         : args.Contains("cs8600") ? "cs8600"
         : "cs8603";   // cs8603=返回类型批 · cs8618=字段批 · cs8625=参数默认值 · cs8600=局部声明批
var refPaths = args.Skip(1).Where(a => !a.StartsWith("cs86") && a != "--dry-run").ToArray();

var refs = new List<MetadataReference>();
foreach (var p in refPaths)
{
    if (File.Exists(p))
        refs.Add(MetadataReference.CreateFromFile(p));
}
var tpa = (string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES");
foreach (var p in tpa.Split(Path.PathSeparator))
{
    if (File.Exists(p))
        refs.Add(MetadataReference.CreateFromFile(p));
}
Console.WriteLine($"引用 {refs.Count} 个程序集");

// 收集 CS8603 站点（去重；排除 ThirdParty）
var warnRe = new System.Text.RegularExpressions.Regex(
    mode == "cs8625"
        ? @"^([A-Za-z]:\\[^(]+\.cs)\((\d+),(\d+)\): warning CS8625: "
        : mode == "cs8600"
        ? @"^([A-Za-z]:\\[^(]+\.cs)\((\d+),(\d+)\): warning CS8600: "
        : mode == "cs8619"
        ? @"^([A-Za-z]:\\[^(]+\.cs)\((\d+),(\d+)\): warning CS8619: "
        : mode == "cs8601"
        ? @"^([A-Za-z]:\\[^(]+\.cs)\((\d+),(\d+)\): warning CS8601: "
        : mode == "cs8604"
        ? @"^([A-Za-z]:\\[^(]+\.cs)\((\d+),(\d+)\): warning CS8604: "
        : mode == "cs8618"
        ? @"^([A-Za-z]:\\[^(]+\.cs)\((\d+),(\d+)\): warning CS8618: .*?字段\s*""([^""]+)"""
        : @"^([A-Za-z]:\\[^(]+\.cs)\((\d+),(\d+)\): warning CS8603: ");
var sites = new List<(string file, int line, int col, string member)>();
foreach (var line in File.ReadLines(logPath))
{
    var m = warnRe.Match(line);
    if (m.Success && !m.Groups[1].Value.Contains("ThirdParty"))
        sites.Add((m.Groups[1].Value, int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value), m.Groups.Count > 4 ? m.Groups[4].Value : null));
}
sites = sites.Distinct().OrderBy(s => s.file).ThenBy(s => s.line).ToList();
Console.WriteLine($"{mode.ToUpperInvariant()} 站点（去重后）：{sites.Count}");

var trees = new Dictionary<string, SyntaxTree>();
var models = new Dictionary<string, SemanticModel>();

SyntaxTree GetTree(string file)
{
    if (!trees.TryGetValue(file, out var t))
    {
        t = CSharpSyntaxTree.ParseText(File.ReadAllText(file), new CSharpParseOptions(LanguageVersion.Latest), file);
        trees[file] = t;
    }
    return t;
}
SemanticModel GetModel(string file)
{
    if (!models.TryGetValue(file, out var sm))
    {
        sm = CSharpCompilation.Create("pfx", trees.Values, refs,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable))
            .GetSemanticModel(GetTree(file));
        models[file] = sm;
    }
    return sm;
}

var edits = new List<(string file, int pos, string text)>();
var stripNullBang = new HashSet<string>();
var stats = new Dictionary<string, int>();
void Bump(string k) => stats[k] = stats.GetValueOrDefault(k) + 1;

foreach (var (file, line, col, member) in sites)
{
    var tree = GetTree(file);
    var text = tree.GetText();
    if (line - 1 >= text.Lines.Count) { Bump("行号越界"); continue; }
    var span = text.Lines[line - 1].Span;
    var model = GetModel(file);

    if (mode == "cs8600")
    {
        // 站点 = 被判"可能为 null 的值"处。本片只处理**局部变量声明**（`T x = <可能null>;`）⇒ 声明标 `T?`。
        // 赋值形态（`x = …;`：变量本意）与字段形态（字段契约）留给人工。
        var token = tree.GetRoot().FindToken(text.Lines[line - 1].Start + Math.Max(0, col - 1));
        Microsoft.CodeAnalysis.CSharp.Syntax.VariableDeclaratorSyntax? vd = null;
        for (var n = token.Parent; n != null; n = n.Parent)
        {
            if (n is Microsoft.CodeAnalysis.CSharp.Syntax.VariableDeclaratorSyntax v) { vd = v; break; }
            if (n is Microsoft.CodeAnalysis.CSharp.Syntax.AssignmentExpressionSyntax) break;   // 赋值形态 ⇒ 人工
            if (n is Microsoft.CodeAnalysis.CSharp.Syntax.StatementSyntax) break;
        }
        if (vd == null) { Bump("非局部声明形态（赋值/字段等）"); continue; }
        if (vd.Parent is not Microsoft.CodeAnalysis.CSharp.Syntax.VariableDeclarationSyntax vdecl) { Bump("无声明语法"); continue; }
        if (vdecl.Type.IsVar) { Bump("var 声明（不该有本告警）"); continue; }
        var lsym = model.GetDeclaredSymbol(vd) as ILocalSymbol;
        if (lsym == null) { Bump("非局部变量"); continue; }
        if (!lsym.Type.IsReferenceType && lsym.Type.TypeKind != TypeKind.TypeParameter) { Bump("值类型局部"); continue; }
        if (lsym.Type.NullableAnnotation == NullableAnnotation.Annotated) { Bump("已是可空"); continue; }
        edits.Add((file, vdecl.Type.Span.End, "?"));
        Bump("待改（" + lsym.Name + "）");
        continue;
    }

    if (mode == "cs8604")
    {
        // 站点 = "可能为 null 的实参"处 ⇒ 找到对应**形参**，把形参声明标 `T?`（与批 2a 同型：调用点把可空值
        // 传进来 ⇒ 形参本就该接受 null；方法体若有非空假定会浮成 CS8602 = 诚实级联）。
        var tk4 = tree.GetRoot().FindToken(text.Lines[line - 1].Start + Math.Max(0, col - 1));
        Microsoft.CodeAnalysis.CSharp.Syntax.ArgumentSyntax? arg = null;
        for (var n = tk4.Parent; n != null; n = n.Parent)
        {
            if (n is Microsoft.CodeAnalysis.CSharp.Syntax.ArgumentSyntax a) { arg = a; break; }
            if (n is Microsoft.CodeAnalysis.CSharp.Syntax.StatementSyntax) break;
        }
        if (arg == null) { Bump("非实参形态"); continue; }
        var inv = arg.Parent?.Parent;
        var tgt = inv == null ? null : model.GetSymbolInfo(inv).Symbol as IMethodSymbol;
        if (tgt == null) { Bump("查不到调用符号"); continue; }
        IParameterSymbol? p4 = null;
        if (arg.NameColon != null)
            p4 = tgt.Parameters.FirstOrDefault(pp => pp.Name == arg.NameColon.Name.Identifier.ValueText);
        else
        {
            int idx4 = 0;
            foreach (var a2 in arg.Parent!.ChildNodes().OfType<Microsoft.CodeAnalysis.CSharp.Syntax.ArgumentSyntax>())
            {
                if (a2 == arg) break;
                if (a2.NameColon == null) idx4++;
            }
            if (idx4 < tgt.Parameters.Length) p4 = tgt.Parameters[idx4];
        }
        if (p4 == null) { Bump("定位不到形参"); continue; }
        if (!p4.Type.IsReferenceType && p4.Type.TypeKind != TypeKind.TypeParameter) { Bump("值类型形参"); continue; }
        if (p4.Type.NullableAnnotation == NullableAnnotation.Annotated) { Bump("形参已是可空"); continue; }
        var pdecl = p4.DeclaringSyntaxReferences.Select(r => r.GetSyntax())
            .OfType<Microsoft.CodeAnalysis.CSharp.Syntax.ParameterSyntax>().FirstOrDefault();
        if (pdecl?.Type == null) { Bump("找不到形参声明（可能来自元数据）"); continue; }
        edits.Add((pdecl.SyntaxTree.FilePath, pdecl.Type.Span.End, "?"));
        Bump("待改（形参 " + p4.Name + "）");
        continue;
    }

    if (mode == "cs8619")
    {
        // 站点 = 元组返回类型与实参的可空性不匹配（`return (null, null, null);`）⇒ **元组元素契约改真**：
        // 方法返回类型里每个"引用类型且未标 ?"的元素补 `?`（与 2a 同型，只是作用在元组元素上）。
        var span9 = text.Lines[line - 1].Span;
        var sym9 = model.GetEnclosingSymbol(span9.Start);
        if (sym9 is not IMethodSymbol m9) { Bump("非方法符号"); continue; }
        var decl9 = m9.DeclaringSyntaxReferences.Select(r => r.GetSyntax()).OfType<Microsoft.CodeAnalysis.CSharp.Syntax.MethodDeclarationSyntax>().FirstOrDefault();
        if (decl9?.ReturnType is not Microsoft.CodeAnalysis.CSharp.Syntax.TupleTypeSyntax tt) { Bump("返回类型不是元组语法"); continue; }
        if (m9.ReturnType is not INamedTypeSymbol { IsTupleType: true } tup) { Bump("符号不是元组"); continue; }
        int added9 = 0;
        for (int i = 0; i < tt.Elements.Count && i < tup.TupleElements.Length; i++)
        {
            var el = tup.TupleElements[i];
            if (!el.Type.IsReferenceType && el.Type.TypeKind != TypeKind.TypeParameter) continue;
            if (el.Type.NullableAnnotation == NullableAnnotation.Annotated) continue;
            var elt = tt.Elements[i].Type;
            if (elt == null) continue;
            edits.Add((decl9.SyntaxTree.FilePath, elt.Span.End, "?"));
            added9++;
        }
        if (added9 == 0) { Bump("元组元素无需改"); continue; }
        Bump("待改（元组 " + m9.Name + " ×" + added9 + "）");
        continue;
    }

    if (mode == "cs8601")
    {
        // 站点 = "把可能为 null 的值赋给非空字段/属性"处 ⇒ **字段/属性契约改真**：声明标 `T?`
        // （与 2a 同型：接住可空返回值 ⇒ 该成员本就该可空），并撤掉批 1 的 `= null!;`（若有）。
        var tk1 = tree.GetRoot().FindToken(text.Lines[line - 1].Start + Math.Max(0, col - 1));
        Microsoft.CodeAnalysis.CSharp.Syntax.AssignmentExpressionSyntax? asg1 = null;
        for (var n = tk1.Parent; n != null; n = n.Parent)
        {
            if (n is Microsoft.CodeAnalysis.CSharp.Syntax.AssignmentExpressionSyntax a) { asg1 = a; break; }
            if (n is Microsoft.CodeAnalysis.CSharp.Syntax.StatementSyntax) break;
        }
        if (asg1 == null) { Bump("非赋值形态"); continue; }
        var sym1 = model.GetSymbolInfo(asg1.Left).Symbol;
        var fld = sym1 as IFieldSymbol;
        var prop = sym1 as IPropertySymbol;
        if (fld == null && prop == null) { Bump("左侧非字段/属性"); continue; }
        var ty1 = fld != null ? fld.Type : prop!.Type;
        if (!ty1.IsReferenceType && ty1.TypeKind != TypeKind.TypeParameter) { Bump("值类型成员"); continue; }
        if (ty1.NullableAnnotation == NullableAnnotation.Annotated) { Bump("成员已是可空"); continue; }
        var dsyn = (fld ?? (ISymbol)prop!).DeclaringSyntaxReferences.Select(r => r.GetSyntax()).FirstOrDefault();
        switch (dsyn)
        {
            case Microsoft.CodeAnalysis.CSharp.Syntax.VariableDeclaratorSyntax vd1
                when vd1.Parent is Microsoft.CodeAnalysis.CSharp.Syntax.VariableDeclarationSyntax vv1:
                edits.Add((vd1.SyntaxTree.FilePath, vv1.Type.Span.End, "?"));
                break;
            case Microsoft.CodeAnalysis.CSharp.Syntax.PropertyDeclarationSyntax pd1 when pd1.Type != null:
                edits.Add((pd1.SyntaxTree.FilePath, pd1.Type.Span.End, "?"));
                break;
            default:
                Bump("找不到成员声明语法");
                continue;
        }
        stripNullBang.Add((fld ?? (ISymbol)prop!).Name);
        Bump("待改（成员 " + (fld ?? (ISymbol)prop!).Name + "）");
        continue;
    }

    if (mode == "cs8625f")
    {
        // 站点 = `字段 = null;`（**清理式赋值**：Destroy/Release 之后归零）⇒ **字段契约改真**：
        // 声明标 `T?`，并把批 1 为消 CS8618 而加的 `= null!; // …` 初始器**撤掉**（那是本片要修的那句谎）。
        var tk = tree.GetRoot().FindToken(text.Lines[line - 1].Start + Math.Max(0, col - 1));
        Microsoft.CodeAnalysis.CSharp.Syntax.AssignmentExpressionSyntax? asg = null;
        for (var n = tk.Parent; n != null; n = n.Parent)
            if (n is Microsoft.CodeAnalysis.CSharp.Syntax.AssignmentExpressionSyntax a) { asg = a; break; }
        if (asg == null) { Bump("非赋值形态"); continue; }
        var fsym = model.GetSymbolInfo(asg.Left).Symbol as IFieldSymbol;
        if (fsym == null) { Bump("左侧非字段"); continue; }
        if (!fsym.Type.IsReferenceType && fsym.Type.TypeKind != TypeKind.TypeParameter) { Bump("值类型字段"); continue; }
        if (fsym.Type.NullableAnnotation == NullableAnnotation.Annotated) { Bump("字段已是可空"); continue; }
        var fdecl = fsym.DeclaringSyntaxReferences.Select(r => r.GetSyntax())
            .OfType<Microsoft.CodeAnalysis.CSharp.Syntax.VariableDeclaratorSyntax>().FirstOrDefault();
        if (fdecl?.Parent is not Microsoft.CodeAnalysis.CSharp.Syntax.VariableDeclarationSyntax fv) { Bump("找不到字段声明"); continue; }
        edits.Add((file, fv.Type.Span.End, "?"));
        stripNullBang.Add(fsym.Name);
        Bump("待改（字段 " + fsym.Name + "）");
        continue;
    }

    if (mode == "cs8625")
    {
        // 本站点 = `null` 所在处。**主流形态 = 参数默认值**（`void F(Map map = null)`）⇒ 形参本就该可空。
        // 实参形态（`F(null)` 传给非空形参）要判"改形参还是改实参"，**不做**，报告留给人工。
        var node = tree.GetRoot().FindToken(text.Lines[line - 1].Start + Math.Max(0, col - 1)).Parent;
        Microsoft.CodeAnalysis.CSharp.Syntax.ParameterSyntax? ps = null;
        for (var n = node; n != null; n = n.Parent)
        {
            if (n is Microsoft.CodeAnalysis.CSharp.Syntax.ParameterSyntax p) { ps = p; break; }
            if (n is Microsoft.CodeAnalysis.CSharp.Syntax.ArgumentSyntax) break;   // 实参形态 ⇒ 跳过
        }
        if (ps == null) { Bump("非默认值形态（实参/字段初始化等）"); continue; }
        var psym = model.GetDeclaredSymbol(ps) as IParameterSymbol;
        if (psym == null) { Bump("非参数符号"); continue; }
        if (!psym.Type.IsReferenceType && psym.Type.TypeKind != TypeKind.TypeParameter) { Bump("值类型参数"); continue; }
        if (psym.Type.NullableAnnotation == NullableAnnotation.Annotated) { Bump("已是可空"); continue; }
        if (ps.Type == null) { Bump("参数无类型语法"); continue; }
        edits.Add((file, ps.Type.Span.End, "?"));
        Bump("待改（" + psym.Name + "）");
        continue;
    }

    if (mode == "cs8618")
    {
        Microsoft.CodeAnalysis.CSharp.Syntax.VariableDeclaratorSyntax? decl = null;
        foreach (var d in tree.GetRoot().DescendantNodes().OfType<Microsoft.CodeAnalysis.CSharp.Syntax.VariableDeclaratorSyntax>())
            if (d.Identifier.ValueText == member) { decl = d; break; }
        if (decl == null) { Bump("找不到字段声明"); continue; }
        var sym = model.GetDeclaredSymbol(decl) as IFieldSymbol;
        if (sym == null) { Bump("非字段符号"); continue; }
        if (!sym.Type.IsReferenceType && sym.Type.TypeKind != TypeKind.TypeParameter) { Bump("值类型字段"); continue; }
        if (decl.Initializer != null) { Bump("已有初始化"); continue; }
        edits.Add((file, decl.Identifier.Span.End, " = null!;   // null!：引擎/Def/存档绑定（N2 可空族家规）"));
        Bump("待改（" + member + "）");
        continue;
    }

    var s2 = model.GetEnclosingSymbol(span.Start);
    if (s2 is not IMethodSymbol m) { Bump("非方法符号"); continue; }
    if (m.MethodKind != MethodKind.Ordinary) { Bump("非普通方法（" + m.MethodKind + "）"); continue; }
    if (m.ReturnsVoid) { Bump("void"); continue; }
    var rt = m.ReturnType;
    if (!rt.IsReferenceType && rt.TypeKind != TypeKind.TypeParameter) { Bump("值类型返回（" + rt.Name + "）"); continue; }
    if (rt.NullableAnnotation == NullableAnnotation.Annotated) { Bump("已是可空"); continue; }
    var decl2 = m.DeclaringSyntaxReferences.Select(r => r.GetSyntax()).OfType<Microsoft.CodeAnalysis.CSharp.Syntax.MethodDeclarationSyntax>().FirstOrDefault();
    if (decl2 == null) { Bump("找不到声明语法"); continue; }
    edits.Add((file, decl2.ReturnType.Span.End, "?"));
    Bump("待改（" + m.Name + "）");
}

var toEdit = edits.Distinct().ToList();
Console.WriteLine($"待改：{toEdit.Count} 处（去重后）");
foreach (var g in stats.GroupBy(kv => kv.Key.StartsWith("待改") ? "待改" : kv.Key))
    Console.WriteLine($"  {g.Key,-24} {g.Sum(kv => kv.Value)}");

foreach (var g in toEdit.GroupBy(e => e.file))
{
    var text = File.ReadAllText(g.Key);
    var hadBom = text.StartsWith('\ufeff');     // ⚠ 首版用 WriteAllText 裸写 ⇒ BOM 被剥（14 个文件 churn）
    if (hadBom)
        text = text[1..];
    var sb = new StringBuilder(text);
    foreach (var (_, pos, ins) in g.OrderByDescending(e => e.pos))
        sb.Insert(hadBom ? pos + 1 : pos, ins);
    var outp = sb.ToString();
    foreach (var nm in stripNullBang)
        outp = System.Text.RegularExpressions.Regex.Replace(outp,
            @"(?m)^(\s*(?:public|private|protected|internal|static|readonly|volatile|\s)*[\w\.<>\[\],\?]+\s+" + nm + @")\s*=\s*null!;\s*//[^
]*$", "$1;");
    if (!dry)
        File.WriteAllText(g.Key, outp, new System.Text.UTF8Encoding(hadBom));
    Console.WriteLine($"  {(dry ? "[dry] " : "")}{Path.GetFileName(g.Key)}: +{g.Count()} 个 ?");
}
if (dry) Console.WriteLine("（dry-run：未写盘）");
