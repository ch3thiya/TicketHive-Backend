using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Notification.Service.Services;

public class EmailJsService : IEmailService
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<EmailJsService> _logger;
    private readonly string _apiUrl;
    private readonly string _serviceId;
    private readonly string _templateId;
    private readonly string _publicKey;
    private readonly string _privateKey;

    public EmailJsService(
        HttpClient httpClient,
        IConfiguration configuration,
        ILogger<EmailJsService> logger)
    {
        _httpClient = httpClient;
        _logger = logger;

        _apiUrl = configuration["EmailJS:ApiUrl"] ?? "https://api.emailjs.com/api/v1.0/email/send";
        _serviceId = configuration["EmailJS:ServiceId"]
            ?? configuration["Email__ServiceId"]
            ?? string.Empty;
        _templateId = configuration["EmailJS:TemplateId"]
            ?? configuration["Email__TemplateId"]
            ?? string.Empty;
        _publicKey = configuration["EmailJS:PublicKey"]
            ?? configuration["Email__PublicKey"]
            ?? configuration["Email__ApiKey"]
            ?? string.Empty;
        _privateKey = configuration["EmailJS:PrivateKey"]
            ?? configuration["Email__PrivateKey"]
            ?? string.Empty;
    }

    public async Task<EmailResult> SendTicketConfirmationAsync(
        string toEmail,
        string customerName,
        Guid orderId,
        decimal totalAmount,
        string currency,
        IReadOnlyList<string> ticketCodes)
    {
        if (string.IsNullOrWhiteSpace(toEmail))
        {
            return new EmailResult(false, "Recipient email address is required.");
        }

        var codesSummary = ticketCodes.Count > 0
            ? string.Join(", ", ticketCodes)
            : "TKT-PENDING-ISSUANCE";

        var templateParams = new Dictionary<string, object>
        {
            ["to_email"] = toEmail,
            ["customer_name"] = string.IsNullOrWhiteSpace(customerName) ? "Valued Customer" : customerName,
            ["order_id"] = orderId.ToString(),
            ["ticket_codes"] = codesSummary,
            ["ticket_count"] = ticketCodes.Count,
            ["total_amount"] = $"{totalAmount:F2} {currency}",
            ["booking_ref"] = orderId.ToString().Substring(0, 8).ToUpper()
        };

        // If EmailJS credentials are not yet set up, fallback to simulation log
        if (string.IsNullOrWhiteSpace(_serviceId) || string.IsNullOrWhiteSpace(_publicKey))
        {
            _logger.LogWarning(
                "[SIMULATION MODE] EmailJS keys not configured. Simulating email delivery to {Email} for Order {OrderId}. Ticket codes: {Codes}",
                toEmail, orderId, codesSummary);
            return new EmailResult(true, null);
        }

        var payload = new Dictionary<string, object>
        {
            ["service_id"] = _serviceId,
            ["template_id"] = _templateId,
            ["user_id"] = _publicKey,
            ["template_params"] = templateParams
        };

        if (!string.IsNullOrWhiteSpace(_privateKey))
        {
            payload["accessToken"] = _privateKey;
        }

        _logger.LogInformation("[EmailJS Debug] Preparing EmailJS POST request to {Url} (ServiceId: {ServiceId}, TemplateId: {TemplateId}, PublicKey: {PublicKey}, ToEmail: {ToEmail})",
            _apiUrl, _serviceId, _templateId, _publicKey, toEmail);

        try
        {
            var json = JsonSerializer.Serialize(payload);
            _logger.LogInformation("[EmailJS Debug] Request payload JSON: {Json}", json);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            var response = await _httpClient.PostAsync(_apiUrl, content);
            var responseBody = await response.Content.ReadAsStringAsync();

            if (response.IsSuccessStatusCode)
            {
                _logger.LogInformation("[EmailJS Debug] EmailJS API call SUCCESS (HTTP {StatusCode}). Response: {Body}. Email sent to {Email} for Order {OrderId}",
                    response.StatusCode, responseBody, toEmail, orderId);
                return new EmailResult(true, null);
            }

            _logger.LogWarning("[EmailJS Debug] EmailJS API FAILED with HTTP {StatusCode}: {ResponseBody}", response.StatusCode, responseBody);
            return new EmailResult(false, $"EmailJS API returned {response.StatusCode}: {responseBody}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[EmailJS Debug] Exception while attempting to call EmailJS API to {Email} for Order {OrderId}", toEmail, orderId);
            return new EmailResult(false, ex.Message);
        }
    }
}
