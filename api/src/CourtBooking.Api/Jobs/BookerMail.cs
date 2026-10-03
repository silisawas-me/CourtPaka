using CourtBooking.Api.Bookings;
using CourtBooking.Api.Data;
using CourtBooking.Api.Email;
using CourtBooking.Api.Identity;
using CourtBooking.Api.Observability;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace CourtBooking.Api.Jobs;

/// <summary>
/// Telling bookers what happened to their bookings (PRD US-06).
///
/// It reads what already happened instead of being called from where it happens. Every move a
/// booking makes is written to <see cref="BookingStatusChange"/>, whoever or whatever made it:
/// a booker, a venue, a hold running out in the middle of somebody else's write. Every refund
/// is a <see cref="RefundRecord"/>. So one reader of those two tables tells the booker about all
/// of them, and a way of moving a booking added later is told about without anybody remembering
/// to call anything. It also means the message goes out after the commit, never inside it, for
/// every path at once.
///
/// Each message is claimed before it is sent (<see cref="BookerNotice"/>, unique on what it is
/// about), so two instances, or two ticks, send it once.
///
/// Counter bookings are left out: the customer has no account and nothing to send to (US-06).
///
/// Which way it goes out is <see cref="BookerReach"/>'s answer, not this one's: somebody who
/// signed in with LINE hears on LINE, and may never have proved an email address at all, which
/// until there was a second way to reach them meant they heard nothing (PRD US-34).
/// </summary>
public sealed class BookerMail(
    AppDbContext database,
    ITransactionalEmailSender emails,
    ILineMessenger line,
    IOptions<AppOptions> options,
    ILoggerFactory loggers)
{
    /// <summary>
    /// How far back a move is still worth telling. It bounds the scan, and a message about
    /// something that happened days ago — because the mail was down — is worth less than the
    /// booker's own history page, which already says it.
    /// </summary>
    public static readonly TimeSpan LookBack = TimeSpan.FromDays(1);

    /// <summary>How long before the first hour a confirmed booker is reminded (PRD US-06, S-05).</summary>
    public static readonly TimeSpan RemindWithin = TimeSpan.FromHours(2);

    /// <summary>One message that is due: what kind, what it is about, and whose booking.</summary>
    private sealed record Due(BookerNoticeKind Kind, Guid SourceId, Guid BookingId, DateTimeOffset At);

    /// <summary>Finds, claims and sends everything due. Answers how many went out.</summary>
    public async Task<int> SendDueAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        // Off until there is a booker-side app again (docs/plan/owner-complete.md 3b): every
        // message links to a page that no longer exists. Nothing is claimed while it is off, so
        // turning it on sends what fell due within LookBack and no further back.
        if (!options.Value.TellBookers)
        {
            return 0;
        }

        var due = await DueAsync(now, cancellationToken);
        var sent = 0;

        // Oldest first, so a hold that was made and then ran out arrives in that order.
        foreach (var one in due.OrderBy(one => one.At))
        {
            if (!await ClaimAsync(one, now, cancellationToken))
            {
                continue;
            }

            if (await TellAsync(one))
            {
                sent++;
            }
        }

        return sent;
    }

    private async Task<List<Due>> DueAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        var since = now - LookBack;
        var notices = database.BookerNotices;

        // A row whose From and To are the same is not a move: it is the venue settling whether
        // the money arrived (BookingTransitions.Settled), and it is told as that. Read as a move,
        // it would tell the booker a second time that their booking was cancelled — by the venue.
        //
        // Only where the booking itself ended, though. The desk taking the balance of a booking
        // held on a deposit writes the same shape of row against a booking that is still standing
        // (PRD US-26, US-28), and there is nothing to tell somebody who just paid at the counter
        // in person — least of all that the booking they are about to play was cancelled.
        var moves = await database.BookingStatusChanges
            .AsNoTracking()
            .Where(change =>
                change.ChangedAt >= since
                && change.Booking!.Channel == BookingChannel.Online
                && ((change.To == BookingStatus.Held && change.From == null)
                    || change.To == BookingStatus.Confirmed
                    || change.To == BookingStatus.Rejected
                    || change.To == BookingStatus.Expired
                    || change.To == BookingStatus.Cancelled)
                && !notices.Any(notice => notice.SourceId == change.Id))
            .Select(change => new
            {
                change.Id,
                change.BookingId,
                change.To,
                Settled = change.From == change.To,
                Ended = change.Booking!.Status == BookingStatus.Cancelled
                    || change.Booking.Status == BookingStatus.Rejected,
                change.ChangedAt,
            })
            .ToListAsync(cancellationToken);

        // The desk's own settle rows are dropped rather than left to be told wrongly. Their
        // receipt row stays unwritten, which is right: nothing was sent, so nothing is recorded
        // as sent, and the row is dropped again on every sweep by the same rule.
        moves.RemoveAll(move => move.Settled && !move.Ended);

        var refunds = await database.RefundRecords
            .AsNoTracking()
            .Where(record =>
                record.RecordedAt >= since
                && record.Booking!.Channel == BookingChannel.Online
                && !notices.Any(notice => notice.SourceId == record.Id))
            .Select(record => new { record.Id, record.BookingId, record.RecordedAt })
            .ToListAsync(cancellationToken);

        // The first hour of a booking stored as Confirmed, starting within the two hours. A
        // booking that is already being played is past reminding about.
        //
        // Asked from the hours rather than from the bookings: a booking's stored status stays
        // Confirmed after it is played (Completed is read, not written — PRD 9.2), so asking the
        // bookings would walk every booking ever confirmed on every tick.
        var remindBy = now + RemindWithin;
        var reminders = await database.BookingSlots
            .AsNoTracking()
            .Where(slot =>
                slot.IsActive
                && slot.StartsAt > now
                && slot.StartsAt <= remindBy
                && slot.Booking!.Status == BookingStatus.Confirmed
                && slot.Booking.Channel == BookingChannel.Online
                && !slot.Booking.Slots.Any(earlier => earlier.StartsAt < slot.StartsAt)
                && !notices.Any(notice =>
                    notice.SourceId == slot.BookingId
                    && notice.Kind == BookerNoticeKind.AboutToPlay))
            .Select(slot => slot.BookingId)
            .Distinct()
            .ToListAsync(cancellationToken);

        // Hours that changed on a booking that is standing (PRD US-29). One letter for the whole
        // change, not one per hour: a move of three hours is one thing the venue did, and three
        // letters saying the same evening moved is three letters nobody finishes. They are
        // grouped by the instant they were written, which is what the doors write them with, and
        // the earliest row of a group is what the receipt is taken against — the same row every
        // sweep, so a group that is claimed stays claimed.
        var hourRows = await database.BookingSlotChanges
            .AsNoTracking()
            .Where(change =>
                change.ChangedAt >= since && change.Booking!.Channel == BookingChannel.Online)
            .Select(change => new { change.Id, change.BookingId, change.ChangedAt })
            .ToListAsync(cancellationToken);

        var hourChanges = hourRows
            .GroupBy(change => (change.BookingId, change.ChangedAt))
            .Select(group => group.OrderBy(change => change.Id).First())
            .ToList();

        var claimed = await database.BookerNotices
            .AsNoTracking()
            .Where(notice => notice.Kind == BookerNoticeKind.HoursChanged
                && hourChanges.Select(change => change.Id).Contains(notice.SourceId))
            .Select(notice => notice.SourceId)
            .ToListAsync(cancellationToken);

        hourChanges.RemoveAll(change => claimed.Contains(change.Id));

        // A hold the queue put aside is still a hold, but the booker did not ask for it a moment
        // ago — they asked days ago — so the letter has to say where it came from (PRD US-27).
        var fromQueue = await database.WaitlistEntries
            .AsNoTracking()
            .Where(entry => entry.OfferedBookingId != null
                && moves.Select(move => move.BookingId).Contains(entry.OfferedBookingId!.Value))
            .Select(entry => entry.OfferedBookingId!.Value)
            .ToListAsync(cancellationToken);

        return
        [
            .. moves.Select(move => new Due(
                move.Settled
                    ? BookerNoticeKind.PaymentSettled
                    : move.To == BookingStatus.Held && fromQueue.Contains(move.BookingId)
                        ? BookerNoticeKind.WaitlistOffer
                        : KindOf(move.To),
                move.Id,
                move.BookingId,
                move.ChangedAt)),
            .. refunds.Select(record => new Due(
                BookerNoticeKind.RefundRecorded, record.Id, record.BookingId, record.RecordedAt)),
            .. reminders.Select(bookingId => new Due(
                BookerNoticeKind.AboutToPlay, bookingId, bookingId, now)),
            .. hourChanges.Select(change => new Due(
                BookerNoticeKind.HoursChanged, change.Id, change.BookingId, change.ChangedAt)),
        ];
    }

    private static BookerNoticeKind KindOf(BookingStatus to) => to switch
    {
        BookingStatus.Held => BookerNoticeKind.Held,
        BookingStatus.Confirmed => BookerNoticeKind.Confirmed,
        BookingStatus.Rejected => BookerNoticeKind.Rejected,
        BookingStatus.Expired => BookerNoticeKind.Expired,
        BookingStatus.Cancelled => BookerNoticeKind.Cancelled,
        _ => throw new ArgumentOutOfRangeException(nameof(to), to, null),
    };

    /// <summary>
    /// Takes the message for this sender, or finds that somebody already has. The database
    /// decides, by the unique index, so there is no window between asking and taking.
    /// </summary>
    private async Task<bool> ClaimAsync(Due due, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var claimed = await database.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO "BookerNotices" ("Id", "BookingId", "Kind", "SourceId", "ClaimedAt")
            VALUES ({Guid.CreateVersion7()}, {due.BookingId}, {(int)due.Kind}, {due.SourceId}, {now})
            ON CONFLICT ("SourceId", "Kind") DO NOTHING
            """,
            cancellationToken);

        return claimed == 1;
    }

    /// <summary>
    /// Writes and sends one message. Takes no cancellation token: the claim is already made, and
    /// a shutdown halfway through must not be what decides whether the booker hears.
    /// </summary>
    private async Task<bool> TellAsync(Due due)
    {
        try
        {
            return await WriteAndSendAsync(due);
        }
        catch (Exception failure)
        {
            // Claimed and not sent. The booker's own history still says what happened, so this
            // is said out loud for somebody to follow up, and not tried again: trying again is
            // how the same message arrives twice. Caught around the reads as well as the send, so
            // one booking that cannot be read does not end the sweep for everybody after it.
            loggers.CreateLogger<BookerMail>().LogError(
                failure,
                "Could not tell the booker of {BookingId} about {Kind}.",
                due.BookingId, due.Kind);
            return false;
        }
    }

    private async Task<bool> WriteAndSendAsync(Due due)
    {
        var booking = await database.Bookings
            .AsNoTracking()
            .Where(one => one.Id == due.BookingId)
            .Select(one => new
            {
                one.BookerUserId,
                // Nobody to write to once the person has asked to be forgotten (PRD 8).
                // Only an address its owner has proved they read: a LINE account may carry one
                // LINE shared that nobody has opened a link from yet (PRD US-01).
                Address = one.Booker!.DeletedAt == null && one.Booker.EmailConfirmed
                    ? one.Booker.Email
                    : null,

                // Deleting an account takes its LINE login row with it, because a LINE id names a
                // person — so this is null for somebody who has gone, without asking again.
                LineUserId = database.UserLogins
                    .Where(login =>
                        login.UserId == one.BookerUserId
                        && login.LoginProvider == LineLoginEndpoints.Provider)
                    .Select(login => login.ProviderKey)
                    .FirstOrDefault(),
                one.Booker!.Language,
                VenueName = one.Venue!.Name,
                one.TotalBaht,
                one.RefundDueBaht,
                one.PaymentState,
                one.HoldExpiresAt,
                Slots = one.Slots
                    .OrderBy(slot => slot.StartsAt)
                    .Select(slot => new LetterSlot(slot.Court!.Name, slot.StartsAt))
                    .ToList(),
            })
            .SingleOrDefaultAsync(CancellationToken.None);

        if (booking is null)
        {
            return false;
        }

        // A booker who asked to be forgotten has nowhere left to be written to (PRD 8), and one
        // who never proved an address and never signed in with LINE has nowhere yet.
        var reach = BookerReach.For(booking.LineUserId, booking.Address, line.IsEnabled);
        if (reach.Channel == BookerChannel.None)
        {
            return false;
        }

        var letter = new BookerLetter(
            due.Kind,
            booking.VenueName,
            booking.Slots,
            booking.TotalBaht,
            $"{options.Value.BaseUrl.TrimEnd('/')}/bookings/{due.BookingId}",
            booking.HoldExpiresAt,
            RefundDueBaht: booking.RefundDueBaht,
            PaymentUnconfirmed: booking.PaymentState == PaymentState.Unconfirmed,
            PaymentReceived: booking.PaymentState == PaymentState.Received);

        if (due.Kind is BookerNoticeKind.Rejected or BookerNoticeKind.Cancelled)
        {
            var move = await database.BookingStatusChanges
                .AsNoTracking()
                .Where(change => change.Id == due.SourceId)
                .Select(change => new { change.Reason, change.Cause, change.ChangedByUserId })
                .SingleAsync(CancellationToken.None);

            letter = letter with
            {
                Reason = move.Reason,
                Cause = move.Cause,
                CancelledByBooker = move.ChangedByUserId == booking.BookerUserId,
            };
        }
        else if (due.Kind is BookerNoticeKind.RefundRecorded)
        {
            var record = await database.RefundRecords
                .AsNoTracking()
                .Where(one => one.Id == due.SourceId)
                .Select(one => new { one.AmountBaht, one.Method, one.RefundedOn, one.VoidedAt })
                .SingleAsync(CancellationToken.None);

            // Taken back before anybody was told: there is nothing to tell (PRD US-18).
            if (record.VoidedAt is not null)
            {
                return false;
            }

            var sentBack = await database.RefundRecords
                .Where(one => one.BookingId == due.BookingId)
                .StillStanding()
                .SumAsync(one => one.AmountBaht, CancellationToken.None);

            letter = letter with
            {
                RefundAmountBaht = record.AmountBaht,
                RefundMethod = record.Method,
                RefundedOn = record.RefundedOn,
                StillOwedBaht = Refunds.OutstandingOf(booking.RefundDueBaht, sentBack),
            };
        }

        var (subject, body) = BookerLetters.Write(letter, booking.Language);
        var template = $"booker.{due.Kind}";

        if (reach.Channel == BookerChannel.Line)
        {
            // The letter's own words, with what was its subject as the first line. One set of
            // sentences for both ways of sending, so neither can say something the other does not
            // (PRD US-23, US-34).
            var said = await line.SendAsync(
                new LineMessage(reach.Address!, booking.Language, $"{subject}\n\n{body}", template),
                CancellationToken.None);

            if (!said)
            {
                // Claimed and not said. Not tried again, and not quietly turned into an email
                // either: falling back would send to an address this person may never read, and
                // the two ways of hearing would stop being one message.
                loggers.CreateLogger<BookerMail>().LogWarning(
                    "LINE would not carry {Kind} for booking {BookingId}.", due.Kind, due.BookingId);

                return false;
            }
        }
        else
        {
            await emails.SendAsync(
                new EmailMessage(reach.Address!, booking.Language, subject, body, template),
                CancellationToken.None);
        }

        // Which way it went. Written after, because until now it had not gone anywhere — and
        // wrapped, because by this point it has. A failure here is a note not taken, not a
        // message not sent: letting it fall through would hand a delivered message to the catch
        // that logs "could not tell the booker", and the row would say it never went out.
        try
        {
            await database.BookerNotices
                .Where(one => one.SourceId == due.SourceId && one.Kind == due.Kind)
                .ExecuteUpdateAsync(
                    set => set.SetProperty(one => one.SentBy, reach.Channel),
                    CancellationToken.None);
        }
        catch (Exception failure)
        {
            loggers.CreateLogger<BookerMail>().LogWarning(
                failure,
                "Told the booker of {BookingId} about {Kind} by {Channel}, and could not write "
                    + "down which way it went.",
                due.BookingId, due.Kind, reach.Channel);
        }

        // Somebody has now been asked whether they are coming, which is what the counter reads
        // as Reminded (PRD US-24). Written after the message, because it is about a message that
        // went out; a second tick finds the arrival already moved and leaves it alone.
        if (due.Kind is BookerNoticeKind.AboutToPlay)
        {
            var reminded = await database.Bookings
                .Where(one =>
                    one.Id == due.BookingId && one.Arrival == BookingArrival.Unconfirmed)
                .ExecuteUpdateAsync(
                    set => set.SetProperty(one => one.Arrival, BookingArrival.Reminded),
                    CancellationToken.None);

            if (reminded == 1)
            {
                database.BookingArrivalChanges.Add(new BookingArrivalChange
                {
                    BookingId = due.BookingId,
                    From = BookingArrival.Unconfirmed,
                    To = BookingArrival.Reminded,
                    ChangedAt = DateTimeOffset.UtcNow,
                    // Nobody pressed anything: the reminder going out is the system's own doing.
                    ChangedByUserId = null,
                });
                await database.SaveChangesAsync(CancellationToken.None);
            }
        }

        // Which way it went as well as that it went, because whether anybody reads LINE is the
        // question this feature exists to answer (PRD 8.1, US-34).
        AppEvents.For(loggers).LogInformation(
            "booker_notified {BookingId} {Kind} {Channel}", due.BookingId, due.Kind, reach.Channel);

        return true;
    }
}
