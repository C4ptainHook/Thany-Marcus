namespace ThanyMarcus.Portal.SagaWorker.Infrastructure.Terraform;

public sealed record TerraformResult(int ExitCode, string Stdout, string Stderr)
{
    public bool Success => ExitCode == 0;
}
