using System.Diagnostics;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Reflection;
using System.Text.Json;

namespace MetadataApi.E2E.Tests;

public sealed class ApiServerFixture : IAsyncLifetime
{
    // EF tooling copies BuildHost files into the shared API output directory, even with --no-build.
    private static readonly SemaphoreSlim MigrationToolGate = new(1, 1);
    private readonly string root = FindRepositoryRoot();
    private readonly string configuration = typeof(ApiServerFixture).Assembly
        .GetCustomAttribute<AssemblyConfigurationAttribute>()?.Configuration
        ?? throw new InvalidOperationException("The test build configuration is missing.");
    private readonly string apiKey = Environment.GetEnvironmentVariable("Authentication__ApiKey")
        ?? Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    private string DatabaseDirectory => Path.Combine(LogDirectory, "database");
    private readonly string runId = Guid.NewGuid().ToString("N");
    private Process? server;
    private TextWriter? serverLog;
    private HttpClient? client;
    private int startCount;
    private readonly object traceLock = new();

    public ApiServerFixture() : this(new Dictionary<string, string>()) { }

    internal ApiServerFixture(IReadOnlyDictionary<string, string> settings) => EnvironmentVariables = settings;

    public IReadOnlyDictionary<string, string> EnvironmentVariables { get; init; } =
        new Dictionary<string, string>();

    public HttpClient Client => client
        ?? throw new InvalidOperationException("The E2E server has not started.");

    public HttpClient CreateUnauthenticatedClient() => new(CreateTraceHandler())
    {
        BaseAddress = Client.BaseAddress,
        Timeout = Client.Timeout
    };

    private HttpTraceHandler CreateTraceHandler() => new(line =>
    {
        lock (traceLock)
        {
            File.AppendAllText(Path.Combine(LogDirectory, "http-trace.log"), line + Environment.NewLine);
        }
    });

