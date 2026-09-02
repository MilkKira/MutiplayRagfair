using System.Reflection;
using System.Text.Json;

namespace CrossRagfair.Spt;

public sealed class CrossRagfairConfig
{
    public bool Enabled { get; set; } = true;
    public bool ReadOnly { get; set; } = true;
    public bool EnablePurchases { get; set; }
    public string ServerId { get; set; } = "server-a";
    public string HubUrl { get; set; } = "https://hub.example.invalid:7443";
    public string HubCertificatePath { get; set; } = "certificate.cer";
    public string SharedSecret { get; set; } = "";
    public string SptVersion { get; set; } = "4.0.13";
    public string CompatibilityHash { get; set; } = "";
    public int SyncIntervalSeconds { get; set; } = 2;
    public int RequestTimeoutMilliseconds { get; set; } = 2000;
    public int OriginLeaseSeconds { get; set; } = 10;
    public string NodeDataDirectory { get; set; } = "data/node";

    public static CrossRagfairConfig Load()
    {
        var directory = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)
            ?? throw new InvalidOperationException("Cannot locate CrossRagfair assembly directory.");
        var path = Path.Combine(directory, "config.json");
        if (!File.Exists(path)) throw new FileNotFoundException("CrossRagfair config.json was not found.", path);
        var config = JsonSerializer.Deserialize<CrossRagfairConfig>(File.ReadAllText(path),
            new JsonSerializerOptions(JsonSerializerDefaults.Web))
            ?? throw new InvalidDataException("CrossRagfair config.json is empty.");
        config.Validate();
        return config;
    }

    private void Validate()
    {
        if (!Uri.TryCreate(HubUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            throw new InvalidDataException("hubUrl must be an absolute HTTPS URL.");
        if (Enabled && string.IsNullOrWhiteSpace(HubCertificatePath))
            throw new InvalidDataException("hubCertificatePath must identify the Hub public certificate.");
        if (string.IsNullOrWhiteSpace(ServerId) || ServerId.Length > 64)
            throw new InvalidDataException("serverId must contain 1-64 characters.");
        if (SptVersion != "4.0.13") throw new InvalidDataException("This build only supports SPT 4.0.13.");
        if (string.IsNullOrWhiteSpace(CompatibilityHash))
            throw new InvalidDataException("compatibilityHash must be configured identically on both servers.");
        if (Enabled && (string.IsNullOrWhiteSpace(SharedSecret) || SharedSecret.Length < 32))
            throw new InvalidDataException("sharedSecret in config.json must contain at least 32 characters.");
        if (string.IsNullOrWhiteSpace(NodeDataDirectory)) throw new InvalidDataException("nodeDataDirectory cannot be empty.");
        SyncIntervalSeconds = Math.Clamp(SyncIntervalSeconds, 1, 30);
        RequestTimeoutMilliseconds = Math.Clamp(RequestTimeoutMilliseconds, 500, 10000);
        OriginLeaseSeconds = Math.Clamp(OriginLeaseSeconds, 5, 60);
    }

    public string ResolveNodeDataDirectory(string assemblyDirectory) => Path.GetFullPath(NodeDataDirectory, assemblyDirectory);

    public string ResolveHubCertificatePath(string assemblyDirectory) =>
        Path.GetFullPath(HubCertificatePath, assemblyDirectory);
}
