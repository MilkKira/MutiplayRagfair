using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace CrossRagfair.Spt;

internal sealed class HubCertificatePin
{
    private readonly byte[] _expectedSha256;

    public HubCertificatePin(string certificatePath)
    {
        if (!File.Exists(certificatePath))
            throw new FileNotFoundException("The pinned Hub certificate was not found.", certificatePath);

        try
        {
            using var certificate = X509CertificateLoader.LoadCertificateFromFile(certificatePath);
            _expectedSha256 = certificate.GetCertHash(HashAlgorithmName.SHA256);
        }
        catch (CryptographicException exception)
        {
            throw new InvalidDataException($"The pinned Hub certificate is not a valid X.509 certificate: {certificatePath}",
                exception);
        }
    }

    public string Sha256 => Convert.ToHexString(_expectedSha256);

    public bool Validate(HttpRequestMessage _, X509Certificate2? certificate, X509Chain? __,
        SslPolicyErrors sslPolicyErrors) => Validate(certificate, sslPolicyErrors, DateTimeOffset.UtcNow);

    internal bool Validate(X509Certificate2? certificate, SslPolicyErrors sslPolicyErrors, DateTimeOffset now)
    {
        if (certificate is null ||
            sslPolicyErrors is not (SslPolicyErrors.None or SslPolicyErrors.RemoteCertificateChainErrors))
            return false;

        var nowUtc = now.UtcDateTime;
        if (nowUtc < certificate.NotBefore.ToUniversalTime() || nowUtc > certificate.NotAfter.ToUniversalTime())
            return false;

        var actualSha256 = certificate.GetCertHash(HashAlgorithmName.SHA256);
        return CryptographicOperations.FixedTimeEquals(_expectedSha256, actualSha256);
    }
}
