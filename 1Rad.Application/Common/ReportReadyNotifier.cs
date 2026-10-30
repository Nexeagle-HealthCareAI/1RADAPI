using _1Rad.Application.Interfaces;
using _1Rad.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace _1Rad.Application.Common;

/// <summary>
/// Tells the patient on WhatsApp that their report is ready, with their tracking link
/// (gap analysis P2-10). Before Oct 2026 nothing told a patient their report was ready;
/// delivery depended on the printed QR slip.
///
/// Sent once, when a visit first becomes REPORTED (all its service lines signed Final).
/// Off by default. Configuration:
///   Notifications:ReportReady:Enabled           true to send (also needs WhatsApp:IsEnabled)
///   Notifications:ReportReady:TemplateName      approved Meta utility template, default "report_ready"
///   Notifications:ReportReady:TemplateLanguage  default "en"
///   Notifications:PublicAppBaseUrl              web app origin for /track links
///                                               (falls back to ReferralLinks:PortalBaseUrl)
/// The template takes three body parameters, in order: patient name, centre name, link.
/// Best-effort: a failure is logged and never blocks the sign-off.
/// </summary>
public interface IReportReadyNotifier
{
    Task NotifyAsync(Appointment appointment, CancellationToken ct);
}

public class ReportReadyNotifier : IReportReadyNotifier
{
    private readonly IApplicationDbContext _db;
    private readonly ISmsService _sms;
    private readonly ITrackingTokenService _tokens;
    private readonly IConfiguration _config;
    private readonly ILogger<ReportReadyNotifier> _logger;

    public ReportReadyNotifier(IApplicationDbContext db, ISmsService sms, ITrackingTokenService tokens,
        IConfiguration config, ILogger<ReportReadyNotifier> logger)
    {
        _db = db;
        _sms = sms;
        _tokens = tokens;
        _config = config;
        _logger = logger;
    }

    private bool Enabled =>
        string.Equals(_config["Notifications:ReportReady:Enabled"]?.Trim(), "true", StringComparison.OrdinalIgnoreCase);

    private string? BaseUrl
    {
        get
        {
            var url = _config["Notifications:PublicAppBaseUrl"];
            if (string.IsNullOrWhiteSpace(url)) url = _config["ReferralLinks:PortalBaseUrl"];
            return string.IsNullOrWhiteSpace(url) ? null : url.Trim().TrimEnd('/');
        }
    }

    public async Task NotifyAsync(Appointment appointment, CancellationToken ct)
    {
        if (!Enabled) return;
        try
        {
            var mobile = ReferralLinkSender.NormalizeMobile(appointment.Mobile);
            if (mobile == null)
            {
                _logger.LogInformation("Report-ready message skipped for {AppointmentId}: no usable mobile.", appointment.AppointmentId);
                return;
            }
            var baseUrl = BaseUrl;
            if (baseUrl == null)
            {
                _logger.LogWarning("Report-ready message skipped: set Notifications:PublicAppBaseUrl.");
                return;
            }

            var centreName = await _db.Hospitals.AsNoTracking()
                .Where(h => h.HospitalId == appointment.HospitalId)
                .Select(h => h.HospitalName)
                .FirstOrDefaultAsync(ct);

            var token = _tokens.Issue(appointment.AppointmentId);
            var link = $"{baseUrl}/track/{appointment.AppointmentId}?token={Uri.EscapeDataString(token)}";

            await _sms.SendReportReadyAsync(mobile,
                string.IsNullOrWhiteSpace(appointment.PatientName) ? "Patient" : appointment.PatientName!,
                string.IsNullOrWhiteSpace(centreName) ? "your diagnostic centre" : centreName!,
                link);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Report-ready message failed for appointment {AppointmentId}.", appointment.AppointmentId);
        }
    }
}
