namespace ThanyMarcus.Cloud.Api.Features.Processing.Specialists;

public static class SpecialistChannels
{
    public const string TasksChanged = "extraction_tasks_changed";

    public static string NewTasksChannel(string sidecar) => $"extraction_tasks_{sidecar}_new";
}
