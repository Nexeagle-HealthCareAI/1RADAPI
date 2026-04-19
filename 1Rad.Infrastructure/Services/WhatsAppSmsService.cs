using System.Net.Http.Headers;
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
    private readonly string _isEnabled;

    public WhatsAppSmsService(HttpClient httpClient, IConfiguration configuration, ILogger<WhatsAppSmsService> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
        
        var config = configuration.GetSection("WhatsApp");
        _apiUrl = config["ApiUrl"] ?? string.Empty;
        _accessToken = config["AccessToken"] ?? string.Empty;
        _isEnabled = config["IsEnabled"] ?? "false";
    }

    public async Task SendOtpAsync(string mobileNumber, string otp)
    {
        try
        {
            if (!string.IsNullOrEmpty(_isEnabled) && _isEnabled.Trim().ToLower() == "true")
            {
                var payload = new
                {
                    messaging_product = "whatsapp",
                    to = mobileNumber,
                    type = "template",
                    template = new
                    {
                        name = "otp",
                        language = new
                        {
                            code = "en"
                        },
                        components = new object[]
                        {
                            new
                            {
                                type = "body",
                                parameters = new object[]
                                {
                                    new
                                    {
                                        type = "text",
                                        text = otp
                                    }
                                }
                            },
                            new
                            {
                                type = "button",
                                sub_type = "url",
                                index = 0,
                                parameters = new object[]
                                {
                                    new
                                    {
                                        type = "text",
                                        text = otp
                                    }
                                }
                            }
                        }
                    }
                };

                var json = JsonSerializer.Serialize(payload);
                var content = new StringContent(json, Encoding.UTF8, "application/json");

                var request = new HttpRequestMessage(HttpMethod.Post, _apiUrl)
                {
                    Content = content
                };
                request.Headers.Add("Authorization", $"Bearer {_accessToken}");

                var response = await _httpClient.SendAsync(request);

                if (!response.IsSuccessStatusCode)
                {
                    var error = await response.Content.ReadAsStringAsync();
                    _logger.LogError("Failed to send WhatsApp OTP. Status: {Status}, Error: {Error}", response.StatusCode, error);
                    throw new Exception($"WHATSAPP_GATEWAY_FAILURE: Status {response.StatusCode}. Details: {error}");
                }
                else
                {
                    _logger.LogInformation("WhatsApp OTP template sent successfully to {Mobile}", mobileNumber);
                }
            }
            else
            {
                _logger.LogWarning("WhatsApp integration is disabled in configuration.");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception while sending WhatsApp OTP to {Mobile}", mobileNumber);
            throw; // Rethrow to let the handler catch it
        }
    }
}
