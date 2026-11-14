using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Jellyfin.Plugin.ShapeShifter.Server;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace Jellyfin.Plugin.ShapeShifter.Tests;

public class DualModeListenerTests
{
    [Fact]
    public async Task ServesHttpAndHttpsOnOnePort()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var cert = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));

        var port = FreePort();
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, port, o => PlexListenerService.UseDualMode(o, cert)));
        await using var app = builder.Build();
        ((IApplicationBuilder)app).Run(ctx => ctx.Response.WriteAsync(ctx.Request.IsHttps ? "tls" : "plain"));
        await app.StartAsync();

        using var handler = new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, _, _, _) => true };
        using var client = new HttpClient(handler);
        Assert.Equal("plain", await client.GetStringAsync($"http://127.0.0.1:{port}/"));
        Assert.Equal("tls", await client.GetStringAsync($"https://127.0.0.1:{port}/"));
        Assert.Equal("plain", await client.GetStringAsync($"http://127.0.0.1:{port}/again"));

        await app.StopAsync();
    }

    private static int FreePort()
    {
        using var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        l.Start();
        return ((IPEndPoint)l.LocalEndpoint).Port;
    }
}
