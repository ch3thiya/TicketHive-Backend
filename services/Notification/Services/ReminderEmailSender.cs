using System.Net.Http.Json;
using Microsoft.Extensions.Options;
using Notification.Service.Models;

namespace Notification.Service.Services;

public interface IReminderEmailSender
{
    Task<ReminderSendResult> SendAsync(ReminderEmail email, CancellationToken cancellationToken = default);
}

/// <summary>
/// Simulated by default: Reminders:Simulate=true records the reminder without calling any provider.
/// Live delivery (Simulate=false) needs the EmailJS service, template and keys in the Reminders section.
/// </summary>
public class ReminderEmailSender(HttpClient http, IOptions<ReminderOptions> options) : IReminderEmailSender
{
    public async Task<ReminderSendResult> SendAsync(ReminderEmail email, CancellationToken cancellationToken = default)
    {
        var settings = options.Value;
        if (settings.Simulate)
        {
            return new ReminderSendResult(ReminderSendOutcome.Simulated);
        }

        if (string.IsNullOrWhiteSpace(settings.ServiceId) || string.IsNullOrWhiteSpace(settings.TemplateId) || string.IsNullOrWhiteSpace(settings.PublicKey))
        {
            return new ReminderSendResult(ReminderSendOutcome.Rejected, "Live reminder delivery is not configured.");
        }

        try
        {
            using var response = await http.PostAsJsonAsync("https://api.emailjs.com/api/v1.0/email/send", new
            {
                service_id = settings.ServiceId,
                template_id = settings.TemplateId,
                user_id = settings.PublicKey,
                accessToken = settings.PrivateKey,
                template_params = new
                {
                    to_email = email.RecipientEmail,
                    customer_name = email.CustomerName,
                    subject = email.Subject,
                    message = email.Message,
                    event_name = email.EventName,
                    show_time = email.When,
                    venue = email.Venue,
                    ticket_count = email.TicketCount
                }
            }, cancellationToken);

            return response.IsSuccessStatusCode
                ? new ReminderSendResult(ReminderSendOutcome.Sent)
                : new ReminderSendResult(ReminderSendOutcome.Rejected, $"The email provider refused the reminder (HTTP {(int)response.StatusCode}).");
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            // No answer at all: the email may have been delivered, so it must never be sent again by itself.
            return new ReminderSendResult(ReminderSendOutcome.Ambiguous, $"The email provider did not answer ({ex.GetType().Name}).");
        }
    }
}
