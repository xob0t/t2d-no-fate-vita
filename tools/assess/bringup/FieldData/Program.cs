// FieldData: write the initial data of every field with an RVA (the array-initializer blobs in
// <PrivateImplementationDetails>) of an assembly to <out dir>/<field name>.bin, for the probe's
// __Rewind.InitArray (Rewind's C# names these fields in FieldToken markers but has no way to carry
// their bytes in source).
//   FieldData <assembly.dll> <out dir>
using Mono.Cecil;

if (args.Length != 2)
{
    Console.Error.WriteLine("usage: FieldData <assembly.dll> <out dir>");
    return 2;
}
Directory.CreateDirectory(args[1]);
using var module = ModuleDefinition.ReadModule(args[0]);
int n = 0;
long bytes = 0;
foreach (var type in module.GetTypes())
    foreach (var field in type.Fields)
    {
        if (!field.HasFieldRVA || field.InitialValue == null || field.InitialValue.Length == 0) continue;
        File.WriteAllBytes(Path.Combine(args[1], field.Name + ".bin"), field.InitialValue);
        n++;
        bytes += field.InitialValue.Length;
    }
Console.WriteLine($"{n} fields, {bytes} bytes written to {args[1]}");
return 0;
