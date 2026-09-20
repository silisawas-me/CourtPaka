using CourtBooking.Api.Bookings;
using CourtBooking.Api.Data;
using CourtBooking.Api.Email;
using CourtBooking.Api.Observability;
using Microsoft.EntityFrameworkCore;

namespace CourtBooking.Api.Venues;

/// <summary>
/// What a venue is waiting to be told (PRD US-17).
///
/// Three things pile up at a counter that nobody is standing at: slips nobody has looked at, money
/// a booker is owed and nobody has sent, and money nobody has said whether they received. Each is
/// somebody's to deal with, and who that is comes from the permission rather than from a list of
/// addresses — so the people told are exactly the people who can act.
///
/// The counts and the mail say the same thing in two ways, and both are asked of this, so a venue
/// cannot see a number it was never told about.
/// </summary>
public sealed class VenueNotifications(
    AppDbContext database,
    ITransactionalEmailSender emails,
    ILoggerFactory loggers)
{
    /// <summary>
    /// A slip has arrived and nobody has looked at it yet. The people who may look are told, and
    /// each of them may turn this one off — it is the only one of the three that can be, because
    /// it is the only one that arrives on an ordinary day rather than when something is stuck
    /// (PRD US-17).
    /// </summary>
    public async Task SlipArrivedAsync(
        Guid venueId,
        Guid bookingId,
        bool seenBefore,
        CancellationToken cancellationToken)
    {
        await TellAsync(
            venueId,
            VenuePermissions.VerifySlip,
            seenBefore ? Notice.SlipSeenBefore : Notice.SlipWaiting,
            bookingId,
            onlyThoseWhoWantSlipMail: true,
            cancellationToken);
    }

    /// <summary>
    /// A booking has money on it that somebody has to move or answer for: an amount owed back, or
    /// a payment nobody has confirmed since the booker gave the hours up (PRD US-17, 6.2).
    /// </summary>
    public async Task MoneyWaitingAsync(
        Guid venueId,
        Guid bookingId,
        bool awaitingAnswer,
        CancellationToken cancellationToken)
    {
        await TellAsync(
            venueId,
            VenuePermissions.ManageBookings,
            awaitingAnswer ? Notice.PaymentUnanswered : Notice.RefundOwed,
            bookingId,
            onlyThoseWhoWantSlipMail: false,
            cancellationToken);
    }

    /// <summary>
    /// How much is waiting, for the reader who is asking. Each number is only counted for somebody
    /// who could do something about it: a member who checks slips is not shown the money, and a
    /// member who handles money is not shown the queue.
    /// </summary>
    public async Task<VenueAttentionResponse> WaitingForAsync(
        VenueMembership membership,
        CancellationToken cancellationToken)
    {
        var venueId = membership.VenueId;

        var slips = membership.Allows(VenuePermissions.VerifySlip)
            ? await database.Bookings.CountAsync(
                booking => booking.VenueId == venueId
                    && booking.Status == BookingStatus.PendingVerification,
                cancellationToken)
            : 0;

        var money = membership.Allows(VenuePermissions.ManageBookings)
            ? await database.Bookings
                .Where(booking => booking.VenueId == venueId)
                .Select(booking => new { booking.RefundDueBaht, booking.PaymentState })
                .Where(booking =>
                    booking.RefundDueBaht > 0
                    || booking.PaymentState == PaymentState.Unconfirmed)
                .CountAsync(cancellationToken)
            : 0;

        var unanswered = membership.Allows(VenuePermissions.ManageBookings)
            ? await database.Bookings.CountAsync(
                booking => booking.VenueId == venueId
                    && booking.PaymentState == PaymentState.Unconfirmed,
                cancellationToken)
            : 0;

        return new VenueAttentionResponse(slips, money, unanswered);
    }

    /// <summary>What a venue is told about, and the words that name it.</summary>
    private enum Notice
    {
        SlipWaiting,
        SlipSeenBefore,
        RefundOwed,
        PaymentUnanswered,
    }

    /// <summary>
    /// Tells the people who hold a permission at this venue. The owner holds every permission and
    /// so is always among them (PRD 8), and a venue that has been suspended is told nothing — it
    /// cannot act on any of this.
    /// </summary>
    private async Task TellAsync(
        Guid venueId,
        VenuePermissions permission,
        Notice notice,
        Guid bookingId,
        bool onlyThoseWhoWantSlipMail,
        CancellationToken cancellationToken)
    {
        var members = await database.VenueMemberships
            .AsNoTracking()
            .Where(member => member.VenueId == venueId)
            .Where(member => !onlyThoseWhoWantSlipMail || member.WantsSlipEmails)
            .Select(member => new
            {
                member.Role,
                member.Permissions,
                Address = member.User!.Email,
                Language = member.User!.Language,
            })
            .ToListAsync(cancellationToken);

        var told = members
            .Where(member =>
                member.Role == VenueRole.Owner || member.Permissions.HasFlag(permission))
            .Where(member => !string.IsNullOrEmpty(member.Address))
            .ToList();

        foreach (var member in told)
        {
            await emails.SendAsync(
                new EmailMessage(
                    member.Address!,
                    member.Language,
                    Subject(notice),
                    Body(notice, bookingId)),
                cancellationToken);
        }

        // Counted rather than named: who was told is in the mail log, and what matters here is
        // that a venue was told at all (PRD 8).
        AppEvents.For(loggers).LogInformation(
            "venue_notified {VenueId} {BookingId} {Notice} {Told}",
            venueId, bookingId, notice, told.Count);
    }

    /// <summary>
    /// The words, in English, until US-06 brings templates in both languages. The subject is what
    /// a reader sees first and is kept short enough to survive a phone's inbox.
    /// </summary>
    private static string Subject(Notice notice) => notice switch
    {
        Notice.SlipWaiting => "A slip is waiting to be checked",
        Notice.SlipSeenBefore => "A slip you have seen before has arrived again",
        Notice.RefundOwed => "A booking is owed money back",
        _ => "A booking needs an answer about its payment",
    };

    private static string Body(Notice notice, Guid bookingId) => notice switch
    {
        Notice.SlipWaiting =>
            $"A booker has sent a slip for booking {bookingId}. It is in the slip queue.",
        Notice.SlipSeenBefore =>
            $"The slip sent for booking {bookingId} has the same bytes as one this venue has "
            + "been sent before. Check it against the transfer.",
        Notice.RefundOwed =>
            $"Booking {bookingId} is owed money back. The venue sends it and records that it "
            + "did.",
        _ =>
            $"Booking {bookingId} was given up while its slip was still being checked. Until "
            + "the venue says whether the money arrived, nobody knows what is owed.",
    };
}
