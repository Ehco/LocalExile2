// usage: dotnet run --project scripts/re/poe2/steamdump -- <Bundles2 dir> <outDir> <path>...
//        dotnet run --project scripts/re/poe2/steamdump -- <Bundles2 dir> --list <regex>
// Writes each path to <outDir>/<path with / as _>, or prints "<path>: not found". PoE2's index needs the Node ooz
// decoder (npm ci in Tools/ooz).
using GameServer.Data.Ggpk;
using GgDump;
// The repository root: the first parent of the build output that has Tools/ooz.
string root = AppContext.BaseDirectory;
while (!File.Exists(Path.Combine(root, "Tools", "ooz", "ooz_worker.mjs")))
    root = Path.GetDirectoryName(root.TrimEnd(Path.DirectorySeparatorChar))
        ?? throw new InvalidOperationException("Tools/ooz/ooz_worker.mjs not found above the build output");
using var oodle = new NodeOodleDecoder("node", Path.Combine(root, "Tools", "ooz", "ooz_worker.mjs"));
string dir = args[0];
var index = GameIndex.Parse(BundleFile.DecompressWhole(File.ReadAllBytes(Path.Combine(dir, "_.index.bin")), oodle));
if (args[1] == "--list")
{
    var re = new System.Text.RegularExpressions.Regex(args[2], System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    foreach (var p in index.Paths(BundleFile.DecompressWhole(index.PathTableBundle, oodle))) if (re.IsMatch(p)) Console.WriteLine(p);
    return;
}
Directory.CreateDirectory(args[1]);
foreach (var path in args.Skip(2))
{
    if (!index.TryGetFile(path, out var loc)) { Console.WriteLine($"{path}: not found"); continue; }
    // Bundle names are relative to the install root, Bundles2's parent.
    byte[] bundle = File.ReadAllBytes(Path.Combine(dir, "..", index.BundleFiles[loc.BundleIndex]));
    byte[] data = BundleFile.DecompressSlice(bundle, loc.Offset, loc.Size, oodle);
    string o = Path.Combine(args[1], path.Replace('/', '_'));
    File.WriteAllBytes(o, data);
    Console.WriteLine($"{path} -> {o} ({data.Length} bytes)");
}
