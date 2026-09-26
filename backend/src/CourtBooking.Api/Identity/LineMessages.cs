using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.Extensions.Options;

namespace CourtBooking.Api.Identity;

/// <summary>
/// One message to one person on LINE (PRD US-34). The words are the letter's, so there is one set
/// of them and not a second to keep in step (US-23).
/// </summary>
/// <param name="To">The LINE user id, which is what the login stored against the account.</param>
/// <param name="Template">The constant name of the message, as <c>EmailMessage.Template</c> is.</param>
public sealed record LineMessage(string To, string Language, string Text, string Template);

/// <summary>
/// Pushing a message to somebody on LINE. An interface so a local stack and the tests can stand in
/// for LINE, the same way <see cref="ILineLogin"/> does.
/// </summary>
public interface ILineMessenger
{
    /// <summary>Whether this deployment can reach anybody on LINE at all.</summary>
    bool IsEnabled { get; }

    /// <summary>
    /// Sends it, or does not. It never throws: a message that could not go out must not take down
    /// the work that had already been done, and a booker who closed their phone must not decide
    /// whether the rest of the round runs (PRD US-06, US-34).
    /// </summary>
    Task<bool> SendAsync(LineMessage message, CancellationToken cancellationToken);
}

/// <summary>
/// LINE's push endpoint. Switched on by having a channel access token, so a deployment without one
/// carries on writing to everybody by email and nothing has to be turned off.
/// </summary>
public sealed class LineMessenger(
    HttpClient client,
    IOptions<AppOptions> options,
    ILoggerFactory loggers) : ILineMessenger
{
    private const string PushUrl = "https://api.line.me/v2/bot/message/push";

    /// <summary>LINE refuses anything longer, and a letter this long is a letter nobody reads.</summary>
    private const int MostCharacters = 4900;

    private readonly ILogger<LineMessenger> log = loggers.CreateLogger<LineMessenger>();

    public bool IsEnabled => !string.IsNullOrWhiteSpace(options.Value.Line.MessagingToken);

    public async Task<bool> SendAsync(LineMessage message, CancellationToken cancellationToken)
    {
        if (!IsEnabled)
        {
            return false;
        }

        try
        {
            using var asking = new HttpRequestMessage(HttpMethod.Post, PushUrl)
            {
                Content = JsonContent.Create(new
                {
                    to = message.To,
                    messages = new[] { new { type = "text", text = Short(message.Text) } },
                }),
            };

            asking.Headers.Authorization =
                new AuthenticationHeaderValue("Bearer", options.Value.Line.MessagingToken);

            var answer = await client.SendAsync(asking, cancellationToken);

            if (answer.IsSuccessStatusCode)
            {
                return true;
            }

            // What LINE says went wrong stays out of the log: the body of a refusal can carry the
            // message back, and the message is somebody's booking (PDPA, S-15).
            log.LogWarning(
                "LINE would not take the {Template} message: {Status}.",
                message.Template, (int)answer.StatusCode);

            return false;
        }
        catch (Exception failure)
        {
            log.LogWarning(failure, "Could not reach LINE to send the {Template} message.", message.Template);
            return false;
        }
    }

    private static string Short(string text) =>
        text.Length <= MostCharacters ? text : text[..MostCharacters];
}

/// <summary>
/// A stand-in for local stacks: writes the message where a developer can read it, the way
/// <c>LoggingEmailSender</c> does for email. Only ever registered on a Development host.
/// </summary>
public sealed class LoggingLineMessenger(ILoggerFactory loggers) : ILineMessenger
{
    private readonly ILogger<LoggingLineMessenger> log = loggers.CreateLogger<LoggingLineMessenger>();

    public bool IsEnabled => true;

    public Task<bool> SendAsync(LineMessage message, CancellationToken cancellationToken)
    {
        log.LogInformation(
            "LINE to {To} ({Template}):\n{Text}", message.To, message.Template, message.Text);

        return Task.FromResult(true);
    }
}

/// <summary>
/// Who the platform can reach, and how (PRD US-34). One place, because "did this person get told"
/// has to have one answer: a booker reachable two ways would otherwise be told twice by whichever
/// two callers each picked a different one.
///
/// LINE first when there is a LINE account and this deployment can push to it — somebody who signed
/// in with LINE lives in LINE, and may never have proved an email address at all, which until now
/// meant they were told nothing (PRD US-01, US-06).
/// </summary>
public enum BookerChannel
{
    /// <summary>Nobody to tell: no LINE, no confirmed address, or the account is gone.</summary>
    None = 0,

    Email = 1,
    Line = 2,
}

/// <summary>Where one booker's messages go, and the address to send them to.</summary>
/// <param name="Address">A LINE user id or an email address, depending on <paramref name="Channel"/>.</param>
public sealed record BookerReach(BookerChannel Channel, string? Address)
{
    public static readonly BookerReach Nobody = new(BookerChannel.None, null);

    /// <summary>
    /// The one rule. <paramref name="lineUserId"/> is null for an account that never signed in
    /// with LINE, and for one that has been deleted — deleting an account takes its LINE login
    /// row with it, because a LINE id names a person (PRD US-01, PDPA).
    /// </summary>
    public static BookerReach For(string? lineUserId, string? confirmedEmail, bool canPushToLine) =>
        canPushToLine && !string.IsNullOrEmpty(lineUserId)
            ? new BookerReach(BookerChannel.Line, lineUserId)
            : string.IsNullOrEmpty(confirmedEmail)
                ? Nobody
                : new BookerReach(BookerChannel.Email, confirmedEmail);
}
