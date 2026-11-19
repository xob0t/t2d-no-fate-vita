// RoslynDiag: compile a csc response file with the Roslyn API and write every diagnostic as a row
// of a TSV, with the member (method, constructor, accessor, field) and type it is in.
//
//   RoslynDiag <file.rsp> <out.tsv> [--dep <dep.rsp>]... [--depdll <dll>]... [--stub-errors <stubbed.tsv>]
//              [--emit <out.dll>]
//
// --dep compiles another response file (a dependency's source) and references it as a compilation,
// so its errors don't matter and no DLL of it is needed; --depdll references a built one.
// --stub-errors replaces the body of every member that has an error with `{ throw null; }` (and a
// constructor initializer's arguments with `default`), recompiles, repeats until no errors are left
// in bodies, lists the stubbed members in <stubbed.tsv>, and writes the diagnostics left after that.
// For measuring only: it lets the rest of the assembly emit.
//
// csc stops after parse and declaration errors before it binds method bodies; GetDiagnostics() does
// not, so the TSV holds the errors csc would show in later rounds as well.
// Columns: code, severity, file, line, col, type, member, markers, message, text (the span, at most
// 160 chars). markers: "gap" when the member's text has __Rewind.Opaque or __Rewind.Effect, "note" when
// it has only __Rewind.Note, "" otherwise.
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

if (args.Length < 2)
{
    Console.Error.WriteLine("usage: RoslynDiag <file.rsp> <out.tsv> [--emit <out.dll>]");
    return 2;
}
string rsp = Path.GetFullPath(args[0]);
string outTsv = args[1];
string? emit = null, stubOut = null;
var deps = new List<string>();
var depDlls = new List<string>();
for (int i = 2; i + 1 < args.Length; i += 2)
{
    if (args[i] == "--emit") emit = args[i + 1];
    else if (args[i] == "--dep") deps.Add(Path.GetFullPath(args[i + 1]));
    else if (args[i] == "--depdll") depDlls.Add(Path.GetFullPath(args[i + 1]));
    else if (args[i] == "--stub-errors") stubOut = args[i + 1];
    else { Console.Error.WriteLine("unknown option " + args[i]); return 2; }
}

var depRefs = deps.Select(d => (MetadataReference)Load(d, new List<MetadataReference>()).ToMetadataReference()).ToList();
depRefs.AddRange(depDlls.Select(d => (MetadataReference)MetadataReference.CreateFromFile(d)));
var compilation = Load(rsp, depRefs);

static CSharpCompilation Load(string rsp, List<MetadataReference> extra)
{
    var parsed = CSharpCommandLineParser.Default.Parse(new[] { "@" + rsp }, Path.GetDirectoryName(rsp)!, sdkDirectory: null);
    foreach (var d in parsed.Errors) Console.Error.WriteLine(d);
    var parseOptions = parsed.ParseOptions;
    var trees = parsed.SourceFiles
        .AsParallel().AsOrdered()
        .Select(f => CSharpSyntaxTree.ParseText(SourceText.From(File.ReadAllText(f.Path), Encoding.UTF8), parseOptions, f.Path))
        .ToList();
    var refs = parsed.MetadataReferences.Select(r => (MetadataReference)MetadataReference.CreateFromFile(r.Reference)).ToList();
    refs.AddRange(extra);
    return CSharpCompilation.Create(
        Path.GetFileNameWithoutExtension(parsed.OutputFileName ?? "out"), trees, refs,
        parsed.CompilationOptions.WithConcurrentBuild(true));
}

var sw = System.Diagnostics.Stopwatch.StartNew();
var diags = compilation.GetDiagnostics();
Console.Error.WriteLine($"diagnostics: {diags.Length} in {sw.ElapsedMilliseconds} ms");

