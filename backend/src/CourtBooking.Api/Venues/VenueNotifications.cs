using CourtBooking.Api.Bookings;
using CourtBooking.Api.Data;
using CourtBooking.Api.Email;
using CourtBooking.Api.Observability;
using Microsoft.EntityFrameworkCore;

namespace CourtBooking.Api.Venues;

/// <summary>
/// What a venue is waiting to be told (PRD US-17).
///
/// Two things pile up at a counter nobody is standing at: slips nobody has looked at, and money
/// that has to be moved or answered for. Each is somebody's to deal with, and who that is comes
/// from the permission rather than from a list of addresses — so the people told are exactly the
/// people who can act.
///
/// The number beside a door and the message in an inbox say the same thing, and both are decided
/// here, so a venue cannot see a count it was never told about or be told of work its screen does
/// not show. That is why the rule about when money is waiting lives in this file and not at the
/// four places money moves.
/// </summary>
public sealed class VenueNotifications(
    AppDbContext database,
    ITransactionalEmailSender emails,
    ILoggerFactory loggers)
{
    /// <summary>
    /// A slip has arrived and nobody has looked at it yet (PRD US-17).
    /// </summary>
    public Task SlipArrivedAsync(
        Guid venueId,
        Guid bookingId,
        bool seenBefore,
        CancellationToken cancellationToken) =>
        TellAsync(
            seenBefore ? Notice.SlipSeenBefore : Notice.SlipWaiting,
            venueId,
            bookingId,
            cancellationToken);

    /// <summary>
    /// A booking's money may need somebody. Whether it does is decided here rather than by the
    /// caller: an amount owed back has to be sent by hand (BR-06), and a payment nobody has
    /// confirmed leaves the amount itself unknown (6.2) — and a booking with neither is a booking
    /// nobody has to think about.
    /// </summary>
    public Task MoneyMayBeWaitingAsync(
        Guid venueId,
        Guid bookingId,
        decimal refundDue,
        PaymentState payment,
        CancellationToken cancellationToken) =>
        Waiting(refundDue, payment) switch
        {
            { } notice => TellAsync(notice, venueId, bookingId, cancellationToken),
            _ => Task.CompletedTask,
        };

    /// <summary>
    /// How much is waiting, for the reader who is asking. Each number is only counted for somebody
    /// who could do something about it: a member who checks slips is not shown the money, and a
    /// member who handles money is not shown the queue.
    /// </summary>
    public async Task<VenueAttentionResponse> WaitingForAsync(
        VenueMembership membership,
        CancellationToken cancellationToken)
    {
        // A venue that may not act is shown no work: every door these numbers sit on is refused
        // to it, and a count nobody can clear is only a reproach (PRD US-20).
        if (VenueStatusRules.IsFrozen(membership.Venue?.Status))
        {
            return new VenueAttentionResponse(0, 0);
        }

        var venueId = membership.VenueId;

        var slips = membership.Allows(Who(Notice.SlipWaiting).Permission)
            ? await database.Bookings.CountAsync(
                booking => booking.VenueId == venueId
                    && booking.Status == BookingStatus.PendingVerification,
                cancellationToken)
            : 0;

        var money = membership.Allows(Who(Notice.RefundOwed).Permission)
            ? await database.Bookings.CountAsync(
                booking => booking.VenueId == venueId
                    && (booking.RefundDueBaht > 0
                        || booking.PaymentState == PaymentState.Unconfirmed),
                cancellationToken)
            : 0;

        return new VenueAttentionResponse(slips, money);
    }

    /// <summary>What a venue is told about. Everything else about a notice follows from this.</summary>
    private enum Notice
    {
        SlipWaiting,
        SlipSeenBefore,
        RefundOwed,
        PaymentUnanswered,
    }

    /// <summary>
    /// Whether a booking's money is waiting for somebody, and for what. The same test the count
    /// above makes, so the number and the message cannot drift apart (PRD 6.2).
    /// </summary>
    private static Notice? Waiting(decimal refundDue, PaymentState payment) =>
        payment == PaymentState.Unconfirmed ? Notice.PaymentUnanswered
        : refundDue > 0 ? Notice.RefundOwed
        : null;

    /// <summary>
    /// Who a notice is for, and whether they may ask not to hear it. Only the slip may be turned
    /// off: it is the one that arrives on an ordinary day, where the others mean somebody is
    /// waiting for money (PRD US-17).
    /// </summary>
    private static (VenuePermissions Permission, bool CanBeSilenced) Who(Notice notice) =>
        notice switch
        {
            Notice.SlipWaiting or Notice.SlipSeenBefore => (VenuePermissions.VerifySlip, true),
            _ => (VenuePermissions.ManageBookings, false),
        };

    /// <summary>
    /// Tells the people who may act on this at this venue. The owner holds every permission and
    /// so is always among them (PRD 8) — including the person who has just pressed something: the
    /// money still has to be sent, so their own press did not finish the work.
    ///
    /// A venue that is not approved is told nothing. It cannot act on any of this, and a message
    /// about work it is barred from doing is only noise.
    /// </summary>
    private async Task TellAsync(
        Notice notice,
        Guid venueId,
        Guid bookingId,
        CancellationToken cancellationToken)
    {
        var (permission, canBeSilenced) = Who(notice);

        var members = await database.VenueMemberships
            .AsNoTracking()
            .Where(member => member.VenueId == venueId)
            .Where(member => member.Venue!.Status == VenueStatus.Approved)
            .Where(member => !canBeSilenced || member.WantsSlipEmails)
            .Select(member => new
            {
                Member = member,
                Address = member.User!.Email,
                Language = member.User!.Language,
            })
            .ToListAsync(cancellationToken);

        var told = members
            .Where(member => member.Member.Allows(permission))
            .Where(member => !string.IsNullOrEmpty(member.Address))
            .ToList();

        var (subject, body) = Words(notice, bookingId);

        foreach (var member in told)
        {
            try
            {
                // Not the request's token: the write this is about has already been committed,
                // and a booker closing their browser must not be what decides whether the venue
                // hears about money it owes.
                await emails.SendAsync(
                    new EmailMessage(member.Address!, member.Language, subject, body),
                    CancellationToken.None);
            }
            catch (Exception failure)
            {
                // The work is durable and the number beside the door still shows it, so a
                // message that would not send is worth saying out loud and nothing more —
                // certainly not a failure answer for a write that succeeded.
                loggers.CreateLogger<VenueNotifications>().LogError(
                    failure,
                    "Could not tell {Recipient} about {Notice} on booking {BookingId}.",
                    Redact(member.Address!), notice, bookingId);
            }
        }

        // Counted rather than named: who was told is in the mail log, and what matters here is
        // that a venue was told at all. The name is not one PRD 8 lists; it is written down with
        // the rest of that debt in CLAUDE.md.
        AppEvents.For(loggers).LogInformation(
            "venue_notified {VenueId} {BookingId} {Notice} {Told}",
            venueId, bookingId, notice, told.Count);
    }

    /// <summary>Enough to match a support request against an account, not enough to harvest.</summary>
    private static string Redact(string address)
    {
        var at = address.IndexOf('@', StringComparison.Ordinal);
        return at <= 1 ? "***" : $"{address[0]}***{address[at..]}";
    }

    /// <summary>
    /// What a notice says. In English until US-06 brings templates in both languages; the language
    /// each reader chose already travels with the message. One switch with no default arm, so a
    /// notice added later cannot quietly go out wearing another one's words.
    /// </summary>
    private static (string Subject, string Body) Words(Notice notice, Guid bookingId) =>
        notice switch
        {
            Notice.SlipWaiting => (
                "A slip is waiting to be checked",
                $"A booker has sent a slip for booking {bookingId}. It is in the slip queue."),

            Notice.SlipSeenBefore => (
                "A slip you have seen before has arrived again",
                $"The slip sent for booking {bookingId} has the same bytes as one this venue has "
                + "been sent before. Check it against the transfer."),

            Notice.RefundOwed => (
                "A booking is owed money back",
                $"Booking {bookingId} is owed money back. The venue sends it and records that "
                + "it did."),

            Notice.PaymentUnanswered => (
                "A booking needs an answer about its payment",
                $"Booking {bookingId} was given up while its slip was still being checked. Until "
                + "the venue says whether the money arrived, nobody knows what is owed."),

            // Not a default: a notice added later should stop here rather than go out quietly
            // wearing another one's words.
            _ => throw new ArgumentOutOfRangeException(nameof(notice), notice, null),
        };
}
