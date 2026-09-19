using System.Collections.Concurrent;
using CourtBooking.Api.Identity;

namespace CourtBooking.Api.Tests.Infrastructure;

public sealed record SentEmail(string To, string Subject, string Body);

public sealed class FakeEmailSender : IEmailSender
{
    private readonly ConcurrentQueue<SentEmail> _sent = new();

    public IReadOnlyCollection<SentEmail> Sent => _sent;

    public SentEmail LastTo(string email) =>
        _sent.Last(message => string.Equals(message.To, email, StringComparison.OrdinalIgnoreCase));

    public Task SendAsync(string toEmail, string subject, string body, CancellationToken cancellationToken = default)
    {
        _sent.Enqueue(new SentEmail(toEmail, subject, body));
        return Task.CompletedTask;
    }
}
