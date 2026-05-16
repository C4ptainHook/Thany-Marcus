using System.Diagnostics;

namespace ThanyMarcus.Portal.Tests.SagaWorker.Terraform;

internal static class TerraformProcess
{
    public static bool IsAvailable()
    {
        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        var sep = OperatingSystem.IsWindows() ? ';' : ':';
        var exe = OperatingSystem.IsWindows() ? "terraform.exe" : "terraform";
        foreach (var part in pathEnv.Split(sep, StringSplitOptions.RemoveEmptyEntries))
        {
            if (File.Exists(Path.Combine(part, exe))) return true;
        }
        return false;
    }

    public static string LocateModule(string relativePath)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, relativePath);
            if (Directory.Exists(candidate)) return Path.GetFullPath(candidate);
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException($"Could not locate {relativePath} from {AppContext.BaseDirectory}");
    }

    public static async Task<TerraformResult> RunAsync(
        string[] args,
        string workdir,
        IReadOnlyDictionary<string, string>? env = null,
        CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo
        {
            FileName               = "terraform",
            WorkingDirectory       = workdir,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
        };
        foreach (var arg in args) psi.ArgumentList.Add(arg);
        psi.Environment["TF_INPUT"] = "0";
        psi.Environment["TF_IN_AUTOMATION"] = "1";
        if (env is not null)
        {
            foreach (var (k, v) in env) psi.Environment[k] = v;
        }

        using var p = Process.Start(psi)
            ?? throw new InvalidOperationException("terraform: Process.Start returned null");
        var stdoutTask = p.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = p.StandardError.ReadToEndAsync(ct);
        await p.WaitForExitAsync(ct);
        return new TerraformResult(p.ExitCode, await stdoutTask, await stderrTask);
    }
}

internal sealed record TerraformResult(int ExitCode, string Stdout, string Stderr);
