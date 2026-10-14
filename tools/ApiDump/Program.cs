// ApiDump — throwaway metadata inspector for the RimWorld 1.7 migration audit.
// NOT part of the mod source. Lives in D:\tmp on purpose.
//
// Modes:
//   types   <dll> [nsPrefix]              list type full names
//   members <dll> <typeFullName> [...]    list members of the given types
//   nsmembers <dll> <nsPrefix> [...]      list members of all types under ns prefixes
//   refs    <dll> [assemblyPrefix]        list referenced (TypeRef/MemberRef) members grouped by target assembly
//
// Output lines are TSV, machine-diffable:
//   T<TAB>asesemblyOrUnit<TAB>FullName
//   M<TAB>FullName<TAB>methodName<TAB>arity<TAB>signature
//   P<TAB>FullName<TAB>propName<TAB>get,set
//   F<TAB>FullName<TAB>fieldName<TAB>type
//   E<TAB>FullName<TAB>eventName<TAB>type
using System.Collections.Immutable;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

static class Program
{
    static int Main(string[] args)
    {
        if (args.Length < 2) { Console.Error.WriteLine("usage: ApiDump <mode> <dll> [...]"); return 2; }
        string mode = args[0];
        string dll = args[1];
        using var fs = File.OpenRead(dll);
        using var pe = new PEReader(fs);
        var md = pe.GetMetadataReader();
        var p = new Prov(md);

        switch (mode)
        {
            case "types":
            {
                string prefix = args.Length > 2 ? args[2] : "";
                foreach (var h in md.TypeDefinitions)
                {
                    var td = md.GetTypeDefinition(h);
                    string full = FullName(md, td);
                    if (full.StartsWith(prefix)) Console.WriteLine("T\t-\t" + full);
                }
                break;
            }
            case "members":
            {
                var wanted = new HashSet<string>(args.Skip(2));
                foreach (var h in md.TypeDefinitions)
                {
                    var td = md.GetTypeDefinition(h);
                    string full = FullName(md, td);
                    if (!wanted.Contains(full)) continue;
                    DumpMembers(md, td, full, p);
                }
                break;
            }
            case "nsmembers":
            {
                var prefixes = args.Skip(2).ToArray();
                foreach (var h in md.TypeDefinitions)
                {
                    var td = md.GetTypeDefinition(h);
                    string full = FullName(md, td);
                    if (!prefixes.Any(x => full.StartsWith(x))) continue;
                    DumpMembers(md, td, full, p);
                }
                break;
            }
            case "refs":
            {
                string asmPrefix = args.Length > 2 ? args[2] : "";
                // group: target assembly -> lines
                foreach (var h in md.TypeReferences)
                {
                    var tr = md.GetTypeReference(h);
                    string scope = ScopeName(md, tr);
                    if (!scope.StartsWith(asmPrefix)) continue;
                    string full = RefFullName(md, tr);
                    Console.WriteLine("RT\t" + scope + "\t" + full);
                }
                foreach (var h in md.MemberReferences)
                {
                    var mr = md.GetMemberReference(h);
                    string scope, parentName;
                    if (mr.Parent.Kind == HandleKind.TypeReference)
                    {
                        var tr = md.GetTypeReference((TypeReferenceHandle)mr.Parent);
                        scope = ScopeName(md, tr);
                        parentName = RefFullName(md, tr);
                    }
                    else if (mr.Parent.Kind == HandleKind.TypeSpecification)
                    {
                        var ts = md.GetTypeSpecification((TypeSpecificationHandle)mr.Parent);
                        parentName = ts.DecodeSignature(p, null);
                        scope = "spec";
                    }
                    else if (mr.Parent.Kind == HandleKind.TypeDefinition)
                    {
                        var td = md.GetTypeDefinition((TypeDefinitionHandle)mr.Parent);
                        scope = "self";
                        parentName = FullName(md, td);
                    }
                    else continue;
                    if (!scope.StartsWith(asmPrefix)) continue;
                    string name = md.GetString(mr.Name);
                    string sig;
                    int arity;
                    try
                    {
                        if (mr.GetKind() == MemberReferenceKind.Field)
                        {
                            sig = mr.DecodeFieldSignature(p, null);
                            arity = -1;
                        }
                        else
                        {
                            var ms = mr.DecodeMethodSignature(p, null);
                            var pars = ms.ParameterTypes.ToArray();
                            arity = pars.Length;
                            sig = "(" + string.Join(", ", pars) + ") : " + ms.ReturnType;
                        }
                    }
                    catch (Exception e) { sig = "<" + e.GetType().Name + ">"; arity = -2; }
                    Console.WriteLine("RM\t" + scope + "\t" + parentName + "\t" + name + "\t" + arity + "\t" + sig);
                }
                break;
            }
            default:
                Console.Error.WriteLine("unknown mode " + mode);
                return 2;
        }
        return 0;
    }

