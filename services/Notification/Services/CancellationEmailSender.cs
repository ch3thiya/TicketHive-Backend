using System.Net.Http.Json;
using Microsoft.Extensions.Options;
using Notification.Service.Models;

namespace Notification.Service.Services;

public class CancellationEmailOptions
{
    public bool Simulate { get; set; } = true;
    public string ServiceId { get; set; } = "";
    public string TemplateId { get; set; } = "";
    public string PublicKey { get; set; } = "";
    public string PrivateKey { get; set; } = "";
}

public class CancellationEmailSender(HttpClient http, IOptions<CancellationEmailOptions> options)
{
    public async Task<string> SendAsync(CancellationEmail email)
    {
        if (string.IsNullOrWhiteSpace(email.CustomerEmail)) return "MissingRecipient";
        var settings = options.Value;
        if (settings.Simulate) return "Simulated";
        if (string.IsNullOrWhiteSpace(settings.TemplateId) || string.IsNullOrWhiteSpace(settings.ServiceId) || string.IsNullOrWhiteSpace(settings.PublicKey))
            return "NeedsReconciliation";
        using var response = await http.PostAsJsonAsync("https://api.emailjs.com/api/v1.0/email/send", new
        {
            service_id = settings.ServiceId,
            template_id = settings.TemplateId,
            user_id = settings.PublicKey,
            accessToken = settings.PrivateKey,
            template_params = new
            {
                to_email = email.CustomerEmail,
                customer_name = email.CustomerName,
                subject = email.Reason == "Show" ? "Your show has been cancelled" : "Your order has been cancelled",
                show_id = email.ShowId,
                order_ids = string.Join(", ", email.OrderIds),
                total_amount = $"{email.Amount:F2} {email.Currency}",
                message = email.Simulated
                    ? "Your tickets have been cancelled. A full refund has been simulated in the sandbox; no money was moved."
                    : email.Amount == 0 ? "Your unpaid reservation has been cancelled. No refund was required."
                    : "Your tickets have been cancelled and your full refund has been processed."
            }
        });
        return response.IsSuccessStatusCode ? "Sent" : "NeedsReconciliation";
    }
}
