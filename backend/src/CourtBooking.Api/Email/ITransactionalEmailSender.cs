namespace CourtBooking.Api.Email;

/// <summary>
/// One outgoing message, already written in the recipient's language (PRD US-06, US-23). Language
/// travels with it because background jobs send mail too and have no request context.
///
/// <see cref="Template"/> names which message it is — "auth.verify", "venue.slip_waiting" — in a
/// form that does not change with the language or the wording: what tests assert on, and what a
/// provider's own templates can be keyed by when one is chosen.
/// </summary>
public sealed record EmailMessage(
    string To,
    string Language,
    string Subject,
    string Body,
    string? Template = null);

/// <summary>
/// Transactional email. A real provider replaces the development implementation before the pilot.
/// Named to avoid colliding with <c>Microsoft.AspNetCore.Identity.IEmailSender&lt;TUser&gt;</c>.
/// </summary>
public interface ITransactionalEmailSender
{
    Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default);
}
