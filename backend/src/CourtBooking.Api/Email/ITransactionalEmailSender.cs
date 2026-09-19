namespace CourtBooking.Api.Email;

/// <summary>
/// One outgoing message. Language travels with it because background jobs send mail too and have no
/// request context (PRD US-06); the text itself moves into localized templates with US-06/US-23.
/// </summary>
public sealed record EmailMessage(string To, string Language, string Subject, string Body);

/// <summary>
/// Transactional email. A real provider replaces the development implementation before the pilot.
/// Named to avoid colliding with <c>Microsoft.AspNetCore.Identity.IEmailSender&lt;TUser&gt;</c>.
/// </summary>
public interface ITransactionalEmailSender
{
    Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default);
}
