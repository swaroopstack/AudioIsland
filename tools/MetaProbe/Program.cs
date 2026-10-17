using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

var path = args[0];
using var fs = File.OpenRead(path);
using var pe = new PEReader(fs);
var md = pe.GetMetadataReader();
foreach (var h in md.TypeDefinitions)
{
    var t = md.GetTypeDefinition(h);
    var ns = md.GetString(t.Namespace);
    var name = md.GetString(t.Name);
    if (name != "SessionCreatedDelegate" && name != "AudioSessionManager" && name != "AudioSessionState") continue;

    foreach (var mh in t.GetMethods())
    {
        var m = md.GetMethodDefinition(mh);
        var mname = md.GetString(m.Name);
        if (name == "AudioSessionManager" && mname != ".ctor") continue;
        if (name == "AudioSessionState") { Console.WriteLine($"{ns}.{name} (enum)"); break; }
        var sig = m.DecodeSignature(new SigProvider(), null);
        Console.WriteLine($"{ns}.{name}.{mname}({string.Join(", ", sig.ParameterTypes)}) -> {sig.ReturnType}");
    }
}
