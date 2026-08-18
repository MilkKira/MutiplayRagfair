#nullable disable
using SPTarkov.Server.Core.Models.Spt.Mod;

namespace CrossRagfair.Spt;

public sealed record ModMetadata : AbstractModMetadata
{
    public override string ModGuid { get; init; } = "com.mochix2milk.crossragfair";
    public override string Name { get; init; } = "CrossRagfair";
    public override string Author { get; init; } = "Mochix2Milk";
    public override List<string> Contributors { get; init; } = [];
    public override SemanticVersioning.Version Version { get; init; } = new("0.1.0");
    public override SemanticVersioning.Range SptVersion { get; init; } = new("=4.0.13");
    public override List<string> Incompatibilities { get; init; } = [];
    public override Dictionary<string, SemanticVersioning.Range> ModDependencies { get; init; } = [];
    public override string Url { get; init; } = "https://github.com/Mochix2Milk/MutiplayRagfair";
    public override string License { get; init; } = "MIT";
    public override bool? IsBundleMod { get; init; } = false;
}
