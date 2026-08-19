using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using verpixeld.Configuration;

namespace verpixeld.Services;

/// <summary>
///     Service for managing SSL certificates for HTTPS.
///     Supports self-signed generation, custom certificate upload, and runtime info queries.
/// </summary>
public class CertificateService
{
    private const string DefaultPassword = "rgbdisplay";

    /// <summary>Path to the active certificate file.</summary>
    public string CertificatePath { get; private set; }

    /// <summary>Password used to load the certificate.</summary>
    public string CertificatePassword { get; private set; }

    /// <summary>The currently loaded certificate (null if unavailable).</summary>
    public X509Certificate2? CurrentCertificate { get; private set; }

    public CertificateService(string? certPath = null, string? password = null)
    {
        CertificatePath = certPath ?? AppPaths.Certificate;
        CertificatePassword = password ?? DefaultPassword;
    }

    /// <summary>
    ///     Gets or creates a certificate for HTTPS.
    /// </summary>
    public X509Certificate2? GetOrCreateCertificate()
    {
        // Generate if doesn't exist
        if (!File.Exists(CertificatePath))
        {
            Console.WriteLine("[CERT] Generating self-signed HTTPS certificate...");
            GenerateSelfSignedCertificate(CertificatePath, CertificatePassword);
        }

        // Try to load
        CurrentCertificate = LoadCertificate(CertificatePath);
        return CurrentCertificate;
    }

    /// <summary>
    ///     Loads a certificate from file.
    /// </summary>
    public X509Certificate2? LoadCertificate(string certPath)
    {
        if (!File.Exists(certPath))
            return null;

        try
        {
            // Load with exportable flag - critical for Linux/Raspberry Pi
            var certificate = new X509Certificate2(
                certPath,
                CertificatePassword,
                X509KeyStorageFlags.MachineKeySet | X509KeyStorageFlags.EphemeralKeySet);

            Console.WriteLine($"[CERT] Loaded: {certificate.Subject}");
            Console.WriteLine($"[CERT] Has private key: {certificate.HasPrivateKey}");
            Console.WriteLine($"[CERT] Valid: {certificate.NotBefore:yyyy-MM-dd} to {certificate.NotAfter:yyyy-MM-dd}");

            return certificate;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[CERT] Warning: Could not load certificate: {ex.Message}");

            // Try to regenerate
            try
            {
                File.Delete(certPath);
                GenerateSelfSignedCertificate(certPath, CertificatePassword);
                return new X509Certificate2(
                    certPath,
                    CertificatePassword,
                    X509KeyStorageFlags.MachineKeySet | X509KeyStorageFlags.EphemeralKeySet);
            }
            catch (Exception ex2)
            {
                Console.WriteLine($"[CERT] Failed to regenerate: {ex2.Message}");
                return null;
            }
        }
    }

    /// <summary>
    ///     Returns information about the currently loaded certificate.
    /// </summary>
    public object? GetCertificateInfo()
    {
        var cert = CurrentCertificate;
        if (cert == null)
            return null;

        return new
        {
            subject = cert.Subject,
            issuer = cert.Issuer,
            notBefore = cert.NotBefore.ToString("yyyy-MM-dd HH:mm:ss"),
            notAfter = cert.NotAfter.ToString("yyyy-MM-dd HH:mm:ss"),
            thumbprint = cert.Thumbprint,
            serialNumber = cert.SerialNumber,
            hasPrivateKey = cert.HasPrivateKey,
            isSelfSigned = cert.Subject == cert.Issuer,
            daysUntilExpiry = (int)(cert.NotAfter - DateTime.Now).TotalDays,
            path = CertificatePath
        };
    }

