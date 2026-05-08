using _1Rad.Application.Interfaces;
using ClosedXML.Excel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.IO;

namespace _1Rad.Infrastructure.BackgroundJobs;

public class DailyReferralExcelReportJob : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<DailyReferralExcelReportJob> _logger;

    public DailyReferralExcelReportJob(IServiceProvider serviceProvider, ILogger<DailyReferralExcelReportJob> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Daily Referral Excel Report Job started.");

        while (!stoppingToken.IsCancellationRequested)
        {
            var now = DateTime.Now;
            // Target time: 9:05 PM (21:05) - staggered slightly after the financial report
            var nextRunTime = new DateTime(now.Year, now.Month, now.Day, 21, 5, 0);

            if (now > nextRunTime)
            {
                nextRunTime = nextRunTime.AddDays(1);
            }

            var delay = nextRunTime - now;
            _logger.LogInformation("Next Referral Audit scheduled for {NextRunTime} (in {DelayHours:F2} hours)", nextRunTime, delay.TotalHours);

            try
            {
                await Task.Delay(delay, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            try
            {
                _logger.LogInformation("Generating Daily Referral Excel Reports at {Time}", DateTime.Now);
                await GenerateAndSendReportsAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error occurred while generating daily referral reports.");
            }
        }
    }

    private async Task GenerateAndSendReportsAsync(CancellationToken stoppingToken)
    {
        using var scope = _serviceProvider.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<IApplicationDbContext>();
        var emailService = scope.ServiceProvider.GetRequiredService<IEmailService>();

        var today = DateTime.Today;
        // Calculate start of the current week (Monday)
        var startOfWeek = today.AddDays(-(int)today.DayOfWeek + (int)DayOfWeek.Monday);
        if (today < startOfWeek) startOfWeek = startOfWeek.AddDays(-7);

        var hospitals = await context.Hospitals
            .IgnoreQueryFilters()
            .Where(h => h.Status == "Active")
            .ToListAsync(stoppingToken);

        foreach (var hospital in hospitals)
        {
            try
            {
                var referrers = await context.Referrers
                    .IgnoreQueryFilters()
                    .Where(r => r.HospitalId == hospital.HospitalId)
                    .ToListAsync(stoppingToken);

                if (!referrers.Any()) continue;

                using var workbook = new XLWorkbook();
                var worksheet = workbook.Worksheets.Add("Daily_Referral_Audit");

                // --- STYLING & HEADERS ---
                worksheet.Cell(1, 1).Value = "REFERRER_NAME";
                worksheet.Cell(1, 2).Value = "TODAY_PATIENT_COUNT";
                worksheet.Cell(1, 3).Value = "WEEKLY_PATIENT_COUNT";
                worksheet.Cell(1, 4).Value = "CONTACT_NUMBER";

                var headerRange = worksheet.Range(1, 1, 1, 4);
                headerRange.Style.Font.Bold = true;
                headerRange.Style.Font.FontColor = XLColor.White;
                headerRange.Style.Fill.BackgroundColor = XLColor.FromHtml("#0f52ba");
                headerRange.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;

                int row = 2;
                foreach (var referrer in referrers)
                {
                    // Count unique patients sent today
                    var todayCount = await context.Appointments
                        .IgnoreQueryFilters()
                        .Include(a => a.Patient)
                        .Where(a => a.HospitalId == hospital.HospitalId && 
                                    a.Patient.ReferrerId == referrer.ReferrerId && 
                                    a.CreatedAt.Date == today)
                        .Select(a => a.PatientId)
                        .Distinct()
                        .CountAsync(stoppingToken);

                    // Count unique patients sent this week
                    var weekCount = await context.Appointments
                        .IgnoreQueryFilters()
                        .Include(a => a.Patient)
                        .Where(a => a.HospitalId == hospital.HospitalId && 
                                    a.Patient.ReferrerId == referrer.ReferrerId && 
                                    a.CreatedAt.Date >= startOfWeek)
                        .Select(a => a.PatientId)
                        .Distinct()
                        .CountAsync(stoppingToken);

                    worksheet.Cell(row, 1).Value = referrer.Name;
                    worksheet.Cell(row, 2).Value = todayCount;
                    worksheet.Cell(row, 3).Value = weekCount;
                    worksheet.Cell(row, 4).Value = referrer.Contact ?? "N/A";
                    
                    // Highlight rows with activity today
                    if (todayCount > 0)
                    {
                        worksheet.Row(row).Style.Fill.BackgroundColor = XLColor.FromHtml("#f0fdf4");
                    }

                    row++;
                }

                worksheet.Columns().AdjustToContents();
                worksheet.RangeUsed().Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
                worksheet.RangeUsed().Style.Border.InsideBorder = XLBorderStyleValues.Thin;

                // Prepare Stream
                using var stream = new MemoryStream();
                workbook.SaveAs(stream);
                var excelContent = stream.ToArray();

                // Fetch Admin Emails
                var adminEmails = await context.UserHospitalMappings
                    .IgnoreQueryFilters()
                    .Where(m => m.HospitalId == hospital.HospitalId)
                    .Include(m => m.Roles)
                    .Include(m => m.User)
                    .Where(m => m.Roles.Any(r => r.RoleName == "Admin" || r.RoleName == "AdminDoctor"))
                    .Select(m => m.User.Email)
                    .Distinct()
                    .ToListAsync(stoppingToken);

                if (!adminEmails.Any()) continue;

                var subject = $"[REFERRAL_AUDIT] {hospital.HospitalName} - {today:dd MMM yyyy}";
                var body = $@"
                    <div style='font-family: sans-serif; padding: 25px; border: 1px solid #e2e8f0; border-radius: 12px;'>
                        <h2 style='color: #0f52ba; margin-top: 0;'>Daily Referral Performance Audit</h2>
                        <p style='color: #475569;'>Facility: <strong>{hospital.HospitalName}</strong></p>
                        <p style='color: #475569;'>Audit Date: <strong>{today:D}</strong></p>
                        <hr style='border: 0; border-top: 1px solid #f1f5f9; margin: 20px 0;'/>
                        <p>Please find the attached Excel report containing the detailed breakdown of patients referred today and during the current week.</p>
                        <p style='font-size: 12px; color: #64748b;'>Weekly counts are calculated from {startOfWeek:dd MMM yyyy} to current.</p>
                        <br/>
                        <div style='padding: 15px; background: #f8fafc; border-radius: 8px; font-size: 11px; color: #94a3b8;'>
                            This is an automated clinical audit generated by the 1Rad Intelligence Engine.
                        </div>
                    </div>";

                foreach (var email in adminEmails)
                {
                    if (string.IsNullOrEmpty(email)) continue;
                    var fileName = $"Referral_Audit_{hospital.HospitalName?.Replace(" ", "_")}_{today:yyyyMMdd}.xlsx";
                    await emailService.SendEmailWithAttachmentAsync(email, subject, body, excelContent, fileName);
                }

                _logger.LogInformation("Referral Excel report sent for {HospitalName} to {Count} admins.", hospital.HospitalName, adminEmails.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to generate referral report for {HospitalName}", hospital.HospitalName);
            }
        }
    }
}