    private string LogDirectory => Path.Combine(root, "artifacts", "e2e", runId);

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(DatabaseDirectory);
        Directory.CreateDirectory(LogDirectory);
        var initialized = false;
        try
        {
            await MigrationToolGate.WaitAsync();
            try
            {
                await ApplyMigrationsAsync();
            }
            finally
            {
                MigrationToolGate.Release();
            }

            await StartServerAsync();
            initialized = true;
        }
        finally
        {
            if (!initialized)
            {
                await DisposeAsync();
            }
        }
    }

    public async Task RestartAsync()
    {
        await StopServerAsync();
        await StartServerAsync();
    }

    public async Task<(int ExitCode, string Output)> StartWithoutApiKeyAsync()
    {
        var applicationDirectory = Path.Combine(root, "src", "MetadataApi");
        var assemblyPath = Path.Combine(
            applicationDirectory, "bin", configuration, "net10.0", "MetadataApi.dll");
        using var process = new Process
        {
            StartInfo = CreateStartInfo(assemblyPath, "--contentRoot", applicationDirectory,
                "--urls", "http://127.0.0.1:0", "--Authentication:ApiKey=")
        };
        process.StartInfo.Environment.Remove("Authentication__ApiKey");
        process.Start();
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }

            await File.WriteAllTextAsync(Path.Combine(LogDirectory, "missing-key.log"),
                await output + await errors);
        }

        return (process.ExitCode, await output + await errors);
    }

    private async Task ApplyMigrationsAsync()
    {
        using var process = new Process
        {
            StartInfo = CreateStartInfo(
                "ef", "database", "update",
                "--project", Path.Combine(root, "src", "MetadataApi"),
                "--configuration", configuration, "--no-build")
        };
        process.Start();
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var logPath = Path.Combine(LogDirectory, "migrations.log");
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }

            await File.WriteAllTextAsync(logPath, await output + await errors);
        }

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"E2E migrations failed. Run 'dotnet tool restore' and inspect {logPath}.");
        }
    }

    private async Task StartServerAsync()
    {
        var applicationDirectory = Path.Combine(root, "src", "MetadataApi");
        var assemblyPath = Path.Combine(
            applicationDirectory, "bin", configuration, "net10.0", "MetadataApi.dll");
        if (!File.Exists(assemblyPath))
        {
            throw new InvalidOperationException($"Build the API before E2E tests: {assemblyPath}");
        }

        var logPath = Path.Combine(LogDirectory, $"server-{++startCount}.log");
        var log = TextWriter.Synchronized(new StreamWriter(logPath) { AutoFlush = true });
        serverLog = log;
        var listening = new TaskCompletionSource<Uri>(TaskCreationOptions.RunContinuationsAsynchronously);
        var process = new Process
        {
            StartInfo = CreateStartInfo(
                assemblyPath, "--contentRoot", applicationDirectory, "--urls", "http://127.0.0.1:0"),
            EnableRaisingEvents = true
        };
        server = process;
        process.OutputDataReceived += (_, args) =>
        {
            if (args.Data is not { } line)
            {
                return;
            }

            log.WriteLine(line);
            if (line.StartsWith('{'))
            {
                using var entry = JsonDocument.Parse(line);
                line = entry.RootElement.GetProperty("Message").GetString() ?? "";
            }

            const string marker = "Now listening on: ";
            var markerIndex = line.IndexOf(marker, StringComparison.Ordinal);
            if (markerIndex >= 0
                && Uri.TryCreate(line[(markerIndex + marker.Length)..].Trim(), UriKind.Absolute, out var address))
            {
                listening.TrySetResult(address);
            }
        };
        process.ErrorDataReceived += (_, args) =>
        {
            if (args.Data is { } line)
            {
                log.WriteLine(line);
            }
        };
        process.Exited += (_, _) => listening.TrySetException(
            new InvalidOperationException($"The E2E API exited before startup completed. See {logPath}."));
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        // Port zero lets Kestrel select a free port without racing another process.
        var baseAddress = await listening.Task.WaitAsync(TimeSpan.FromSeconds(30));
        client = new HttpClient(CreateTraceHandler())
        {
            BaseAddress = baseAddress,
            Timeout = TimeSpan.FromSeconds(15)
        };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        using var readinessTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            while (true)
            {
                try
                {
                    using var readiness = await client.GetAsync("/health/ready", readinessTimeout.Token);
                    if (readiness.IsSuccessStatusCode)
                    {
                        break;
                    }
                }
                catch (HttpRequestException) when (!process.HasExited)
                {
                    // The listener can become available before the application is ready.
                }

                await Task.Delay(TimeSpan.FromMilliseconds(100), readinessTimeout.Token);
            }
        }
        catch (OperationCanceledException) when (readinessTimeout.IsCancellationRequested)
        {
            throw new TimeoutException($"The E2E API did not become ready within 30 seconds. See {logPath}.");
        }
    }

    private ProcessStartInfo CreateStartInfo(params string[] arguments)
    {
        var info = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = root,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        info.Environment["Database__Path"] = Path.Combine(DatabaseDirectory, "metadata.db");
        info.Environment["Authentication__ApiKey"] = apiKey;
        info.Environment["ASPNETCORE_ENVIRONMENT"] = "Production";
        info.Environment["DOTNET_ENVIRONMENT"] = "Production";
        info.Environment["Logging__LogLevel__Default"] = "Warning";
        info.Environment["Logging__LogLevel__Microsoft.Hosting.Lifetime"] = "Information";
        foreach (var (name, value) in EnvironmentVariables)
        {
            info.Environment[name] = value;
        }

        return info;
    }

    private async Task StopServerAsync()
    {
        client?.Dispose();
        client = null;
        if (server is { } process)
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await process.WaitForExitAsync(timeout.Token);
            process.Dispose();
            server = null;
        }

        serverLog?.Dispose();
        serverLog = null;
    }

    public async Task DisposeAsync()
    {
        await StopServerAsync();
        if (Directory.Exists(DatabaseDirectory))
        {
            Directory.Delete(DatabaseDirectory, recursive: true);
        }
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "MetadataApi.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException("Run the E2E suite from a built repository checkout.");
    }
}
