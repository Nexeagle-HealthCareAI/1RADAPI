namespace _1Rad.Application.Interfaces;

/// <summary>
/// The door every report-AI request goes through: is AI on for this centre,
/// is the centre within its monthly allowance, is this user within the
/// per-minute burst limit — and afterwards, record what the request used.
/// </summary>
public interface IReportAiGate
{
    /// <summary>Null when allowed; otherwise the message to show the user.</summary>
    Task<string?> CheckAsync(string feature, CancellationToken ct);

    /// <summary>Records one request (best effort — never throws).</summary>
    Task RecordAsync(string feature, string? model, AiUsage? usage, int maskedCount, string outcome, CancellationToken ct);
}
