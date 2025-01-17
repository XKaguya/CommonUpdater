namespace CommonUpdater.Native;

public class ProjectEntry
{
    public bool MaintenanceMode { get; set; } = false;
    public string ProjectName { get; set; } = string.Empty;
    public string ProjectCurrentVersion { get; set; } = string.Empty;
    public string ProjectVersion { get; set; } = string.Empty;
    public bool ProjectForceUpdate { get; set; } = false;
}