namespace ThanyMarcus.Cloud.Api.Infrastructure.Sidecars;

public static class IngestNotifyChannels
{
    public const string IngestJobsNew = "ingest_jobs_new";
    public const string IngestJobsChanged = "ingest_jobs_changed";
    public const string ExtractionTasksChanged = "extraction_tasks_changed";

    public static string ExtractionTasksNewForSidecar(string sidecar) =>
        $"extraction_tasks_{sidecar}_new";
}
