using System.Collections.Concurrent;
using CourtBooking.Api.Email;

namespace CourtBooking.Api.Tests.Infrastructure;

public sealed class FakeEmailSender : ITransactionalEmailSender
{
    private readonly ConcurrentQueue<EmailMessage> _sent = new();

    public EmailMessage LastTo(string email) =>
        _sent.Last(message => string.Equals(message.To, email, StringComparison.OrdinalIgnoreCase));

    public Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default)
    {
        _sent.Enqueue(message);
        return Task.CompletedTask;
    }
}
