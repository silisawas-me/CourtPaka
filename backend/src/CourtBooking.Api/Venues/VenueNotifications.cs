using CourtBooking.Api.Bookings;
using CourtBooking.Api.Data;
using CourtBooking.Api.Email;
using CourtBooking.Api.Observability;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

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
    IOptions<AppOptions> options,
    ILoggerFactory loggers)
{
    /// <summary>
    /// A slip has arrived and nobody has looked at it yet (PRD US-17).
    ///
    /// Takes no cancellation token, and neither does anything below it: every caller is past its
    /// commit by the time it gets here, and a booker who closed their browser must not be what
    /// decides whether the venue hears about work it now has.
    /// </summary>
    public Task SlipArrivedAsync(Guid venueId, Guid bookingId, bool seenBefore) =>
        TellAsync(seenBefore ? VenueNotice.SlipSeenBefore : VenueNotice.SlipWaiting, venueId, bookingId);

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
        PaymentState payment) =>
        Waiting(refundDue, payment) switch
        {
            { } notice => TellAsync(notice, venueId, bookingId),
            _ => Task.CompletedTask,
        };

    /// <summary>
    /// A slip is still waiting and the hours it paid for are about to be played (PRD US-17,
    /// S-23). The owner's alone, and not something anybody can turn off: by now the choice is
    /// between looking at it and a player standing at a counter nobody expected.
    /// </summary>
    public Task SlipStillWaitingAsync(Guid venueId, Guid bookingId) =>
        TellAsync(VenueNotice.SlipStillWaiting, venueId, bookingId);

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

        var slips = membership.Allows(Who(VenueNotice.SlipWaiting).Permission)
            ? await database.Bookings.CountAsync(
                booking => booking.VenueId == venueId
                    && booking.Status == BookingStatus.PendingVerification,
                cancellationToken)
            : 0;

        // What is still to be sent, not what was once owed: a booking the venue has paid back is
        // finished with, and a number nobody can clear is only a reproach (PRD 6.2, US-18).
        var money = membership.Allows(Who(VenueNotice.RefundOwed).Permission)
            // The same rule as Refunds.StillStanding, written out: EF cannot translate a call
            // that builds a query when it sits inside a correlated subquery.
            ? await database.Bookings.CountAsync(
                booking => booking.VenueId == venueId
                    && (booking.RefundDueBaht > database.RefundRecords
                            .Where(record =>
                                record.BookingId == booking.Id && record.VoidedAt == null)
                            .Sum(record => record.AmountBaht)
                        || booking.PaymentState == PaymentState.Unconfirmed),
                cancellationToken)
            : 0;

        return new VenueAttentionResponse(slips, money);
    }

    /// <summary>
    /// Whether a booking's money is waiting for somebody, and for what. The same test the count
    /// above makes, so the number and the message cannot drift apart (PRD 6.2).
    /// </summary>
    private static VenueNotice? Waiting(decimal refundDue, PaymentState payment) =>
        payment == PaymentState.Unconfirmed ? VenueNotice.PaymentUnanswered
        : refundDue > 0 ? VenueNotice.RefundOwed
        : null;

    /// <summary>
    /// Who a notice is for, and whether they may ask not to hear it. Only the slip may be turned
    /// off: it is the one that arrives on an ordinary day, where the others mean somebody is
    /// waiting for money (PRD US-17).
    /// </summary>
    private static (VenuePermissions Permission, bool CanBeSilenced, bool OwnerOnly) Who(
        VenueNotice notice) =>
        notice switch
        {
            VenueNotice.SlipWaiting or VenueNotice.SlipSeenBefore =>
                (VenuePermissions.VerifySlip, true, false),
            VenueNotice.SlipStillWaiting => (VenuePermissions.VerifySlip, false, true),
            _ => (VenuePermissions.ManageBookings, false, false),
        };

    /// <summary>
    /// Tells the people who may act on this at this venue. The owner holds every permission and
    /// so is always among them (PRD 8) — including the person who has just pressed something: the
    /// money still has to be sent, so their own press did not finish the work.
    ///
    /// A venue that is not approved is told nothing. It cannot act on any of this, and a message
    /// about work it is barred from doing is only noise.
    /// </summary>
    private async Task TellAsync(VenueNotice notice, Guid venueId, Guid bookingId)
    {
        var (permission, canBeSilenced, ownerOnly) = Who(notice);

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
            .ToListAsync(CancellationToken.None);

        var told = members
            .Where(member => !ownerOnly || member.Member.Role == VenueRole.Owner)
            .Where(member => member.Member.Allows(permission))
            .Where(member => !string.IsNullOrEmpty(member.Address))
            .ToList();

        var link = VenueLetters.LinkFor(notice, options.Value.BaseUrl, venueId);

        foreach (var member in told)
        {
            // Each reader in their own language: one venue's staff need not share one (US-23).
            var (subject, body) = VenueLetters.Notice(notice, bookingId, link, member.Language);

            try
            {
                await emails.SendAsync(
                    new EmailMessage(
                        member.Address!, member.Language, subject, body, VenueLetters.TemplateOf(notice)),
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
}
