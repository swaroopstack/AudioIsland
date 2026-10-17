using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

// Prints public members with decoded signatures (namespace-qualified).
var path = args[0];
var filter = args.Length > 1 ? args[1] : "";
using var fs = File.OpenRead(path);
using var pe = new PEReader(fs);
var md = pe.GetMetadataReader();
foreach (var h in md.TypeDefinitions)
{
    var t = md.GetTypeDefinition(h);
    var name = md.GetString(t.Name);
    if (filter.Length > 0 && !name.Contains(filter, StringComparison.OrdinalIgnoreCase)) continue;
    var vis = (t.Attributes & System.Reflection.TypeAttributes.VisibilityMask) switch
    {
        System.Reflection.TypeAttributes.Public => "public",
        System.Reflection.TypeAttributes.NestedPublic => "nested-public",
        _ => "internal",
    };
    if (vis == "internal") continue;
    var ns = md.GetString(t.Namespace);
    Console.WriteLine($"{ns}.{name} [{vis}]");
    foreach (var mh in t.GetMethods())
    {
        var m = md.GetMethodDefinition(mh);
        if ((m.Attributes & System.Reflection.MethodAttributes.Public) == 0) continue;
        try
        {
            var sig = m.DecodeSignature(new SigProvider(), null);
            Console.WriteLine($"  {sig.ReturnType} {md.GetString(m.Name)}({string.Join(", ", sig.ParameterTypes)})");
        }
        catch
        {
            Console.WriteLine($"  {md.GetString(m.Name)}(...)");
        }
    }
}
