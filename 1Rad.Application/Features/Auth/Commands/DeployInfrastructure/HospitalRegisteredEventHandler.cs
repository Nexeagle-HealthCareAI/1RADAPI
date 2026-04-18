using _1Rad.Application.Interfaces;
using _1Rad.Domain.Events;
using MediatR;
using Microsoft.Extensions.Logging;

namespace _1Rad.Application.Features.Auth.Commands.DeployInfrastructure;

public class HospitalRegisteredEventHandler : INotificationHandler<HospitalRegisteredEvent>
{
    private readonly IEmailService _emailService;
    private readonly ILogger<HospitalRegisteredEventHandler> _logger;

    public HospitalRegisteredEventHandler(IEmailService emailService, ILogger<HospitalRegisteredEventHandler> logger)
    {
        _emailService = emailService;
        _logger = logger;
    }

    public async Task Handle(HospitalRegisteredEvent notification, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Handling HospitalRegisteredEvent for User: {UserId}, Hospital: {HospitalId}", 
            notification.User.UserId, notification.Hospital.HospitalId);

        try
        {
            var subject = "Welcome to 1Rad Clinical Hub!";
            var body = $@"
                <h1>Congratulations, {notification.User.FullName}!</h1>
                <p>Your facility, <strong>{notification.Hospital.HospitalName}</strong>, has been successfully deployed on the 1Rad Clinical Hub.</p>
                <p>You can now log in and start managing your clinical missions.</p>
                <br/>
                <p>Best Regards,<br/>1Rad Operations Team</p>";

            await _emailService.SendEmailAsync(notification.User.Email, subject, body);
            
            _logger.LogInformation("Welcome email successfully sent to {Email}", notification.User.Email);
        }
        catch (Exception ex)
        {
            // We catch but don't rethrow here to prevent the main transaction from failing 
            // due to a post-save notification error (Optional: can be handled via outbox pattern in future)
            _logger.LogError(ex, "Failed to send welcome email to {Email} for Hospital {HospitalId}", 
                notification.User.Email, notification.Hospital.HospitalId);
        }
    }
}
