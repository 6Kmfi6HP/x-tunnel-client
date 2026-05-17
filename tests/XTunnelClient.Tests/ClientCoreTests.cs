using System.Net;
using System.Net.Sockets;
using System.Diagnostics;
using System.Text;
using XTunnelClient.Core;

namespace XTunnelClient.Tests;

public sealed class ClientCoreTests
{
    [Fact]
    public void RuntimeConfigReplacesSecretReferenceAndRejectsUnknownFields()
    {
        var service = new RuntimeConfigService();
        var profile = new Profile
        {
            CoreConfigJson = """
                {
                  "listen": "socks5://127.0.0.1:10808,http://127.0.0.1:10809",
                  "forward": "ws://127.0.0.1:18080/tunnel",
                  "token_ref": "secret:profile-token",
                  "connections": 1
                }
                """
        };
        var secrets = new MemorySecretStore();
        secrets.SaveSecret(profile.Id, "profile-token", "runtime-token");

        var runtime = service.GenerateRuntimeConfig(profile, secrets);

        Assert.Contains("\"token\": \"runtime-token\"", runtime);
        Assert.DoesNotContain("token_ref", runtime);
        Assert.Throws<InvalidOperationException>(() => service.ParseAndValidate("""{"listen":"socks5://127.0.0.1:1","unknown":true}"""));
    }

    [Fact]
    public void RedactorRemovesSecretsAndUrlUserInfo()
    {
        var input = """{"token":"abc","password":"pw","forward":"socks5://user:pass@example.com:1080"} secret:profile-token""";
        var redacted = RuntimeConfigService.Redact(input);

        Assert.DoesNotContain("abc", redacted);
        Assert.DoesNotContain("pw", redacted);
        Assert.DoesNotContain("user:pass", redacted);
        Assert.Contains("secret:redacted", redacted);
    }

    [Fact]
    public void SystemProxyServiceRestoresPreviousSettings()
    {
        var store = new MemoryProxyStore
        {
            Snapshot = new ProxySnapshot
            {
                ProxyEnable = 1,
                ProxyServer = "http=corp.proxy:8080",
                ProxyOverride = "localhost",
                AutoConfigUrl = null
            }
        };
        var service = new SystemProxyService(store);

        service.EnableSystemProxy(new LocalProxyEndpoints { Http = "127.0.0.1:10809", Socks = "127.0.0.1:10808" }, "localhost;127.*");
        Assert.Equal("http=127.0.0.1:10809;https=127.0.0.1:10809;socks=127.0.0.1:10808", store.Snapshot.ProxyServer);

        service.Restore();
        Assert.Equal("http=corp.proxy:8080", store.Snapshot.ProxyServer);
        Assert.Equal(1, store.Snapshot.ProxyEnable);
    }

    [Fact]
    public void PacBuilderIncludesBypassAndProxy()
    {
        var pac = PacServer.BuildPac("127.0.0.1:10809", "localhost;*.local;10.*");

        Assert.Contains("127.0.0.1:10809", pac);
        Assert.Contains("dnsDomainIs(host, \".local\")", pac);
        Assert.Contains("shExpMatch(host, \"10.*\")", pac);
    }

    [Fact]
    public void SubscriptionDiffSeparatesAddedAndUpdatedProfiles()
    {
        var service = new SubscriptionService(new RuntimeConfigService(), new HttpClient(new StaticHandler("[]")));
        var current = new[]
        {
            new Profile { Name = "A", CoreConfigJson = """{"listen":"socks5://127.0.0.1:1","forward":"ws://old/tunnel"}""" }
        };
        var incoming = new[]
        {
            new Profile { Name = "A", CoreConfigJson = """{"listen":"socks5://127.0.0.1:1","forward":"ws://new/tunnel"}""" },
            new Profile { Name = "B", CoreConfigJson = """{"listen":"socks5://127.0.0.1:2","forward":"ws://new/tunnel"}""" }
        };

        var diff = service.Diff(current, incoming);

        Assert.Single(diff.Updated);
        Assert.Single(diff.Added);
    }

    [Fact]
    public void UpdateManifestRequiresChecksums()
    {
        Assert.Throws<InvalidOperationException>(() => UpdateService.ValidateManifest(new UpdateManifest { Version = "1.0.0" }));
        UpdateService.ValidateManifest(new UpdateManifest
        {
            Version = "1.0.0",
            GuiUrl = "https://example.invalid/gui.zip",
            CoreUrl = "https://example.invalid/core.zip",
            GuiSha256 = new string('a', 64),
            CoreSha256 = new string('b', 64)
        });
    }

    [Fact]
    public async Task ControlApiClientParsesStructuredErrors()
    {
        var http = new HttpClient(new StaticHandler("""{"ok":false,"error":{"code":"config.invalid","message":"bad config"}}""", HttpStatusCode.BadRequest));
        var client = new ControlApiClient("http://127.0.0.1:1", "token", http);

        var ex = await Assert.ThrowsAsync<ControlApiException>(() => client.CheckConfigAsync("{}"));

        Assert.Equal("config.invalid", ex.Code);
        Assert.Equal("bad config", ex.Message);
    }

