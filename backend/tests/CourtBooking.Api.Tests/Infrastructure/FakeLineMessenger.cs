using System.Collections.Concurrent;
using CourtBooking.Api.Identity;

namespace CourtBooking.Api.Tests.Infrastructure;

/// <summary>
/// LINE's push endpoint, standing still (PRD US-34). Off until a test switches it on, so every
/// test that was written before there was a second way to reach a booker goes on seeing email.
/// </summary>
public sealed class FakeLineMessenger : ILineMessenger
{
    private readonly ConcurrentQueue<LineMessage> _sent = new();

    /// <summary>Whether this deployment can push at all, which is what decides the channel.</summary>
    public bool IsEnabled { get; set; }

    /// <summary>Set to make LINE refuse, the way it does for an id it does not know.</summary>
    public bool Refuses { get; set; }

    /// <summary>
    /// Everything pushed to one LINE account, oldest first. By id rather than by counting what
    /// arrived: one of these is shared by the whole run, and classes run alongside each other.
    /// </summary>
    public IReadOnlyList<LineMessage> To(string lineUserId) =>
        [.. _sent.Where(message => message.To == lineUserId)];

    public Task<bool> SendAsync(LineMessage message, CancellationToken cancellationToken)
    {
        if (Refuses)
        {
            return Task.FromResult(false);
        }

        _sent.Enqueue(message);
        return Task.FromResult(true);
    }
}