    static void DumpMembers(MetadataReader md, TypeDefinition td, string full, Prov p)
    {
        Console.WriteLine("T\t-\t" + full);
        foreach (var fh in td.GetFields())
        {
            var f = md.GetFieldDefinition(fh);
            string sig;
            try { sig = f.DecodeSignature(p, null); } catch { sig = "?"; }
            Console.WriteLine("F\t" + full + "\t" + md.GetString(f.Name) + "\t" + sig);
        }
        foreach (var ph in td.GetProperties())
        {
            var pr = md.GetPropertyDefinition(ph);
            var acc = pr.GetAccessors();
            var kinds = new List<string>();
            if (!acc.Getter.IsNil) kinds.Add("get");
            if (!acc.Setter.IsNil) kinds.Add("set");
            Console.WriteLine("P\t" + full + "\t" + md.GetString(pr.Name) + "\t" + string.Join(",", kinds));
        }
        foreach (var eh in td.GetEvents())
        {
            var e = md.GetEventDefinition(eh);
            Console.WriteLine("E\t" + full + "\t" + md.GetString(e.Name) + "\t-");
        }
        foreach (var mh in td.GetMethods())
        {
            var m = md.GetMethodDefinition(mh);
            string name = md.GetString(m.Name);
            if (name.StartsWith("<")) continue; // compiler-generated
            string sig; int arity;
            try
            {
                var ms = m.DecodeSignature(p, null);
                var pars = ms.ParameterTypes.ToArray();
                arity = pars.Length;
                sig = "(" + string.Join(", ", pars) + ") : " + ms.ReturnType;
            }
            catch { sig = "?"; arity = -2; }
            bool stat = (m.Attributes & MethodAttributes.Static) != 0;
            Console.WriteLine("M\t" + full + "\t" + name + "\t" + arity + "\t" + (stat ? "static " : "") + sig);
        }
    }

    static string FullName(MetadataReader md, TypeDefinition td)
    {
        string ns = md.GetString(td.Namespace);
        string n = md.GetString(td.Name);
        string full = string.IsNullOrEmpty(ns) ? n : ns + "." + n;
        // nested
        var decl = td.GetDeclaringType();
        if (!decl.IsNil) full = FullName(md, md.GetTypeDefinition(decl)) + "/" + n;
        return full;
    }

    static string RefFullName(MetadataReader md, TypeReference tr)
    {
        string ns = md.GetString(tr.Namespace);
        string n = md.GetString(tr.Name);
        string full = string.IsNullOrEmpty(ns) ? n : ns + "." + n;
        if (tr.ResolutionScope.Kind == HandleKind.TypeReference)
            full = RefFullName(md, md.GetTypeReference((TypeReferenceHandle)tr.ResolutionScope)) + "/" + n;
        return full;
    }

    static string ScopeName(MetadataReader md, TypeReference tr)
    {
        switch (tr.ResolutionScope.Kind)
        {
            case HandleKind.AssemblyReference:
                return md.GetString(md.GetAssemblyReference((AssemblyReferenceHandle)tr.ResolutionScope).Name);
            case HandleKind.TypeReference:
                return ScopeName(md, md.GetTypeReference((TypeReferenceHandle)tr.ResolutionScope));
            case HandleKind.ModuleDefinition:
                return "<module>";
            default:
                return "<other>";
        }
    }

    sealed class Prov : ISignatureTypeProvider<string, object>
    {
        readonly MetadataReader md;
        public Prov(MetadataReader md) { this.md = md; }
        public string GetPrimitiveType(PrimitiveTypeCode c) => c switch
        {
            PrimitiveTypeCode.Void => "void",
            PrimitiveTypeCode.Boolean => "bool",
            PrimitiveTypeCode.Char => "char",
            PrimitiveTypeCode.SByte => "sbyte",
            PrimitiveTypeCode.Byte => "byte",
            PrimitiveTypeCode.Int16 => "short",
            PrimitiveTypeCode.UInt16 => "ushort",
            PrimitiveTypeCode.Int32 => "int",
            PrimitiveTypeCode.UInt32 => "uint",
            PrimitiveTypeCode.Int64 => "long",
            PrimitiveTypeCode.UInt64 => "ulong",
            PrimitiveTypeCode.Single => "float",
            PrimitiveTypeCode.Double => "double",
            PrimitiveTypeCode.String => "string",
            PrimitiveTypeCode.IntPtr => "IntPtr",
            PrimitiveTypeCode.UIntPtr => "UIntPtr",
            PrimitiveTypeCode.Object => "object",
            _ => c.ToString()
        };
        public string GetTypeFromDefinition(MetadataReader r, TypeDefinitionHandle h, byte raw) => FullName(r, r.GetTypeDefinition(h));
        public string GetTypeFromReference(MetadataReader r, TypeReferenceHandle h, byte raw) => RefFullName(r, r.GetTypeReference(h));
        public string GetTypeFromSpecification(MetadataReader r, object g, TypeSpecificationHandle h, byte raw)
            => r.GetTypeSpecification(h).DecodeSignature(this, g);
        public string GetSZArrayType(string e) => e + "[]";
        public string GetArrayType(string e, ArrayShape s) => e + "[" + new string(',', Math.Max(0, s.Rank - 1)) + "]";
        public string GetByReferenceType(string e) => e + "&";
        public string GetPointerType(string e) => e + "*";
        public string GetGenericInstantiation(string g, ImmutableArray<string> a) => g + "<" + string.Join(", ", a) + ">";
        public string GetGenericMethodParameter(object g, int i) => "!!" + i;
        public string GetGenericTypeParameter(object g, int i) => "!" + i;
        public string GetModifiedType(string mod, string unmodified, bool req) => unmodified;
        public string GetPinnedType(string e) => e;
        public string GetFunctionPointerType(MethodSignature<string> s) => "fnptr";
    }
}
