using _1Rad.Domain.Common;

namespace _1Rad.Domain.Entities;

/// <summary>
/// A centre's rules for the report-writing AI (assist, formatter, voice draft,
/// grammar fallback). One row per centre; no row = the defaults below.
/// </summary>
public class CentreAiPolicy : BaseEntity, IHospitalContext
{
    public Guid HospitalId { get; set; }

    /// <summary>Report AI on/off for the whole centre.</summary>
    public bool ReportAiEnabled { get; set; } = true;

    /// <summary>
    /// Voice typing that is NOT done on the computer itself (the browser's
    /// cloud speech service, e.g. Chrome sending audio to Google). Off by
    /// default: dictation then works only where on-device recognition exists.
    /// </summary>
    public bool AllowCloudDictation { get; set; }

    /// <summary>Report-AI requests allowed per calendar month; null = server default.</summary>
    public int? MonthlyCallLimit { get; set; }

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public Guid? UpdatedByUserId { get; set; }
}

/// <summary>
/// One row per report-AI request: who, which feature, model, real token
/// counts, and whether patient identifiers were masked. Never the text itself.
/// </summary>
public class ReportAiUsageLog : BaseEntity, IHospitalContext
{
    public Guid ReportAiUsageLogId { get; set; } = Guid.NewGuid();
    public Guid HospitalId { get; set; }
    public Guid? UserId { get; set; }

    /// <summary>voice | assist:&lt;action&gt; | format | grammar</summary>
    public string Feature { get; set; } = string.Empty;
    public string? Model { get; set; }

    public int InputTokens { get; set; }
    public int OutputTokens { get; set; }
    public int CacheReadInputTokens { get; set; }
    public int CacheCreationInputTokens { get; set; }

    /// <summary>How many patient identifiers were replaced before sending.</summary>
    public int MaskedCount { get; set; }

    /// <summary>ok | error | refused</summary>
    public string Outcome { get; set; } = "ok";

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
