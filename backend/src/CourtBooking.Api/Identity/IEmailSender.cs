namespace CourtBooking.Api.Identity;

/// <summary>
/// Transactional email (PRD US-06). A real provider replaces this before the pilot;
/// until then the development implementation logs the message.
/// </summary>
public interface IEmailSender
{
    Task SendAsync(string toEmail, string subject, string body, CancellationToken cancellationToken = default);
}

public sealed class LoggingEmailSender(ILogger<LoggingEmailSender> logger) : IEmailSender
{
    public Task SendAsync(string toEmail, string subject, string body, CancellationToken cancellationToken = default)
    {
        logger.LogInformation("Email to {Recipient}: {Subject}\n{Body}", toEmail, subject, body);
        return Task.CompletedTask;
    }
}
