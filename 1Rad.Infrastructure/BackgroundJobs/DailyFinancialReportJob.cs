using _1Rad.Application.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Text;

namespace _1Rad.Infrastructure.BackgroundJobs;

public class DailyFinancialReportJob : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<DailyFinancialReportJob> _logger;

    public DailyFinancialReportJob(IServiceProvider serviceProvider, ILogger<DailyFinancialReportJob> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Daily Financial Report Job started.");

        while (!stoppingToken.IsCancellationRequested)
        {
            var now = DateTime.Now;
            // Target time: 9 PM (21:00)
            var nextRunTime = new DateTime(now.Year, now.Month, now.Day, 21, 0, 0);

            if (now > nextRunTime)
            {
                nextRunTime = nextRunTime.AddDays(1);
            }

            var delay = nextRunTime - now;
            _logger.LogInformation("Next Daily Report scheduled for {NextRunTime} (in {DelayHours:F2} hours)", nextRunTime, delay.TotalHours);

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
                _logger.LogInformation("Executing Daily Financial Reports at {Time}", DateTime.Now);
                await SendDailyReportsAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error occurred while sending daily financial reports.");
            }
        }
    }

    private async Task SendDailyReportsAsync(CancellationToken stoppingToken)
    {
        using var scope = _serviceProvider.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<IApplicationDbContext>();
        var emailService = scope.ServiceProvider.GetRequiredService<IEmailService>();

        // We use local date for the report summary
        var today = DateTime.Today;
        
        // Fetch all hospitals (Ignore Global Filters as we are running globally for all facilities)
        var hospitals = await context.Hospitals
            .IgnoreQueryFilters()
            .Where(h => h.Status == "Active")
            .ToListAsync(stoppingToken);

        foreach (var hospital in hospitals)
        {
            try
            {
                // 1. Calculate Revenue (Actual cash collected today via payments)
                var revenue = await context.Payments
                    .IgnoreQueryFilters()
                    .Where(p => p.HospitalId == hospital.HospitalId && p.CreatedAt.Date == today)
                    .SumAsync(p => p.Amount, stoppingToken);

                // 2. Calculate Outstanding (Amount invoiced today but still pending)
                var pending = await context.Invoices
                    .IgnoreQueryFilters()
                    .Where(i => i.HospitalId == hospital.HospitalId && i.CreatedAt.Date == today && i.Status == "PENDING")
                    .SumAsync(i => i.TotalAmount - i.PaidAmount, stoppingToken);

                // 3. Calculate Expenses (Total outflow logged today)
                var expenses = await context.Expenses
                    .IgnoreQueryFilters()
                    .Where(e => e.HospitalId == hospital.HospitalId && e.TransactionDate.Date == today)
                    .SumAsync(e => e.Amount, stoppingToken);

                // 4. Calculate Referral Commissions Logged Today
                var referrals = await context.ReferralCommissions
                    .IgnoreQueryFilters()
                    .Where(rc => rc.HospitalId == hospital.HospitalId && rc.CreatedAt.Date == today)
                    .SumAsync(rc => rc.CommissionAmount, stoppingToken);

                var netProfit = revenue - expenses;

                // 5. Identify Recipients (Admins or AdminDoctors mapped to this hospital)
                var adminEmails = await context.UserHospitalMappings
                    .IgnoreQueryFilters()
                    .Where(m => m.HospitalId == hospital.HospitalId)
                    .Include(m => m.Roles)
                    .Include(m => m.User)
                    .Where(m => m.Roles.Any(r => r.RoleName == "Admin" || r.RoleName == "AdminDoctor"))
                    .Select(m => m.User.Email)
                    .Distinct()
                    .ToListAsync(stoppingToken);

                if (!adminEmails.Any())
                {
                    _logger.LogWarning("No admin recipients found for hospital: {HospitalName}", hospital.HospitalName);
                    continue;
                }

                // 6. Build and Send Email
                var subject = $"[DAILY_REVENUE_REPORT] {hospital.HospitalName} - {today:dd MMM yyyy}";
                var body = BuildEmailBody(hospital.HospitalName ?? "Unknown Facility", today, revenue, pending, expenses, referrals, netProfit);

                foreach (var email in adminEmails)
                {
                    if (string.IsNullOrEmpty(email)) continue;
                    await emailService.SendEmailAsync(email, subject, body);
                }

                _logger.LogInformation("Daily report dispatched for {HospitalName} to {AdminCount} recipients.", hospital.HospitalName, adminEmails.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to process daily report for hospital {HospitalName} ({HospitalId})", hospital.HospitalName, hospital.HospitalId);
            }
        }
    }

    private string BuildEmailBody(string hospitalName, DateTime date, decimal revenue, decimal pending, decimal expenses, decimal referrals, decimal profit)
    {
        var sb = new StringBuilder();
        sb.Append("<div style='font-family: \"Segoe UI\", Tahoma, Geneva, Verdana, sans-serif; padding: 30px; background-color: #f8fafc; border-radius: 16px; max-width: 600px; margin: auto;'>");
        
        sb.Append("<div style='background: linear-gradient(135deg, #0f52ba 0%, #061a40 100%); padding: 25px; border-radius: 12px; color: white; margin-bottom: 25px;'>");
        sb.Append($"<h2 style='margin: 0; font-size: 22px; font-weight: 900;'>DAILY FINANCIAL SUMMARY</h2>");
        sb.Append($"<p style='margin: 5px 0 0 0; opacity: 0.8; font-size: 14px;'>{hospitalName} | {date:D}</p>");
        sb.Append("</div>");

        sb.Append("<div style='background: white; padding: 25px; border-radius: 12px; border: 1px solid #e2e8f0; box-shadow: 0 4px 6px rgba(0,0,0,0.02);'>");
        
        sb.Append("<table style='width: 100%; border-collapse: collapse;'>");
        
        // Revenue
        sb.Append("<tr>");
        sb.Append("<td style='padding: 12px 0; border-bottom: 1px solid #f1f5f9; color: #64748b; font-size: 13px; font-weight: 700;'>CASH_COLLECTED (REVENUE)</td>");
        sb.Append($"<td style='padding: 12px 0; border-bottom: 1px solid #f1f5f9; color: #059669; text-align: right; font-size: 16px; font-weight: 950;'>₹{revenue:N2}</td>");
        sb.Append("</tr>");

        // Pending
        sb.Append("<tr>");
        sb.Append("<td style='padding: 12px 0; border-bottom: 1px solid #f1f5f9; color: #64748b; font-size: 13px; font-weight: 700;'>OUTSTANDING_RECEIVABLES</td>");
        sb.Append($"<td style='padding: 12px 0; border-bottom: 1px solid #f1f5f9; color: #ea580c; text-align: right; font-size: 16px; font-weight: 950;'>₹{pending:N2}</td>");
        sb.Append("</tr>");

        // Expenses
        sb.Append("<tr>");
        sb.Append("<td style='padding: 12px 0; border-bottom: 1px solid #f1f5f9; color: #64748b; font-size: 13px; font-weight: 700;'>OPERATIONAL_OUTFLOW</td>");
        sb.Append($"<td style='padding: 12px 0; border-bottom: 1px solid #f1f5f9; color: #dc2626; text-align: right; font-size: 16px; font-weight: 950;'>₹{expenses:N2}</td>");
        sb.Append("</tr>");

        // Referrals
        sb.Append("<tr>");
        sb.Append("<td style='padding: 12px 0; border-bottom: 1px solid #f1f5f9; color: #64748b; font-size: 13px; font-weight: 700;'>REFERRAL_CUTS_LOGGED</td>");
        sb.Append($"<td style='padding: 12px 0; border-bottom: 1px solid #f1f5f9; color: #881337; text-align: right; font-size: 16px; font-weight: 950;'>₹{referrals:N2}</td>");
        sb.Append("</tr>");

        // Net Profit
        sb.Append("<tr>");
        sb.Append("<td style='padding: 20px 0 0 0; color: #1e293b; font-size: 15px; font-weight: 900;'>DAILY_NET_MARGIN</td>");
        sb.Append($"<td style='padding: 20px 0 0 0; color: #0f52ba; text-align: right; font-size: 20px; font-weight: 950;'>₹{profit:N2}</td>");
        sb.Append("</tr>");

        sb.Append("</table>");
        sb.Append("</div>");
        
        sb.Append("<div style='margin-top: 25px; text-align: center; font-size: 11px; color: #94a3b8;'>");
        sb.Append("<p>Automated Financial Intelligence from <strong>1Rad Clinical Hub</strong></p>");
        sb.Append("<p>© 2026 1Rad Systems. All rights reserved.</p>");
        sb.Append("</div>");
        
        sb.Append("</div>");
        return sb.ToString();
    }
}