if (stubOut != null)
{
    var stubbed = new List<string>();
    for (int round = 1; round <= 6; round++)
    {
        var errs = diags.Where(d => d.Severity == DiagnosticSeverity.Error && d.Location.IsInSource).ToList();
        if (errs.Count == 0) break;
        int n = 0;
        foreach (var group in errs.GroupBy(d => d.Location.SourceTree!))
        {
            var tree = group.Key;
            var root = tree.GetRoot();
            var targets = new HashSet<SyntaxNode>();
            foreach (var d in group)
            {
                var node = root.FindToken(d.Location.SourceSpan.Start).Parent;
                if (d.Id == "CS7036" && node is ConstructorDeclarationSyntax cd0 && cd0.Initializer == null)
                {
                    targets.Add(cd0);
                    continue;
                }
                bool inBody = node!.AncestorsAndSelf().Any(a => a is BlockSyntax or ArrowExpressionClauseSyntax);
                if (d.Id == "CS0104" && !inBody && node is IdentifierNameSyntax amb)
                {
                    targets.Add(amb);
                    continue;
                }
                for (var x = node; x != null; x = x.Parent)
                {
                    if (x is ConstructorInitializerSyntax ci) { targets.Add(ci); break; }
                    if (x is BaseMethodDeclarationSyntax or AccessorDeclarationSyntax or LocalFunctionStatementSyntax or AnonymousFunctionExpressionSyntax)
                    {
                        // the outermost member, not a lambda or local function inside it
                        var outer = x.AncestorsAndSelf().LastOrDefault(a => a is BaseMethodDeclarationSyntax or AccessorDeclarationSyntax);
                        if (outer != null) targets.Add(outer);
                        break;
                    }
                }
            }
            targets.RemoveWhere(t => targets.Any(o => o != t && o.Span.Contains(t.Span) && o is not ConstructorInitializerSyntax && t is not ConstructorInitializerSyntax));
            if (targets.Count == 0) continue;
            var throwBody = SyntaxFactory.Block(SyntaxFactory.ParseStatement("throw null;"));
            var newRoot = root.ReplaceNodes(targets, (orig, cur) =>
            {
                switch (cur)
                {
                    case ConstructorInitializerSyntax ci:
                    {
                        // arguments as `default`; as many as the base constructor wants, when that's the error
                        int count = ci.ArgumentList.Arguments.Count;
                        var e = errs.FirstOrDefault(e => e.Id == "CS7036" && e.Location.SourceTree == tree && orig.Span.Contains(e.Location.SourceSpan.Start));
                        if (e != null)
                        {
                            var msg = e.GetMessage();
                            int open = msg.LastIndexOf('('), close = msg.LastIndexOf(')');
                            count = close > open + 1 ? msg.Substring(open + 1, close - open - 1).Split(',').Length : 0;
                        }
                        return ci.WithArgumentList(SyntaxFactory.ArgumentList(SyntaxFactory.SeparatedList(
                            Enumerable.Range(0, count).Select(_ => SyntaxFactory.Argument(SyntaxFactory.ParseExpression("default"))))));
                    }
                    case IdentifierNameSyntax id when orig is IdentifierNameSyntax:
                    {
                        // CS0104 outside a body: the first candidate from another namespace than UnityEngine/System
                        var e = errs.First(e => e.Id == "CS0104" && e.Location.SourceTree == tree && e.Location.SourceSpan == orig.Span);
                        var names = System.Text.RegularExpressions.Regex.Matches(e.GetMessage(), "'([^']+)'").Select(m => m.Groups[1].Value).Skip(1).ToList();
                        var pick = names.FirstOrDefault(n => !n.StartsWith("UnityEngine.") && !n.StartsWith("System.")) ?? names.First();
                        return SyntaxFactory.ParseTypeName("global::" + pick).WithTriviaFrom(id);
                    }
                    case ConstructorDeclarationSyntax c when c.Initializer == null && orig is ConstructorDeclarationSyntax oc
                                                             && errs.Any(e => e.Id == "CS7036" && e.Location.SourceTree == tree && oc.Identifier.Span.Contains(e.Location.SourceSpan.Start)):
                    {
                        // the base constructor's parameter count, from "... of 'B.B(int, int)'"
                        var e = errs.First(e => e.Id == "CS7036" && e.Location.SourceTree == tree && oc.Identifier.Span.Contains(e.Location.SourceSpan.Start));
                        var msg = e.GetMessage();
                        int open = msg.LastIndexOf('('), close = msg.LastIndexOf(')');
                        int count = close > open + 1 ? msg.Substring(open + 1, close - open - 1).Split(',').Length : 0;
                        var init = SyntaxFactory.ConstructorInitializer(SyntaxKind.BaseConstructorInitializer,
                            SyntaxFactory.ArgumentList(SyntaxFactory.SeparatedList(Enumerable.Range(0, count).Select(_ => SyntaxFactory.Argument(SyntaxFactory.ParseExpression("default"))))));
                        return c.WithInitializer(init).WithBody(throwBody).WithExpressionBody(null).WithSemicolonToken(default);
                    }
                    case BaseMethodDeclarationSyntax m when m.Body != null || m.ExpressionBody != null:
                        return m.WithBody(throwBody).WithExpressionBody(null).WithSemicolonToken(default);
                    case AccessorDeclarationSyntax a when a.Body != null || a.ExpressionBody != null:
                        return a.WithBody(throwBody).WithExpressionBody(null).WithSemicolonToken(default);
                }
                return cur;
            });
            foreach (var t in targets)
            {
                var memberNode = t is ConstructorInitializerSyntax ? t.Parent! : t;
                string memberName = MemberOf(memberNode, out var ty, out _);
                stubbed.Add($"{round}	{tree.FilePath}	{ty}	{memberName}	{(t is ConstructorInitializerSyntax ? "ctor-initializer" : "body")}");
            }
            compilation = compilation.ReplaceSyntaxTree(tree, tree.WithRootAndOptions(newRoot, tree.Options));
            n += targets.Count;
        }
        diags = compilation.GetDiagnostics();
        Console.Error.WriteLine($"stub round {round}: {n} members stubbed; {diags.Count(d => d.Severity == DiagnosticSeverity.Error)} errors left");
        if (n == 0) break;
    }
    File.WriteAllLines(stubOut, new[] { "round	file	type	member	what" }.Concat(stubbed));
}

