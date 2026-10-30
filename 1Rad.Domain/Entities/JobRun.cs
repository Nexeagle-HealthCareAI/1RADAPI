namespace _1Rad.Domain.Entities;

/// <summary>
/// One claimed run slot of a scheduled job (dbo.JobRuns, DB script 99). The unique
/// (JobName, RunKey) index lets exactly one API instance run each slot (gap analysis P3-9).
/// </summary>
public class JobRun
{
    public long Id { get; set; }
    public string JobName { get; set; } = string.Empty;
    public string RunKey { get; set; } = string.Empty;
    public string? ClaimedBy { get; set; }
    public DateTime ClaimedAt { get; set; } = DateTime.UtcNow;
}
