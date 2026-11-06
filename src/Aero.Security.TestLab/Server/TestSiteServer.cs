using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Aero.Security.TestLab.Server;

internal static class TestSiteServer
{
    public static async Task<int> RunAsync(
        int port,
        string certificateDirectory,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(certificateDirectory);
        string pfxPath = Path.Combine(certificateDirectory, "aero-testlab-localhost.pfx");
        string cerPath = Path.Combine(certificateDirectory, "aero-testlab-localhost.cer");
        using X509Certificate2 certificate = LoadOrCreateCertificate(pfxPath, cerPath);

        WebApplicationBuilder builder = WebApplication.CreateBuilder(Array.Empty<string>());
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.ListenLocalhost(port, listen => listen.UseHttps(certificate));
        });

        WebApplication app = builder.Build();
        app.MapGet("/", () => Results.Content(LoginPage, "text/html; charset=utf-8"));
        app.MapPost("/login", async context =>
        {
            // The request body is intentionally not parsed, logged, stored, or echoed.
            context.Response.Headers["Cache-Control"] = "no-store";
            context.Response.ContentType = "text/html; charset=utf-8";
            await context.Response.WriteAsync(SuccessPage, cancellationToken);
        });

        Console.WriteLine();
        Console.WriteLine("Aero TestLab local HTTPS site");
        Console.WriteLine($"URL: https://localhost:{port}/");
        Console.WriteLine($"Certificate: {cerPath}");
        Console.WriteLine($"Thumbprint: {certificate.Thumbprint}");
        Console.WriteLine();
        Console.WriteLine("Inside the disposable VM, import the .cer into Current User > Trusted Root");
        Console.WriteLine("only for this test. Remove it or revert the VM checkpoint afterward.");
        Console.WriteLine("The server listens on loopback only and does not log or store form values.");
        Console.WriteLine("Press Ctrl+C to stop.");
        Console.WriteLine();

        await app.RunAsync(cancellationToken);
        return 0;
    }

    private static X509Certificate2 LoadOrCreateCertificate(string pfxPath, string cerPath)
    {
        if (File.Exists(pfxPath))
        {
            return X509CertificateLoader.LoadPkcs12FromFile(
                pfxPath,
                password: string.Empty,
                X509KeyStorageFlags.EphemeralKeySet);
        }

        using RSA rsa = RSA.Create(2048);
        CertificateRequest request = new(
            "CN=localhost",
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment,
            true));
        OidCollection enhancedUsages = new();
        enhancedUsages.Add(new Oid("1.3.6.1.5.5.7.3.1"));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            enhancedUsages,
            true));
        SubjectAlternativeNameBuilder san = new();
        san.AddDnsName("localhost");
        san.AddIpAddress(IPAddress.Loopback);
        san.AddIpAddress(IPAddress.IPv6Loopback);
        request.CertificateExtensions.Add(san.Build());

        using X509Certificate2 generated = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow.AddDays(30));
        File.WriteAllBytes(pfxPath, generated.Export(X509ContentType.Pfx, string.Empty));
        File.WriteAllBytes(cerPath, generated.Export(X509ContentType.Cert));
        return X509CertificateLoader.LoadPkcs12FromFile(
            pfxPath,
            password: string.Empty,
            X509KeyStorageFlags.EphemeralKeySet);
    }

    private const string LoginPage = """
<!doctype html>
<html lang="en">
<head>
  <meta charset="utf-8">
  <meta name="viewport" content="width=device-width,initial-scale=1">
  <meta http-equiv="Cache-Control" content="no-store">
  <title>Aero Security TestLab</title>
  <style>
    body{font-family:Segoe UI,Arial,sans-serif;background:#101216;color:#f5f5f5;display:grid;place-items:center;min-height:100vh;margin:0}
    main{width:min(420px,calc(100% - 40px));background:#191d24;border:1px solid #333b48;border-radius:18px;padding:26px;box-shadow:0 20px 60px #0008}
    label{display:block;margin:15px 0 6px} input{box-sizing:border-box;width:100%;padding:12px;border-radius:10px;border:1px solid #465064;background:#0e1116;color:white}
    button{margin-top:20px;width:100%;padding:12px;border:0;border-radius:10px;font-weight:700;cursor:pointer}
    code{word-break:break-all}.note{color:#aeb8c8;font-size:14px;line-height:1.45}
  </style>
</head>
<body>
<main>
  <h1>Aero Security TestLab</h1>
  <p class="note">Use only a generated <code>AERO_TEST_*</code> marker. Never enter a real password.</p>
  <form method="post" action="/login" autocomplete="on">
    <label for="username">Username</label>
    <input id="username" name="username" autocomplete="username" value="testlab@example.invalid">
    <label for="password">Password marker</label>
    <input id="password" name="password" type="password" autocomplete="current-password">
    <button type="submit">Simulate sign in</button>
  </form>
</main>
</body>
</html>
""";

    private const string SuccessPage = """
<!doctype html>
<html lang="en"><head><meta charset="utf-8"><meta http-equiv="Cache-Control" content="no-store"><title>Test submitted</title></head>
<body style="font-family:Segoe UI,Arial;background:#101216;color:white;padding:40px">
<h1>Test submission received</h1>
<p>No form value was parsed, logged, stored, or echoed by the TestLab server.</p>
<p><a href="/" style="color:#9fc5ff">Return to the form</a></p>
</body></html>
""";
}
