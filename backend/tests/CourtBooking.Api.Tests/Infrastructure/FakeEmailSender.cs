using System.Collections.Concurrent;
using CourtBooking.Api.Email;

namespace CourtBooking.Api.Tests.Infrastructure;

public sealed class FakeEmailSender : ITransactionalEmailSender
{
    private readonly ConcurrentQueue<EmailMessage> _sent = new();

    public EmailMessage LastTo(string email) =>
        _sent.Last(message => string.Equals(message.To, email, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Everything written to one address, oldest first. By address rather than by counting what
    /// arrived: one of these is shared by the whole test run, and classes run alongside each
    /// other — a count would be somebody else's mail as often as not.
    /// </summary>
    public IReadOnlyList<EmailMessage> To(string email) =>
        [.. _sent.Where(message =>
            string.Equals(message.To, email, StringComparison.OrdinalIgnoreCase))];

    public Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default)
    {
        _sent.Enqueue(message);
        return Task.CompletedTask;
    }
}
