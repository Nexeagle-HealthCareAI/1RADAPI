using _1Rad.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace _1Rad.Infrastructure.BackgroundJobs;

/// <summary>
/// Runs daily to manage subscription lifecycle:
/// - Marks expiring subscriptions (3 days before end)
/// - Applies 2-day grace period after expiry
/// - Locks hospitals that have exceeded the grace period with no payment
/// </summary>
public class SubscriptionLifecycleJob : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<SubscriptionLifecycleJob> _logger;
    private readonly TimeSpan _interval = TimeSpan.FromHours(6); // Run every 6 hours

    public SubscriptionLifecycleJob(
        IServiceScopeFactory scopeFactory,
        ILogger<SubscriptionLifecycleJob> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("[SubscriptionLifecycle] Service started.");

        // Run immediately on startup, then on interval
        await RunCycleAsync(stoppingToken);

        using PeriodicTimer timer = new(_interval);
        while (!stoppingToken.IsCancellationRequested && await timer.WaitForNextTickAsync(stoppingToken))
        {
            await RunCycleAsync(stoppingToken);
        }
    }

    private async Task RunCycleAsync(CancellationToken ct)
    {
        _logger.LogInformation("[SubscriptionLifecycle] Running lifecycle check at {Time}", DateTime.UtcNow);
        try
        {
            using var scope = _scopeFactory.CreateScope();
            // Bypass multi-tenant query filter — this job operates across ALL hospitals
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var now = DateTime.UtcNow;

            // Load all subscriptions (ignore global HospitalId filter for this admin job)
            var subscriptions = await db.HospitalSubscriptions
                .IgnoreQueryFilters()
                .Where(s => s.Status != "Locked")
                .ToListAsync(ct);

            int notified = 0, expired = 0, locked = 0;

            foreach (var sub in subscriptions)
            {
                var daysRemaining = (sub.EndDate - now).TotalDays;
                var daysPastExpiry = (now - sub.EndDate).TotalDays;

                // 1. Mark as Expiring (3+ days notice before end)
                if (sub.Status == "Active" && daysRemaining <= 3 && daysRemaining > 0 && sub.NotificationSentAt == null)
                {
                    sub.Status = "Expiring";
                    sub.NotificationSentAt = now;
                    notified++;
                    _logger.LogInformation("[SubscriptionLifecycle] HospitalId={HId} marked Expiring ({Days:F1} days left)", sub.HospitalId, daysRemaining);
                }

                // 2. Trial/subscription has passed EndDate — enter grace period (still unlocked)
                if ((sub.Status == "Active" || sub.Status == "Expiring") && daysRemaining <= 0)
                {
                    sub.Status = "Expired";
                    expired++;
                    _logger.LogInformation("[SubscriptionLifecycle] HospitalId={HId} marked Expired (grace period begins)", sub.HospitalId);
                }

                // 3. Lock after 2-day grace period — check no approved payment exists
                if (sub.Status == "Expired" && !sub.IsLocked && daysPastExpiry >= 2)
                {
                    // Check if hospital has an approved payment request
                    var hasApprovedPayment = await db.SubscriptionPaymentRequests
                        .IgnoreQueryFilters()
                        .AnyAsync(r => r.HospitalId == sub.HospitalId && r.Status == "Approved" && r.CreatedAt > sub.EndDate, ct);

                    if (!hasApprovedPayment)
                    {
                        sub.IsLocked = true;
                        sub.LockedAt = now;
                        sub.LockReason = sub.IsTrial ? "TrialGracePeriodExpired" : "PaymentGracePeriodExpired";
                        sub.Status = "Locked";
                        locked++;
                        _logger.LogWarning("[SubscriptionLifecycle] HospitalId={HId} LOCKED — {Reason}", sub.HospitalId, sub.LockReason);
                    }
                }
            }

            await db.SaveChangesAsync(ct);
            _logger.LogInformation("[SubscriptionLifecycle] Cycle complete. Notified={N} Expired={E} Locked={L}", notified, expired, locked);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[SubscriptionLifecycle] Error during lifecycle check");
        }
    }
}