static string MemberOf(SyntaxNode? node, out string type, out SyntaxNode? memberNode)
{
    type = "";
    memberNode = null;
    string member = "";
    for (var n = node; n != null; n = n.Parent)
    {
        if (member == "")
        {
            memberNode = n;
            switch (n)
            {
                case MethodDeclarationSyntax m: member = m.Identifier.Text; break;
                case ConstructorDeclarationSyntax c: member = c.Modifiers.Any(SyntaxKind.StaticKeyword) ? ".cctor" : ".ctor"; break;
                case DestructorDeclarationSyntax: member = "Finalize"; break;
                case OperatorDeclarationSyntax o: member = "op " + o.OperatorToken.Text; break;
                case ConversionOperatorDeclarationSyntax cv: member = "op " + cv.Type; break;
                case AccessorDeclarationSyntax acc when acc.Parent?.Parent is BasePropertyDeclarationSyntax p:
                    member = acc.Keyword.Text + "_" + (p is PropertyDeclarationSyntax pp ? pp.Identifier.Text : p is EventDeclarationSyntax e ? e.Identifier.Text : "Item");
                    break;
                case PropertyDeclarationSyntax p2: member = p2.Identifier.Text; break;
                case IndexerDeclarationSyntax: member = "Item"; break;
                case EventDeclarationSyntax e2: member = e2.Identifier.Text; break;
                case FieldDeclarationSyntax f: member = "field " + string.Join(",", f.Declaration.Variables.Select(v => v.Identifier.Text)); break;
                case EventFieldDeclarationSyntax ef: member = "event " + string.Join(",", ef.Declaration.Variables.Select(v => v.Identifier.Text)); break;
                case DelegateDeclarationSyntax dd: member = "delegate " + dd.Identifier.Text; break;
                case EnumMemberDeclarationSyntax em: member = "enum " + em.Identifier.Text; break;
            }
        }
        if (n is BaseTypeDeclarationSyntax t)
        {
            var names = new List<string> { t.Identifier.Text };
            for (var p = t.Parent; p != null; p = p.Parent)
            {
                if (p is BaseTypeDeclarationSyntax pt) names.Insert(0, pt.Identifier.Text);
                else if (p is BaseNamespaceDeclarationSyntax ns) { names.Insert(0, ns.Name.ToString()); }
            }
            type = string.Join(".", names);
            break;
        }
    }
    if (member == "") memberNode = null;
    return member;
}

static string Clean(string s) => s.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');

using (var w = new StreamWriter(outTsv, false, new UTF8Encoding(false)))
{
    w.WriteLine("code\tseverity\tfile\tline\tcol\ttype\tmember\tmarkers\tmessage\ttext");
    var markerCache = new Dictionary<SyntaxNode, string>();
    foreach (var d in diags.Where(d => d.Severity >= DiagnosticSeverity.Warning || d.IsWarningAsError).OrderBy(d => d.Location.SourceTree?.FilePath).ThenBy(d => d.Location.SourceSpan.Start))
    {
        if (d.Severity != DiagnosticSeverity.Error) continue;
        string file = "", text = "", type = "", member = "", markers = "";
        int line = 0, col = 0;
        if (d.Location.IsInSource)
        {
            var tree = d.Location.SourceTree!;
            file = tree.FilePath;
            var pos = d.Location.GetLineSpan().StartLinePosition;
            line = pos.Line + 1; col = pos.Character + 1;
            var span = d.Location.SourceSpan;
            text = tree.GetText().ToString(span);
            if (text.Length > 160) text = text[..160];
            var root = tree.GetRoot();
            var node = root.FindToken(span.Start).Parent;
            member = MemberOf(node, out type, out var memberNode);
            if (memberNode != null && !markerCache.TryGetValue(memberNode, out markers!))
            {
                string body = memberNode.ToString();
                markers = body.Contains("__Rewind.Opaque") || body.Contains("__Rewind.Effect") ? "gap" : body.Contains("__Rewind.Note") ? "note" : "";
                markerCache[memberNode] = markers;
            }
        }
        w.WriteLine(string.Join("\t", d.Id, d.Severity, file, line, col, type, Clean(member), markers, Clean(d.GetMessage()), Clean(text)));
    }
}

int errors = diags.Count(d => d.Severity == DiagnosticSeverity.Error);
Console.WriteLine($"{compilation.AssemblyName}: {errors} errors");
if (emit != null)
{
    if (errors != 0) { Console.WriteLine("not emitted: errors"); return 1; }
    using var fs = File.Create(emit);
    var r = compilation.Emit(fs);
    Console.WriteLine(r.Success ? $"emitted {emit}" : "emit failed");
    foreach (var d in r.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)) Console.WriteLine(d);
    return r.Success ? 0 : 1;
}
return errors == 0 ? 0 : 1;
