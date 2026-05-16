namespace ThanyMarcus.Portal.SagaWorker.Infrastructure.Terraform;

public sealed class TerraformException(string operation, TerraformResult result)
    : Exception($"terraform {operation} failed with exit code {result.ExitCode}: {result.Stderr}")
{
    public string Operation { get; } = operation;
    public TerraformResult Result { get; } = result;
}
