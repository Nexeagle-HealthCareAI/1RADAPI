using _1Rad.Application.Features.Referrers.Commands.RenewReferralLinks;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace _1Rad.Infrastructure.BackgroundJobs;

/// <summary>
/// Once a day (10:15 local), renews doctor-portal links that a centre deliberately sent and
/// that are about to expire — over the same channel, to the same portal — so an active
/// doctor never lands on an expired link. All the rules (switches, window, contact checks)
/// live in <see cref="RenewExpiringReferralLinksCommandHandler"/>; this is only the clock.
/// </summary>
public class ReferralLinkRenewalJob : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<ReferralLinkRenewalJob> _logger;

    public ReferralLinkRenewalJob(IServiceProvider serviceProvider, ILogger<ReferralLinkRenewalJob> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Referral link renewal job started.");

        while (!stoppingToken.IsCancellationRequested)
        {
            var now = DateTime.Now;
            var next = new DateTime(now.Year, now.Month, now.Day, 10, 15, 0);
            if (now > next) next = next.AddDays(1);

            try { await Task.Delay(next - now, stoppingToken); }
            catch (OperationCanceledException) { break; }

            try
            {
                using var scope = _serviceProvider.CreateScope();
                var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
                var result = await mediator.Send(new RenewExpiringReferralLinksCommand(), stoppingToken);
                _logger.LogInformation(
                    "Referral link renewal: {Checked} due, {Renewed} renewed, {Failed} failed (retry next run), {Disabled} switched off (no contact).",
                    result.Checked, result.Renewed, result.Failed, result.Disabled);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Referral link renewal run failed.");
            }
        }
    }
}
