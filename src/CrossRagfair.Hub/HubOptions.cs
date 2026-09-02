namespace CrossRagfair.Hub;

public sealed class HubOptions
{
    public string ListenUrl { get; set; } = "http://127.0.0.1:7443";
    public string DataDirectory { get; set; } = "data/hub";
    public string CertificatePath { get; set; } = "certificate.pfx";
    public int AllowedClockSkewSeconds { get; set; } = 30;
    public int OriginCommandTimeoutMilliseconds { get; set; } = 2500;
    public Dictionary<string, string> PeerSecrets { get; set; } = new(StringComparer.Ordinal);

    public void Validate()
    {
        if (!Uri.TryCreate(ListenUrl, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            throw new InvalidDataException("CrossRagfairHub:ListenUrl must be an absolute HTTP or HTTPS URL.");
        if (string.IsNullOrWhiteSpace(DataDirectory))
            throw new InvalidDataException("CrossRagfairHub:DataDirectory cannot be empty.");
        if (uri.Scheme == Uri.UriSchemeHttps && string.IsNullOrWhiteSpace(CertificatePath))
            throw new InvalidDataException("CrossRagfairHub:CertificatePath is required for HTTPS.");
        if (AllowedClockSkewSeconds is < 5 or > 300)
            throw new InvalidDataException("CrossRagfairHub:AllowedClockSkewSeconds must be between 5 and 300.");
        if (OriginCommandTimeoutMilliseconds is < 500 or > 10000)
            throw new InvalidDataException("CrossRagfairHub:OriginCommandTimeoutMilliseconds must be between 500 and 10000.");
        foreach (var (serverId, secret) in PeerSecrets)
        {
            if (string.IsNullOrWhiteSpace(serverId) || secret.Length < 32)
                throw new InvalidDataException("Every Hub peer must have a server ID and a secret of at least 32 characters.");
        }
    }

    public string ResolveCertificatePath() => Path.IsPathRooted(CertificatePath)
        ? Path.GetFullPath(CertificatePath)
        : Path.GetFullPath(CertificatePath, AppContext.BaseDirectory);
}