    /// <summary>
    ///     Upload a custom certificate (.pfx / .p12).
    ///     Validates the certificate before saving. Requires restart to take effect.
    /// </summary>
    public (bool Success, string Message) UploadCertificate(byte[] pfxBytes, string password)
    {
        // Validate the certificate first
        X509Certificate2 testCert;
        try
        {
            testCert = new X509Certificate2(
                pfxBytes,
                password,
                X509KeyStorageFlags.MachineKeySet | X509KeyStorageFlags.EphemeralKeySet);
        }
        catch (CryptographicException ex)
        {
            return (false, $"Invalid certificate or wrong password: {ex.Message}");
        }

        if (!testCert.HasPrivateKey)
        {
            testCert.Dispose();
            return (false, "Certificate must include a private key for HTTPS.");
        }

        Console.WriteLine($"[CERT] Uploading custom certificate: {testCert.Subject}");
        Console.WriteLine($"[CERT] Issuer: {testCert.Issuer}");
        Console.WriteLine($"[CERT] Valid: {testCert.NotBefore:yyyy-MM-dd} to {testCert.NotAfter:yyyy-MM-dd}");
        testCert.Dispose();

        // Save the certificate
        try
        {
            // Backup existing
            if (File.Exists(CertificatePath))
            {
                var backupPath = CertificatePath + ".backup";
                File.Copy(CertificatePath, backupPath, overwrite: true);
                Console.WriteLine($"[CERT] Backed up existing certificate to {backupPath}");
            }

            FileHelper.AtomicWriteAllBytes(CertificatePath, pfxBytes);

            // Update password for next load
            CertificatePassword = password;

            Console.WriteLine($"[CERT] Custom certificate saved to {CertificatePath}");
            return (true, "Certificate uploaded successfully. Restart required to take effect.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[CERT] Error saving certificate: {ex.Message}");
            return (false, $"Failed to save certificate: {ex.Message}");
        }
    }

    /// <summary>
    ///     Regenerate a new self-signed certificate. Requires restart to take effect.
    /// </summary>
    public (bool Success, string Message) RegenerateSelfSigned()
    {
        try
        {
            // Backup existing
            if (File.Exists(CertificatePath))
            {
                var backupPath = CertificatePath + ".backup";
                File.Copy(CertificatePath, backupPath, overwrite: true);
            }

            // Reset password to default
            CertificatePassword = DefaultPassword;

            GenerateSelfSignedCertificate(CertificatePath, CertificatePassword);
            Console.WriteLine("[CERT] Self-signed certificate regenerated");
            return (true, "Self-signed certificate regenerated. Restart required to take effect.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[CERT] Error regenerating certificate: {ex.Message}");
            return (false, $"Failed to regenerate certificate: {ex.Message}");
        }
    }

    /// <summary>
    ///     Generates a self-signed certificate with all local IP addresses.
    /// </summary>
    public void GenerateSelfSignedCertificate(string certPath, string password)
    {
        using var rsa = RSA.Create(2048);

        var request = new CertificateRequest(
            "CN=RGB-Display, O=RGB Display Server, OU=Development",
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);

        // Add extensions
        request.CertificateExtensions.Add(
            new X509BasicConstraintsExtension(false, false, 0, false));

        request.CertificateExtensions.Add(
            new X509KeyUsageExtension(
                X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment,
                false));

        request.CertificateExtensions.Add(
            new X509EnhancedKeyUsageExtension(
                new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") }, // Server Authentication
                false));

        // Build Subject Alternative Names with all local IPs
        var sanBuilder = new SubjectAlternativeNameBuilder();
        sanBuilder.AddDnsName("localhost");
        sanBuilder.AddDnsName(Environment.MachineName);
        sanBuilder.AddIpAddress(IPAddress.Loopback);
        sanBuilder.AddIpAddress(IPAddress.IPv6Loopback);

        // Add all local IPv4 addresses
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;

                foreach (var addr in ni.GetIPProperties().UnicastAddresses)
                    if (addr.Address.AddressFamily == AddressFamily.InterNetwork)
                    {
                        sanBuilder.AddIpAddress(addr.Address);
                        Console.WriteLine($"[CERT] Adding IP to certificate: {addr.Address}");
                    }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[CERT] Warning: Could not enumerate network interfaces: {ex.Message}");
        }

        request.CertificateExtensions.Add(sanBuilder.Build());

        // Create certificate valid for 10 years
        var certificate = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow.AddYears(10));

        // Export to PFX
        var pfxBytes = certificate.Export(X509ContentType.Pfx, password);

        var dir = Path.GetDirectoryName(certPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        File.WriteAllBytes(certPath, pfxBytes);

        Console.WriteLine($"[CERT] Generated: {certPath}");
        Console.WriteLine($"[CERT] Valid until: {certificate.NotAfter:yyyy-MM-dd}");
    }
}
