using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Options;
using ReleaseGuard.WebhookIngestion.Api;

namespace ReleaseGuard.WebhookIngestion.Api.Tests;

public sealed class PythonAiExplanationContractIntegrationTests
{
    [Fact]
    [Trait("Category", "CrossService")]
    public async Task DotNetClient_ExchangesV1ContractWithRealPythonProcess()
    {
        var repositoryRoot = FindRepositoryRoot();
        var pythonProjectDirectory = Path.Combine(
            repositoryRoot,
            "src",
            "ReleaseGuard.AiExplanation.Api");
        var pythonExecutable = ResolvePythonExecutable(pythonProjectDirectory);
        var port = ReserveLoopbackPort();
        await using var pythonService = await PythonServiceProcess.StartAsync(
            pythonExecutable,
            pythonProjectDirectory,
            port);
        using var httpClient = new HttpClient();
        var client = new HttpReleaseRiskExplanationClient(
            httpClient,
            Options.Create(new AiExplanationClientOptions
            {
                BaseUrl = pythonService.BaseUrl,
                RequestTimeoutMilliseconds = 5_000
            }));
        var envelope = ReleaseRiskOutboxEnvelope.Deserialize(
            File.ReadAllText(
                Path.Combine(
                    repositoryRoot,
                    "contracts",
                    "release-risk-assessed.v1.example.json")));

        var explanation = await client.ExplainAsync(
            envelope,
            CancellationToken.None);

        Assert.Equal(envelope.EventId, explanation.EventId);
        Assert.NotEmpty(explanation.Summary);
        Assert.NotEmpty(explanation.Recommendations);
        Assert.All(
            explanation.Recommendations,
            recommendation => Assert.False(
                string.IsNullOrWhiteSpace(recommendation)));
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "ReleaseGuard.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException(
            "Could not find the ReleaseGuard repository root.");
    }

    private static string ResolvePythonExecutable(string pythonProjectDirectory)
    {
        var configuredExecutable = Environment.GetEnvironmentVariable(
            "RELEASEGUARD_AI_PYTHON");
        if (!string.IsNullOrWhiteSpace(configuredExecutable))
        {
            if (!File.Exists(configuredExecutable))
            {
                throw new FileNotFoundException(
                    "RELEASEGUARD_AI_PYTHON does not point to a file.",
                    configuredExecutable);
            }

            return configuredExecutable;
        }

        var executable = OperatingSystem.IsWindows()
            ? Path.Combine(pythonProjectDirectory, ".venv", "Scripts", "python.exe")
            : Path.Combine(pythonProjectDirectory, ".venv", "bin", "python");
        if (!File.Exists(executable))
        {
            throw new FileNotFoundException(
                "Create the Python service .venv as documented in README.md before running the cross-service contract test.",
                executable);
        }

        return executable;
    }

    private static int ReserveLoopbackPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }

    private sealed class PythonServiceProcess : IAsyncDisposable
    {
        private readonly Process _process;
        private readonly Task<string> _standardOutput;
        private readonly Task<string> _standardError;

        private PythonServiceProcess(
            Process process,
            Task<string> standardOutput,
            Task<string> standardError,
            string baseUrl)
        {
            _process = process;
            _standardOutput = standardOutput;
            _standardError = standardError;
            BaseUrl = baseUrl;
        }

        public string BaseUrl { get; }

        public static async Task<PythonServiceProcess> StartAsync(
            string pythonExecutable,
            string workingDirectory,
            int port)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = pythonExecutable,
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            startInfo.ArgumentList.Add("-m");
            startInfo.ArgumentList.Add("uvicorn");
            startInfo.ArgumentList.Add("releaseguard_ai.main:app");
            startInfo.ArgumentList.Add("--host");
            startInfo.ArgumentList.Add("127.0.0.1");
            startInfo.ArgumentList.Add("--port");
            startInfo.ArgumentList.Add(port.ToString(CultureInfo.InvariantCulture));
            startInfo.ArgumentList.Add("--log-level");
            startInfo.ArgumentList.Add("warning");
            startInfo.Environment["RELEASEGUARD_AI_PROVIDER"] = "fake";
            startInfo.Environment["RELEASEGUARD_AI_MODEL"] = "deterministic-v1";
            startInfo.Environment["RELEASEGUARD_AI_TIMEOUT_SECONDS"] = "5";

            var process = new Process { StartInfo = startInfo };
            if (!process.Start())
            {
                process.Dispose();
                throw new InvalidOperationException(
                    "The Python AI explanation process did not start.");
            }

            var standardOutput = process.StandardOutput.ReadToEndAsync();
            var standardError = process.StandardError.ReadToEndAsync();
            var baseUrl = $"http://127.0.0.1:{port}";

            try
            {
                await WaitUntilReadyAsync(process, baseUrl);
                return new PythonServiceProcess(
                    process,
                    standardOutput,
                    standardError,
                    baseUrl);
            }
            catch (Exception exception)
            {
                await StopAsync(process);
                var output = await standardOutput;
                var error = await standardError;
                process.Dispose();
                throw new InvalidOperationException(
                    $"The real Python AI explanation process was not ready. stdout: {output} stderr: {error}",
                    exception);
            }
        }

        public async ValueTask DisposeAsync()
        {
            await StopAsync(_process);
            await _standardOutput;
            await _standardError;
            _process.Dispose();
        }

        private static async Task WaitUntilReadyAsync(
            Process process,
            string baseUrl)
        {
            using var healthClient = new HttpClient
            {
                Timeout = TimeSpan.FromMilliseconds(250)
            };
            using var deadline = new CancellationTokenSource(
                TimeSpan.FromSeconds(15));

            while (!deadline.IsCancellationRequested)
            {
                if (process.HasExited)
                {
                    throw new InvalidOperationException(
                        $"The Python AI explanation process exited with code {process.ExitCode}.");
                }

                try
                {
                    using var response = await healthClient.GetAsync(
                        $"{baseUrl}/health",
                        deadline.Token);
                    if (response.IsSuccessStatusCode)
                    {
                        return;
                    }
                }
                catch (HttpRequestException)
                {
                }
                catch (OperationCanceledException)
                    when (!deadline.IsCancellationRequested)
                {
                }

                try
                {
                    await Task.Delay(100, deadline.Token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }

            throw new TimeoutException(
                "The Python AI explanation process did not become ready within 15 seconds.");
        }

        private static async Task StopAsync(Process process)
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            using var deadline = new CancellationTokenSource(
                TimeSpan.FromSeconds(5));
            await process.WaitForExitAsync(deadline.Token);
        }
    }
}
