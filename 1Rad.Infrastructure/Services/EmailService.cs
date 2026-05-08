using _1Rad.Application.Interfaces;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using MimeKit;

namespace _1Rad.Infrastructure.Services;

public class EmailService : IEmailService
{
    private readonly IConfiguration _configuration;
    private readonly ILogger<EmailService> _logger;

    public EmailService(IConfiguration configuration, ILogger<EmailService> logger)
    {
        _configuration = configuration;
        _logger = logger;
    }

    public async Task SendEmailAsync(string to, string subject, string body)
    {
        await SendEmailWithAttachmentAsync(to, subject, body, null, null);
    }

    public async Task SendEmailWithAttachmentAsync(string to, string subject, string body, byte[]? attachment, string? fileName)
    {
        var config = _configuration.GetSection("Smtp");
        
        var message = new MimeMessage();
        message.From.Add(new MailboxAddress("1Rad Clinical Hub", config["SenderEmail"]));
        message.To.Add(new MailboxAddress("", to));
        message.Subject = subject;

        var bodyBuilder = new BodyBuilder { HtmlBody = body };
        if (attachment != null && !string.IsNullOrEmpty(fileName))
        {
            bodyBuilder.Attachments.Add(fileName, attachment);
        }
        message.Body = bodyBuilder.ToMessageBody();

        using var client = new SmtpClient();
        try
        {
            await client.ConnectAsync(config["Server"], int.Parse(config["Port"]), SecureSocketOptions.StartTls);
            await client.AuthenticateAsync(config["SenderEmail"], config["AppPassword"]);
            await client.SendAsync(message);
            await client.DisconnectAsync(true);
            
            _logger.LogInformation("Email dispatched to {To} (HasAttachment: {HasAttachment})", to, attachment != null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send email to {To}", to);
            throw;
        }
    }
}
