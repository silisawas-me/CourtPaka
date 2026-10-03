namespace CourtBooking.Api.Email;

/// <summary>
/// The development stand-in: the whole message goes to the log, so a local stack can follow a
/// verification or invitation link without a mail provider.
/// </summary>
public sealed class LoggingEmailSender(ILogger<LoggingEmailSender> logger) : ITransactionalEmailSender
{
    public Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default)
    {
        logger.LogInformation(
            "Email to {Recipient} [{Language}]: {Subject}\n{Body}",
            message.To, message.Language, message.Subject, message.Body);
        return Task.CompletedTask;
    }
}

/// <summary>
/// What a deployed environment gets until a real provider is wired up (PRD US-06). The body holds
/// single-use verification and invitation tokens, which are stored only as a hash on purpose, so it
/// never reaches the log: only that a message was dropped, and the subject that names which one.
/// </summary>
public sealed class UndeliveredEmailSender(ILogger<UndeliveredEmailSender> logger) : ITransactionalEmailSender
{
    public Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default)
    {
        logger.LogWarning(
            "No email provider is configured, so \"{Subject}\" was not delivered to {Recipient}.",
            message.Subject, Redact(message.To));
        return Task.CompletedTask;
    }

    /// <summary>Enough to match a support request against an account, not enough to harvest.</summary>
    private static string Redact(string address)
    {
        var at = address.IndexOf('@', StringComparison.Ordinal);
        return at <= 1 ? "***" : $"{address[0]}***{address[at..]}";
    }
}
