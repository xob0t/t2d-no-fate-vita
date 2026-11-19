using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

// Lists where a game assembly uses a platform-facing API, for docs/platform.md's inventory: every call, field access,
// ldtoken and newobj whose member belongs to a type matching one of the prefixes, with the methods that use it; then
// the game's types that derive from or hold fields of such types.
// Reads Rewind's assemblies; prints only member names and the game's own method names, no code.
//
//   PlatformCheck.exe inventory <assembly.dll> <out.tsv> <type prefix>...
//
// A prefix matches a declaring type's full name from its start ("InControl." or "UnityEngine.Screen").
// The TSV has one row per member: prefix, member, kind (call, callvirt, newobj, ldfld, ...), sites, users.
static partial class PlatformCheck
{
    static int Inventory(string[] args)
    {
        if (args.Length < 3)
        {
            Console.Error.WriteLine("usage: PlatformCheck.exe inventory <assembly.dll> <out.tsv> <type prefix>...");
            return 2;
        }
        string[] prefixes = args.Skip(2).ToArray();
        var rows = new Dictionary<string, Row>();
        var structural = new SortedSet<string>(StringComparer.Ordinal);
        using (ModuleDefinition module = ModuleDefinition.ReadModule(args[0]))
        {
            foreach (TypeReference reference in module.GetTypeReferences())
                if (Prefix(reference, prefixes) != null) structural.Add("typeref\t" + reference.FullName);
            foreach (TypeDefinition type in AllTypes(module.Types))
            {
                string owner = type.FullName;
                if (type.BaseType != null && Prefix(type.BaseType, prefixes) != null)
                    structural.Add("base\t" + owner + " : " + type.BaseType.FullName);
                foreach (FieldDefinition field in type.Fields)
                    if (Prefix(field.FieldType, prefixes) != null)
                        structural.Add("field\t" + owner + "::" + field.Name + " : " + field.FieldType.FullName);
                foreach (MethodDefinition method in type.Methods)
                {
                    if (!method.HasBody) continue;
                    string user = Short(owner) + "." + method.Name;
                    foreach (Instruction instruction in method.Body.Instructions)
                    {
                        var member = instruction.Operand as MemberReference;
                        if (member == null) continue;
                        TypeReference declaring = member is TypeReference ? (TypeReference)member : member.DeclaringType;
                        if (declaring == null) continue;
                        string prefix = Prefix(declaring, prefixes);
                        if (prefix == null) continue;
                        string name = Describe(member);
                        string kind = instruction.OpCode.Name;
                        Row row;
                        if (!rows.TryGetValue(name, out row)) rows[name] = row = new Row { Prefix = prefix, Member = name };
                        row.Kinds.Add(kind);
                        row.Sites++;
                        row.Users.Add(user);
                    }
                }
            }
        }
        using (var writer = new StreamWriter(args[1]))
        {
            writer.WriteLine("prefix\tmember\tkind\tsites\tusers");
            foreach (Row row in rows.Values.OrderBy(r => r.Prefix).ThenBy(r => r.Member, StringComparer.Ordinal))
                writer.WriteLine(row.Prefix + "\t" + row.Member + "\t" + string.Join(",", row.Kinds) + "\t" + row.Sites + "\t" + string.Join(" ", row.Users));
            foreach (string line in structural) writer.WriteLine("(type)\t" + line);
        }
        foreach (IGrouping<string, Row> group in rows.Values.GroupBy(r => r.Prefix).OrderBy(g => g.Key))
            Console.WriteLine(group.Key + ": " + group.Count() + " members, " + group.Sum(r => r.Sites) + " sites, " +
                              group.SelectMany(r => r.Users).Distinct().Count() + " methods");
        Console.WriteLine("types deriving from or holding them: " + structural.Count);
        return 0;
    }

    class Row
    {
        public string Prefix, Member;
        public int Sites;
        public readonly SortedSet<string> Kinds = new SortedSet<string>(StringComparer.Ordinal);
        public readonly SortedSet<string> Users = new SortedSet<string>(StringComparer.Ordinal);
    }

    static string Prefix(TypeReference type, string[] prefixes)
    {
        while (type is TypeSpecification) type = ((TypeSpecification)type).ElementType;
        if (type.IsGenericParameter) return null;
        TypeReference top = type;
        while (top.DeclaringType != null) top = top.DeclaringType;
        string full = top.FullName;
        return prefixes.FirstOrDefault(p => full.StartsWith(p, StringComparison.Ordinal));
    }

    // The member as the rule tables name it: Type::Name(parameter types) for methods, Type::Name for fields and types.
    static string Describe(MemberReference member)
    {
        var method = member as MethodReference;
        if (method != null)
        {
            MethodReference element = method is GenericInstanceMethod ? ((GenericInstanceMethod)method).ElementMethod : method;
            return element.ReturnType.FullName + " " + element.DeclaringType.FullName + "::" + element.Name + "(" +
                   string.Join(",", element.Parameters.Select(p => p.ParameterType.FullName)) + ")";
        }
        var field = member as FieldReference;
        if (field != null) return field.FieldType.FullName + " " + field.DeclaringType.FullName + "::" + field.Name;
        return member.FullName;
    }


    static string Short(string fullName)
    {
        int dot = fullName.LastIndexOf('.');
        return dot < 0 ? fullName : fullName.Substring(dot + 1);
    }

    static IEnumerable<TypeDefinition> AllTypes(IEnumerable<TypeDefinition> types)
    {
        foreach (TypeDefinition type in types)
        {
            yield return type;
            foreach (TypeDefinition nested in AllTypes(type.NestedTypes)) yield return nested;
        }
    }
}
