using Accessibility.Application.Common.Interfaces;
using Microsoft.Extensions.Configuration;
using SendGrid;
using SendGrid.Helpers.Mail;

namespace Accessibility.Infrastructure.Services;

public class SendGridEmailSender : IEmailSender
{
    private readonly IConfiguration _configuration;
    private readonly HttpClient _httpClient;

    public SendGridEmailSender(IConfiguration configuration, HttpClient? httpClient = null)
    {
        _configuration = configuration;
        _httpClient = httpClient ?? new HttpClient();
    }

    public async Task SendReminderAsync(ReminderEmail email, CancellationToken cancellationToken)
    {
        var apiKey = _configuration["SENDGRID_API_KEY"];
        if (string.IsNullOrWhiteSpace(apiKey) || string.IsNullOrWhiteSpace(email.To))
        {
            return;
        }

        var client = new SendGridClient(apiKey);
        var from = new EmailAddress(
            _configuration["SENDGRID_FROM_EMAIL"] ?? "noreply@example.com",
            _configuration["SENDGRID_FROM_NAME"] ?? "Accessibility Assistant");
        var to = new EmailAddress(email.To);
        var msg = MailHelper.CreateSingleEmail(from, to, email.Subject, email.Body, email.Body);
        var response = await client.SendEmailAsync(msg, cancellationToken).ConfigureAwait(false);
        if ((int)response.StatusCode < 200 || (int)response.StatusCode >= 300)
        {
            throw new InvalidOperationException($"SendGrid request failed with status {(int)response.StatusCode}");
        }
    }
}
