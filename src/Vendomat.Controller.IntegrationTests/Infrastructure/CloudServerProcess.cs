using System.Diagnostics;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;

namespace Vendomat.Controller.IntegrationTests.Infrastructure;

/// <summary>
/// Launches the real <c>Vendomat.Controller.Cloud</c> ASP.NET Core server as an out-of-process
/// black-box system under test. Each instance gets a free TCP port and an isolated SQLite
/// database file, so tests never touch development or production data.
/// </summary>
public sealed class CloudServerProcess : IAsyncDisposable
{
    private readonly Process _process;
    private readonly StringBuilder _logs = new();

    private CloudServerProcess(Process process, int port, string dbPath)
    {
        _process = process;
        Port = port;
        DbPath = dbPath;
        BaseUrl = $"http://127.0.0.1:{port}";
        WsBaseUrl = $"ws://127.0.0.1:{port}";
    }

    public int Port { get; }
    public string DbPath { get; }
    public string BaseUrl { get; }
    public string WsBaseUrl { get; }
    public string Logs => _logs.ToString();

    public HttpClient CreateClient() => new() { BaseAddress = new Uri(BaseUrl), Timeout = TimeSpan.FromSeconds(30) };

    public static async Task<CloudServerProcess> StartAsync(
        string? dbPath = null,
        bool allowAutoRegister = true,
        CancellationToken cancellationToken = default)
    {
        var dll = LocateCloudDll();
        var port = GetFreeTcpPort();
        dbPath ??= Path.Combine(Path.GetTempPath(), "vendomat-tests", Guid.NewGuid().ToString("N"), "test.db");
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);

        var psi = new ProcessStartInfo("dotnet", $"\"{dll}\"")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(dll)!,
        };
        psi.Environment["ASPNETCORE_ENVIRONMENT"] = "Development";
        psi.Environment["ASPNETCORE_URLS"] = $"http://127.0.0.1:{port}";
        psi.Environment["Cloud__DatabasePath"] = dbPath;
        psi.Environment["Cloud__AllowMachineAutoRegister"] = allowAutoRegister ? "true" : "false";
        psi.Environment["Logging__LogLevel__Default"] = "Warning";

        var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var server = new CloudServerProcess(process, port, dbPath);
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) server._logs.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) server._logs.AppendLine(e.Data); };
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        await server.WaitForHealthAsync(cancellationToken);
        return server;
    }

    private async Task WaitForHealthAsync(CancellationToken cancellationToken)
    {
        using var client = CreateClient();
        var deadline = DateTime.UtcNow.AddSeconds(45);
        while (DateTime.UtcNow < deadline)
        {
            if (_process.HasExited)
            {
                throw new InvalidOperationException($"Cloud server exited early (code {_process.ExitCode}).\n{Logs}");
            }

            try
            {
                using var resp = await client.GetAsync("/api/health", cancellationToken);
                if (resp.IsSuccessStatusCode)
                {
                    return;
                }
            }
            catch
            {
                // server not up yet
            }

            await Task.Delay(300, cancellationToken);
        }

        throw new TimeoutException($"Cloud server did not become healthy in time.\n{Logs}");
    }

    private static int GetFreeTcpPort()
    {
        var listener = new TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static string LocateCloudDll()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(
                dir.FullName, "src", "Vendomat.Controller.Cloud", "bin", "Debug", "net10.0",
                "Vendomat.Controller.Cloud.dll");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        throw new FileNotFoundException(
            "Could not locate Vendomat.Controller.Cloud.dll (net10.0). Build the Cloud project before running integration tests.");
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
                await _process.WaitForExitAsync();
            }
        }
        catch
        {
            // best-effort teardown
        }
        finally
        {
            _process.Dispose();
            TryDeleteDbDirectory();
        }
    }

    private void TryDeleteDbDirectory()
    {
        try
        {
            var dir = Path.GetDirectoryName(DbPath);
            if (dir is not null && dir.Contains("vendomat-tests") && Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
        catch
        {
            // ignore
        }
    }
}
