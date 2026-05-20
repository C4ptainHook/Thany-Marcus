using System.Collections.Immutable;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Configuration;

namespace ThanyMarcus.Portal.SagaWorker.Infrastructure.Terraform;

public sealed partial class TerraformRunner(IConfiguration config, ILogger<TerraformRunner> log) : ITerraformRunner
{
    private readonly string binaryPath = config.GetValue("Terraform:BinaryPath", "/usr/local/bin/terraform")!;
    private readonly string pluginCache = config.GetValue("Terraform:PluginCacheDir", "/var/lib/portal/terraform/plugin-cache")!;

    public Task<TerraformResult> InitAsync(string workdir, IReadOnlyDictionary<string, string> backendConfig, CancellationToken ct)
    {
        var args = new List<string> { "init", "-input=false", "-no-color" };
        foreach (var (k, v) in backendConfig)
        {
            args.Add($"-backend-config={k}={v}");
        }
        return RunAsync(workdir, args, ImmutableDictionary<string, string>.Empty, ct);
    }

    public async Task<TerraformResult> SelectOrCreateWorkspaceAsync(string workdir, string workspaceName, CancellationToken ct)
    {
        var select = await RunAsync(workdir,
            ["workspace", "select", "-no-color", workspaceName],
            ImmutableDictionary<string, string>.Empty, ct);
        if (select.Success)
        {
            return select;
        }
        return await RunAsync(workdir,
            ["workspace", "new", "-no-color", workspaceName],
            ImmutableDictionary<string, string>.Empty, ct);
    }

    public Task<TerraformResult> SelectWorkspaceAsync(string workdir, string workspaceName, CancellationToken ct) =>
        RunAsync(workdir,
            ["workspace", "select", "-no-color", workspaceName],
            ImmutableDictionary<string, string>.Empty, ct);

    public Task<TerraformResult> PlanAsync(string workdir, IReadOnlyDictionary<string, string> envVars, CancellationToken ct) =>
        RunAsync(workdir, ["plan", "-input=false", "-no-color", "-out=plan.tfplan", "-json"], envVars, ct);

    public Task<TerraformResult> ApplyAsync(string workdir, IReadOnlyDictionary<string, string> envVars, CancellationToken ct) =>
        RunAsync(workdir, ["apply", "-input=false", "-no-color", "-json", "plan.tfplan"], envVars, ct);

    public Task<TerraformResult> DestroyAsync(string workdir, IReadOnlyDictionary<string, string> envVars, CancellationToken ct) =>
        RunAsync(workdir, ["destroy", "-auto-approve", "-input=false", "-no-color", "-json"], envVars, ct);

    public async Task<JsonDocument> OutputJsonAsync(string workdir, CancellationToken ct)
    {
        var result = await RunAsync(workdir, ["output", "-json", "-no-color"], ImmutableDictionary<string, string>.Empty, ct);
        if (!result.Success)
        {
            throw new TerraformException("output", result);
        }
        return JsonDocument.Parse(result.Stdout);
    }

    public async Task<TerraformResult> DeleteWorkspaceAsync(string workdir, string workspaceName, CancellationToken ct)
    {
        var select = await RunAsync(workdir,
            ["workspace", "select", "default", "-no-color"],
            ImmutableDictionary<string, string>.Empty, ct);
        if (!select.Success)
        {
            return select;
        }
        return await RunAsync(workdir,
            ["workspace", "delete", "-force", "-no-color", workspaceName],
            ImmutableDictionary<string, string>.Empty, ct);
    }

    public async Task<bool> HasResourcesAsync(string workdir, CancellationToken ct)
    {
        if (!Directory.Exists(workdir))
        {
            return false;
        }
        var result = await RunAsync(workdir,
            ["state", "list", "-no-color"],
            ImmutableDictionary<string, string>.Empty, ct);
        if (!result.Success)
        {
            return false;
        }
        return !string.IsNullOrWhiteSpace(result.Stdout);
    }

    public async Task ForceUnlockAsync(string workdir, string lockId, CancellationToken ct)
    {
        var result = await RunAsync(workdir, ["force-unlock", "-force", lockId], ImmutableDictionary<string, string>.Empty, ct);
        if (!result.Success)
        {
            throw new TerraformException("force-unlock", result);
        }
    }

    private async Task<TerraformResult> RunAsync(
        string workdir,
        List<string> args,
        IReadOnlyDictionary<string, string> envVars,
        CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName               = binaryPath,
            WorkingDirectory       = workdir,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
        };
        foreach (var arg in args)
        {
            psi.ArgumentList.Add(arg);
        }
        psi.Environment["TF_PLUGIN_CACHE_DIR"] = pluginCache;
        psi.Environment["TF_INPUT"]            = "0";
        psi.Environment["TF_IN_AUTOMATION"]    = "1";
        foreach (var (k, v) in envVars)
        {
            psi.Environment[k] = v;
        }

        LogStarting(log, args[0], workdir);

        using var p = Process.Start(psi)
            ?? throw new InvalidOperationException($"terraform {args[0]}: Process.Start returned null");

        var stdoutTask = p.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = p.StandardError.ReadToEndAsync(ct);

        try
        {
            await p.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            try { p.Kill(entireProcessTree: true); } catch { /* best-effort */ }
            throw;
        }

        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        var result = new TerraformResult(p.ExitCode, stdout, stderr);
        LogFinished(log, args[0], p.ExitCode);
        return result;
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Information,
        Message = "terraform {Subcommand} starting in {Workdir}")]
    private static partial void LogStarting(ILogger logger, string subcommand, string workdir);

    [LoggerMessage(EventId = 2, Level = LogLevel.Information,
        Message = "terraform {Subcommand} exited with code {ExitCode}")]
    private static partial void LogFinished(ILogger logger, string subcommand, int exitCode);
}
