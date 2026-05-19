namespace ThanyMarcus.Shared.Saga;

public sealed class SagaOwnershipLostException : Exception
{
    public Guid JobId { get; }
    public string? AttemptedWorkerId { get; }

    public SagaOwnershipLostException(Guid jobId, string? workerId)
        : base(BuildMessage(jobId, workerId))
    {
        JobId = jobId;
        AttemptedWorkerId = workerId;
    }

    public SagaOwnershipLostException(Guid jobId, string? workerId, Exception innerException)
        : base(BuildMessage(jobId, workerId), innerException)
    {
        JobId = jobId;
        AttemptedWorkerId = workerId;
    }

    private static string BuildMessage(Guid jobId, string? workerId) =>
        $"Saga ownership lost: job {jobId} no longer claimed by worker {workerId ?? "<unknown>"}.";
}
