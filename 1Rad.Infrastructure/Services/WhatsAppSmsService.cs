using System.Text;
using System.Text.Json;
using _1Rad.Application.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace _1Rad.Infrastructure.Services;

public class WhatsAppSmsService : ISmsService
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<WhatsAppSmsService> _logger;
    private readonly string _apiUrl;
    private readonly string _accessToken;
    private readonly bool _isEnabled;
    private readonly string _referralTemplate;
    private readonly string _referralTemplateLang;

    public WhatsAppSmsService(HttpClient httpClient, IConfiguration configuration, ILogger<WhatsAppSmsService> logger)
    {
        _httpClient = httpClient;
        _logger = logger;

        var config = configuration.GetSection("WhatsApp");
        _apiUrl = config["ApiUrl"] ?? string.Empty;
        _accessToken = config["AccessToken"] ?? string.Empty;
        _isEnabled = string.Equals((config["IsEnabled"] ?? "false").Trim(), "true", StringComparison.OrdinalIgnoreCase);
        // Name + language of the approved utility template used to deliver a
        // doctor's portal link. Configurable so the centre can point at whatever
        // template they got approved in Meta without a code change.
        _referralTemplate = config["ReferralTemplateName"] ?? "referral_portal";
        _referralTemplateLang = config["ReferralTemplateLanguage"] ?? "en";
    }

    public async Task SendOtpAsync(string mobileNumber, string otp)
    {
        if (!_isEnabled)
        {
            _logger.LogWarning("WhatsApp integration is disabled in configuration.");
            return;
        }

        var payload = new
        {
            messaging_product = "whatsapp",
            to = mobileNumber,
            type = "template",
            template = new
            {
                name = "otp",
                language = new { code = "en" },
                components = new object[]
                {
                    new
                    {
                        type = "body",
                        parameters = new object[] { new { type = "text", text = otp } }
                    },
                    new
                    {
                        type = "button",
                        sub_type = "url",
                        index = 0,
                        parameters = new object[] { new { type = "text", text = otp } }
                    }
                }
            }
        };

        await PostAsync(payload, mobileNumber, "OTP");
    }

    public async Task SendReferralLinkAsync(string mobileNumber, string doctorName, string centreName, string link)
    {
        if (!_isEnabled)
        {
            // Surface this as a failure (not a silent skip) so the operator knows
            // why nothing was delivered and can flip the integration on.
            throw new InvalidOperationException("WHATSAPP_DISABLED: WhatsApp messaging is turned off in configuration.");
        }

        var payload = new
        {
            messaging_product = "whatsapp",
            to = mobileNumber,
            type = "template",
            template = new
            {
                name = _referralTemplate,
                language = new { code = _referralTemplateLang },
                components = new object[]
                {
                    new
                    {
                        type = "body",
                        // Order matters — must match the approved template's {{1}}{{2}}{{3}}.
                        parameters = new object[]
                        {
                            new { type = "text", text = string.IsNullOrWhiteSpace(doctorName) ? "Doctor" : doctorName },
                            new { type = "text", text = string.IsNullOrWhiteSpace(centreName) ? "your diagnostic centre" : centreName },
                            new { type = "text", text = link }
                        }
                    }
                }
            }
        };

        await PostAsync(payload, mobileNumber, "referral link");
    }

    // Shared sender for all template messages. Throws a descriptive exception on a
    // non-success response so callers can report per-recipient failures.
    private async Task PostAsync(object payload, string mobileNumber, string context)
    {
        try
        {
            var json = JsonSerializer.Serialize(payload);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            var request = new HttpRequestMessage(HttpMethod.Post, _apiUrl) { Content = content };
            request.Headers.Add("Authorization", $"Bearer {_accessToken}");

            var response = await _httpClient.SendAsync(request);

            if (!response.IsSuccessStatusCode)
            {
                var error = await response.Content.ReadAsStringAsync();
                _logger.LogError("Failed to send WhatsApp {Context}. Status: {Status}, Error: {Error}", context, response.StatusCode, error);
                throw new Exception($"WHATSAPP_GATEWAY_FAILURE: Status {response.StatusCode}. Details: {error}");
            }

            _logger.LogInformation("WhatsApp {Context} sent successfully to {Mobile}", context, mobileNumber);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception while sending WhatsApp {Context} to {Mobile}", context, mobileNumber);
            throw;
        }
    }
}