    [Fact]
    public async Task RealCoreSidecarSupervisorStartsAndStops()
    {
        var corePath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "..", "x-tunnel", "build", "x-tunnel.exe"));
        if (!File.Exists(corePath))
        {
            return;
        }

        var root = Path.Combine(Path.GetTempPath(), "x-tunnel-client-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var paths = new AppPaths(root);
        var repository = new ProfileRepository(paths);
        var configService = new RuntimeConfigService();
        var proxy = new ProxyCoordinator(new SystemProxyService(new MemoryProxyStore()), new PacServer());
        var supervisor = new SidecarSupervisor(paths, repository, configService, new CoreLocator(paths), proxy, new PortChecker());

        var wsPort = FreeTcpPort();
        var httpPort = FreeTcpPort();
        var socksPort = FreeTcpPort();
        var serverConfig = Path.Combine(root, "server.json");
        await File.WriteAllTextAsync(serverConfig, $$"""
            {
              "listen": "ws://127.0.0.1:{{wsPort}}/tunnel",
              "token": "smoke-token",
              "cidr": "127.0.0.1/32",
              "allow-target": "127.0.0.0/8",
              "fallback": true,
              "shutdown_timeout": "2s"
            }
            """);

        using var server = Process.Start(new ProcessStartInfo
        {
            FileName = corePath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            Arguments = $"-config \"{serverConfig}\""
        })!;
        try
        {
            await WaitTcpAsync("127.0.0.1", wsPort, TimeSpan.FromSeconds(5));
            var profile = new Profile
            {
                Name = "Smoke",
                CoreConfigJson = $$"""
                    {
                      "listen": "socks5://127.0.0.1:{{socksPort}},http://127.0.0.1:{{httpPort}}",
                      "forward": "ws://127.0.0.1:{{wsPort}}/tunnel",
                      "token_ref": "secret:profile-token",
                      "connections": 1,
                      "fallback": true,
                      "reconnect_delay": "50ms",
                      "reconnect_max_delay": "100ms",
                      "reconnect_jitter": "0s",
                      "rtt_timeout": "500ms"
                    }
                    """
            };
            repository.SaveProfile(profile);
            repository.SaveSecret(profile.Id, "profile-token", "smoke-token");

            await supervisor.ConnectAsync(profile, new AppSettings { CorePath = corePath, DefaultProxyMode = ProxyMode.Off }).WaitAsync(TimeSpan.FromSeconds(20));
            Assert.True(supervisor.State is RuntimeState.Running or RuntimeState.Degraded);

            await WaitUntilAsync(async () =>
            {
                var status = await supervisor.Control!.GetStatusAsync();
                return status.Client?.Channels.Any(x => x.Up) == true;
            }, TimeSpan.FromSeconds(8));

            await supervisor.DisconnectAsync().WaitAsync(TimeSpan.FromSeconds(20));
            Assert.Equal(RuntimeState.Stopped, supervisor.State);
        }
        finally
        {
            try { await supervisor.DisconnectAsync().WaitAsync(TimeSpan.FromSeconds(10)); } catch { }
            if (!server.HasExited)
            {
                server.Kill(entireProcessTree: true);
            }
            supervisor.Dispose();
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    private sealed class MemorySecretStore : ISecretStore
    {
        private readonly Dictionary<(Guid, string), string> _secrets = [];
        public void SaveSecret(Guid profileId, string secretRef, string value) => _secrets[(profileId, secretRef)] = value;
        public string? GetSecret(Guid profileId, string secretRef) => _secrets.TryGetValue((profileId, secretRef), out var value) ? value : null;
        public void DeleteSecrets(Guid profileId)
        {
            foreach (var key in _secrets.Keys.Where(x => x.Item1 == profileId).ToList())
            {
                _secrets.Remove(key);
            }
        }
    }

    private sealed class MemoryProxyStore : IProxySettingsStore
    {
        public ProxySnapshot Snapshot { get; set; } = new();
        public ProxySnapshot Read() => new()
        {
            ProxyEnable = Snapshot.ProxyEnable,
            ProxyServer = Snapshot.ProxyServer,
            ProxyOverride = Snapshot.ProxyOverride,
            AutoConfigUrl = Snapshot.AutoConfigUrl
        };
        public void Write(ProxySnapshot snapshot) => Snapshot = snapshot;
    }

    private sealed class StaticHandler : HttpMessageHandler
    {
        private readonly string _body;
        private readonly HttpStatusCode _statusCode;

        public StaticHandler(string body, HttpStatusCode statusCode = HttpStatusCode.OK)
        {
            _body = body;
            _statusCode = statusCode;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(new HttpResponseMessage(_statusCode)
            {
                Content = new StringContent(_body, Encoding.UTF8, "application/json")
            });
        }
    }

    private static int FreeTcpPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static async Task WaitTcpAsync(string host, int port, TimeSpan timeout)
    {
        await WaitUntilAsync(async () =>
        {
            using var client = new TcpClient();
            try
            {
                await client.ConnectAsync(host, port);
                return true;
            }
            catch
            {
                return false;
            }
        }, timeout);
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        Exception? last = null;
        while (DateTimeOffset.UtcNow < deadline)
        {
            try
            {
                if (await condition())
                {
                    return;
                }
            }
            catch (Exception ex)
            {
                last = ex;
            }
            await Task.Delay(100);
        }
        throw new TimeoutException(last?.Message ?? "condition timed out");
    }
}
