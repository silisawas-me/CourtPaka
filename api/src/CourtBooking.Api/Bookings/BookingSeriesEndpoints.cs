using CourtBooking.Api.Data;
using CourtBooking.Api.Http;
using CourtBooking.Api.Localization;
using CourtBooking.Api.Observability;
using CourtBooking.Api.Venues;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace CourtBooking.Api.Bookings;

/// <summary>
/// Standing arrangements: the group that comes every Tuesday at seven (PRD US-30).
///
/// The venue writes one down once, and the job fills the weeks in as the booking window rolls
/// (<see cref="Jobs.SeriesBookings"/>). Nothing here books anything itself — an arrangement is a
/// statement of intent, and what it is worth depends on what the floor looks like each week, which
/// is not knowable today.
///
/// Every door is behind <see cref="VenuePermissions.ManageBookings"/>, including the reading of
/// them: an arrangement carries the group's name and phone number, which PRD 8 gives to the people
/// holding that permission rather than to the venue's members at large (PDPA, as US-13).
/// </summary>
public static class BookingSeriesEndpoints
{
    public static void MapBookingSeriesEndpoints(this RouteGroupBuilder venue)
    {
        var series = venue.MapGroup("/series");

        // Reading what is standing, and stopping something that is, stay open to a venue the
        // platform has suspended: an arrangement the venue can no longer honour is one it must be
        // able to stop, and the weeks already sold have to be answered for either way (PRD US-20).
        series.RequireAuthorization(
            VenuePolicies.NeedsEvenWhenSuspended(VenuePermissions.ManageBookings));

        series.MapGet("/", ListAsync);
        series.MapPost("/{seriesId:guid}/stop", StopAsync);

        // Agreeing an arrangement, and changing one from a date, are both selling — which is the
        // thing a suspension stops (PRD US-20). Declared here, where this repo decides which side
        // of a suspension a door is on.
        series.MapPost("/", AgreeAsync)
            .RequireAuthorization(VenuePolicies.Needs(VenuePermissions.ManageBookings));
        series.MapPost("/{seriesId:guid}/change", ChangeAsync)
            .RequireAuthorization(VenuePolicies.Needs(VenuePermissions.ManageBookings));
    }

    /// <summary>
    /// What this venue has standing, with the weeks each one has made and the weeks it could not.
    /// The misses are the point of the list: they are what somebody has to ring the group about
    /// (PRD US-30).
    ///
    /// Everything still running, and only the last few that stopped. Arrangements are never
    /// deleted, so a venue that has been open a year has a drawer of them, and a drawer tipped out
    /// under the list somebody is working from buries the list.
    /// </summary>
    private static async Task<Ok<BookingSeriesResponse[]>> ListAsync(
        Guid venueId,
        AppDbContext database,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var today = PlatformRequirements.BangkokToday(timeProvider);

        var running = await RowsAsync(
            database.BookingSeries
                .Where(one => one.VenueId == venueId && one.State == SeriesState.Running)
                .OrderByDescending(one => one.CreatedAt),
            today,
            database,
            cancellationToken);

        var ended = await RowsAsync(
            database.BookingSeries
                .Where(one => one.VenueId == venueId && one.State == SeriesState.Ended)
                .OrderByDescending(one => one.EndedAt)
                .ThenByDescending(one => one.CreatedAt)
                .Take(Series.EndedShown),
            today,
            database,
            cancellationToken);

        return TypedResults.Ok<BookingSeriesResponse[]>([.. running, .. ended]);
    }

    /// <summary>The arrangements a query picked out, drawn the way the venue reads them.</summary>
    private static async Task<BookingSeriesResponse[]> RowsAsync(
        IQueryable<BookingSeries> asked,
        DateOnly today,
        AppDbContext database,
        CancellationToken cancellationToken)
    {
        var standing = await asked
            .AsNoTracking()
            .Select(one => new
            {
                Series = one,
                CourtName = one.Court!.Name,
                // The weeks it has that are still on. Counting the cancelled ones too would
                // leave the number unmoved by the stop that just cancelled them, which reads as
                // a stop that did not work.
                Booked = database.Bookings.Count(booking =>
                    booking.SeriesId == one.Id
                    && booking.Status != BookingStatus.Cancelled
                    && booking.Status != BookingStatus.Rejected
                    && booking.Status != BookingStatus.Expired),

                // Only the weeks still to come are worth showing as missed: a week the venue has
                // already lived through is not something they can do anything about now.
                Missed = database.SeriesMisses
                    .Where(miss => miss.SeriesId == one.Id && miss.Date >= today)
                    .OrderBy(miss => miss.Date)
                    .Select(miss => new SeriesMissResponse(miss.Date, miss.Refusal))
                    .ToArray(),
            })
            .ToListAsync(cancellationToken);

        return [.. standing
            .Select(one => new BookingSeriesResponse(
                one.Series.Id,
                one.Series.CourtId,
                one.CourtName,
                one.Series.Day.ToString(),
                one.Series.FromHour,
                one.Series.UntilHour,
                one.Series.CustomerName,
                one.Series.CustomerPhone,
                one.Series.StartsOn,
                one.Series.UntilOn,
                one.Series.State.ToString(),
                one.Series.EndedAt,
                one.Series.EndReason,
                one.Booked,
                one.Missed))];
    }

    /// <summary>
    /// Writes down an arrangement (PRD US-30). It books nothing here: the first week may be a
    /// month away, and the price of it is the price on the day it is made (BR-05). The job makes
    /// whatever weeks are already inside the booking window on its next sweep, within a minute.
    /// </summary>
    private static async Task<Results<Created<BookingSeriesResponse>, ProblemHttpResult>> AgreeAsync(
        Guid venueId,
        BookingSeriesRequest request,
        CurrentVenue venue,
        AppDbContext database,
        TimeProvider timeProvider,
        ILoggerFactory loggers,
        CancellationToken cancellationToken)
    {
        var membership = venue.Require();

        // A venue still waiting to be approved is not frozen, but it is not on the platform
        // either, so it cannot agree hours it could not sell (PRD US-10, US-20).
        if (venue.Status != VenueStatus.Approved)
        {
            return ApiProblem.Of(StatusCodes.Status403Forbidden, VenueErrorCodes.NotApproved);
        }

        var (asked, wrong) = await ReadAsync(
            request, venueId, database, timeProvider, cancellationToken);
        if (asked is not { } agreed)
        {
            return Refused(wrong);
        }

        var now = timeProvider.GetUtcNow();
        var series = Written(venueId, agreed, membership.UserId, now);

        database.BookingSeries.Add(series);
        await database.SaveChangesAsync(cancellationToken);

        AppEvents.For(loggers).LogInformation(
            "series_agreed {SeriesId} {VenueId} {Day} {FromHour} {Hours}",
            series.Id,
            venueId,
            series.Day,
            series.FromHour,
            series.Hours);

        return TypedResults.Created(
            $"/api/venues/{venueId}/series",
            await OneAsync(database, venueId, series.Id, timeProvider, cancellationToken));
    }

    /// <summary>
    /// Stops an arrangement and cancels the weeks of it still to come, one at a time and each on
    /// its own terms (PRD US-30). The terms are each booking's own snapshot (BR-05), so a group
    /// whose Tuesday is tomorrow and whose Tuesday is in a month are not owed the same thing —
    /// which is exactly why this comes back through the ordinary cancelling door rather than
    /// writing the bookings off in one statement.
    /// </summary>
    private static async Task<Results<Ok<BookingSeriesStoppedResponse>, ProblemHttpResult>> StopAsync(
        Guid venueId,
        Guid seriesId,
        BookingSeriesStopRequest request,
        CurrentVenue venue,
        VenueNotifications notifications,
        AppDbContext database,
        TimeProvider timeProvider,
        ILoggerFactory loggers,
        CancellationToken cancellationToken)
    {
        var membership = venue.Require();
        var now = timeProvider.GetUtcNow();

        if (BookingStatusChange.Recorded(request.Note) is not { } reason)
        {
            // The same column and the same refusal the slip queue gives, so the same code.
            return ApiProblem.Of(StatusCodes.Status400BadRequest, SlipErrorCodes.ReasonTooLong);
        }

        // Ended before its weeks are touched: what cancels them is the arrangement being over.
        if (await EndRunningAsync(
                venueId, seriesId, membership.UserId, reason, now, database, cancellationToken)
            is { } refused)
        {
            return Refused(refused);
        }

        AppEvents.For(loggers).LogInformation(
            "series_stopped {SeriesId} {VenueId}", seriesId, venueId);

        var cancelled = await CancelWeeksAheadAsync(
            venueId,
            seriesId,
            membership.UserId,
            now,
            notifications,
            database,
            loggers,
            cancellationToken);

        return TypedResults.Ok(new BookingSeriesStoppedResponse(
            await OneAsync(database, venueId, seriesId, timeProvider, cancellationToken),
            cancelled.Cancelled,
            cancelled.Left));
    }

    /// <summary>
    /// Changes an arrangement from a date: the old one ends and a new one takes over from it
    /// (PRD US-30). Two rows rather than one edited in place, because the weeks already played
    /// were played on the old terms and a row rewritten is a row that no longer says so.
    /// </summary>
    private static async Task<Results<Ok<BookingSeriesStoppedResponse>, ProblemHttpResult>>
        ChangeAsync(
            Guid venueId,
            Guid seriesId,
            BookingSeriesRequest request,
            CurrentVenue venue,
            VenueNotifications notifications,
            AppDbContext database,
            TimeProvider timeProvider,
            ILoggerFactory loggers,
            CancellationToken cancellationToken)
    {
        var membership = venue.Require();

        if (venue.Status != VenueStatus.Approved)
        {
            return ApiProblem.Of(StatusCodes.Status403Forbidden, VenueErrorCodes.NotApproved);
        }

        var (asked, wrong) = await ReadAsync(
            request, venueId, database, timeProvider, cancellationToken, except: seriesId);
        if (asked is not { } agreed)
        {
            return Refused(wrong);
        }

        // The old arrangement stops the moment this is agreed, so a handover further ahead than
        // the weeks can be booked would leave the weeks in between with nobody making them.
        if (agreed.StartsOn > PlatformRequirements.BangkokToday(timeProvider)
                .AddDays(Series.FillWithinDays))
        {
            return Refused(SeriesErrorCodes.ChangeTooFarAhead);
        }

        var now = timeProvider.GetUtcNow();

        // No reason of its own: what happened to the old arrangement is that this one took over,
        // and the row that says so is the new one's ReplacedSeriesId.
        if (await EndRunningAsync(
                venueId, seriesId, membership.UserId, null, now, database, cancellationToken)
            is { } refused)
        {
            return Refused(refused);
        }

        // The old arrangement's weeks go back before the new one is written down: if the change
        // keeps the same court and hours, the weeks the old one holds are exactly the weeks the
        // new one is about to ask for, and an hour it finds taken is a week written off for good.
        var cancelled = await CancelWeeksAheadAsync(
            venueId,
            seriesId,
            membership.UserId,
            now,
            notifications,
            database,
            loggers,
            cancellationToken,
            from: agreed.StartsOn);

        var taking = Written(venueId, agreed, membership.UserId, now, replacing: seriesId);

        database.BookingSeries.Add(taking);
        await database.SaveChangesAsync(cancellationToken);

        AppEvents.For(loggers).LogInformation(
            "series_changed {SeriesId} {VenueId} {Replaced}", taking.Id, venueId, seriesId);

        return TypedResults.Ok(new BookingSeriesStoppedResponse(
            await OneAsync(database, venueId, taking.Id, timeProvider, cancellationToken),
            cancelled.Cancelled,
            cancelled.Left));
    }

    /// <summary>
    /// The weeks of an arrangement that have not been played yet, cancelled one at a time through
    /// the door every other cancellation goes through, so each one's money is worked out under its
    /// own snapshot (PRD BR-05, US-13). Answers how many went and how many would not.
    ///
    /// A week that refuses is left alone rather than retried: the reasons it can refuse — somebody
    /// took the money for it a moment ago, the hours are already being played — are all reasons for
    /// a person to look at it, and the row is still in the day's list where they will.
    /// </summary>
    private static async Task<(int Cancelled, int Left)> CancelWeeksAheadAsync(
        Guid venueId,
        Guid seriesId,
        Guid byUserId,
        DateTimeOffset now,
        VenueNotifications notifications,
        AppDbContext database,
        ILoggerFactory loggers,
        CancellationToken cancellationToken,
        DateOnly? from = null)
    {
        // Weeks that have not begun. A week played earlier today is behind the date a change
            // starts from, but it is not a week still to come — and asking to cancel it would be
            // refused as a correction, which is not what the venue asked for.
        var asked = from is { } start ? PlatformRequirements.BangkokHour(start, 0) : now;
        var after = asked > now ? asked : now;

        var ahead = await database.Bookings
            .AsNoTracking()
            .Where(booking =>
                booking.SeriesId == seriesId
                && booking.VenueId == venueId
                && (booking.Status == BookingStatus.Confirmed
                    || booking.Status == BookingStatus.PendingVerification)
                && booking.Slots.Any(slot => slot.StartsAt >= after))
            .OrderBy(booking => booking.CreatedAt)
            .Select(booking => booking.Id)
            .ToListAsync(cancellationToken);

        var cancelled = 0;

        foreach (var bookingId in ahead)
        {
            var refused = await VenueBookingEndpoints.CloseAsync(
                venueId,
                bookingId,
                BookingStatus.Cancelled,
                (booking, status, taken, at) => VenueDecisions.CancelOffer(
                    booking,
                    status,
                    // The venue is the one standing the group down, whatever the conversation
                    // behind it was: it is the venue's arrangement that ended (PRD 6.1).
                    CancellationReason.VenueInitiated,
                    null,
                    false,
                    taken,
                    at),
                null,
                CancellationReason.VenueInitiated,
                VenueBookingEndpoints.Hours.ReleaseAll,
                byUserId,
                now,
                notifications,
                database,
                loggers,
                CancellationToken.None);

            if (refused is null)
            {
                cancelled++;
            }
        }

        return (cancelled, ahead.Count - cancelled);
    }

    /// <summary>
    /// The request as something that can be written down, or null. Everything it checks is a
    /// property of the arrangement itself; whether the hours are free is the floor's answer, and
    /// it is a different one every week.
    /// </summary>
    private static async Task<(Asked? Asked, string? Refusal)> ReadAsync(
        BookingSeriesRequest request,
        Guid venueId,
        AppDbContext database,
        TimeProvider timeProvider,
        CancellationToken cancellationToken,
        Guid? except = null)
    {
        if (await RefusalOf(request, venueId, database, timeProvider, cancellationToken, except)
            is { } refused)
        {
            return (null, refused);
        }

        return (
            new Asked(
                request.CourtId!.Value,
                Enum.Parse<DayOfWeek>(request.Day!),
                request.FromHour!.Value,
                request.UntilHour!.Value,
                request.CustomerName!.Trim(),
                string.IsNullOrWhiteSpace(request.CustomerPhone)
                    ? null
                    : request.CustomerPhone.Trim(),
                request.StartsOn!.Value,
                request.UntilOn),
            null);
    }

    /// <summary>Why the request cannot be written down, or null.</summary>
    private static async Task<string?> RefusalOf(
        BookingSeriesRequest request,
        Guid venueId,
        AppDbContext database,
        TimeProvider timeProvider,
        CancellationToken cancellationToken,
        Guid? except)
    {
        var name = request.CustomerName?.Trim();
        if (string.IsNullOrEmpty(name) || name.Length > Booking.CustomerNameMaxLength)
        {
            return BookingErrorCodes.InvalidCustomerName;
        }

        var phone = request.CustomerPhone?.Trim();
        if (!string.IsNullOrEmpty(phone)
            && (phone.Length > Booking.CustomerPhoneMaxLength
                || !phone.All(letter => char.IsAsciiDigit(letter) || letter is '+' or '-' or ' ')))
        {
            return BookingErrorCodes.InvalidCustomerPhone;
        }

        // The names and nothing else: Enum.TryParse takes "1", and it ors two names together.
        if (request.Day is not { } day
            || !Enum.GetNames<DayOfWeek>().Contains(day, StringComparer.Ordinal))
        {
            return SeriesErrorCodes.DayUnknown;
        }

        if (request.FromHour is not { } fromHour
            || request.UntilHour is not { } untilHour
            || request.StartsOn is not { } startsOn)
        {
            return SeriesErrorCodes.NotAWindow;
        }

        if (Series.Refusal(
                fromHour,
                untilHour,
                startsOn,
                request.UntilOn,
                PlatformRequirements.BangkokToday(timeProvider))
            is { } wrong)
        {
            return wrong;
        }

        if (request.CourtId is not { } courtId
            || !await database.Courts.AnyAsync(
                one => one.Id == courtId && one.VenueId == venueId, cancellationToken))
        {
            return SeriesErrorCodes.CourtUnknown;
        }

        // A second group on the same court, the same day, the same hours could never have a
        // single week of it — every one would find the first group already there, and a week
        // written off is written off for good. Refused here as well as by the index, so the
        // answer is a code the screen can say rather than a constraint violation.
        var already = await database.BookingSeries.AnyAsync(
            one => one.VenueId == venueId
                && one.State == SeriesState.Running
                && one.CourtId == courtId
                && one.Day == Enum.Parse<DayOfWeek>(day)
                && one.FromHour == fromHour
                && one.UntilHour == untilHour
                && one.Id != except,
            cancellationToken);

        return already ? SeriesErrorCodes.AlreadyStanding : null;
    }

    /// <summary>One arrangement drawn the way the list draws it, so the screen replaces a row.</summary>
    private static async Task<BookingSeriesResponse> OneAsync(
        AppDbContext database,
        Guid venueId,
        Guid seriesId,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var drawn = await RowsAsync(
            database.BookingSeries.Where(one => one.Id == seriesId && one.VenueId == venueId),
            PlatformRequirements.BangkokToday(timeProvider),
            database,
            cancellationToken);

        return drawn.Single();
    }

    /// <summary>
    /// What an arrangement is made of, in one place: the two doors that write one down say the
    /// same thing, and the second differs only in having something to say about what it replaces.
    /// </summary>
    private static BookingSeries Written(
        Guid venueId,
        Asked agreed,
        Guid byUserId,
        DateTimeOffset now,
        Guid? replacing = null) =>
        new()
        {
            VenueId = venueId,
            CourtId = agreed.CourtId,
            Day = agreed.Day,
            FromHour = agreed.FromHour,
            UntilHour = agreed.UntilHour,
            CustomerName = agreed.CustomerName,
            CustomerPhone = agreed.CustomerPhone,
            StartsOn = agreed.StartsOn,
            UntilOn = agreed.UntilOn,
            CreatedAt = now,
            CreatedByUserId = byUserId,
            ReplacedSeriesId = replacing,
        };

    /// <summary>
    /// Ends an arrangement that is running, or says why it could not. Conditional on its still
    /// running: two people stopping the same one at the same moment must not both go on to cancel
    /// its weeks, because the second would be deciding money the first has already decided.
    /// </summary>
    private static async Task<string?> EndRunningAsync(
        Guid venueId,
        Guid seriesId,
        Guid byUserId,
        string? reason,
        DateTimeOffset now,
        AppDbContext database,
        CancellationToken cancellationToken)
    {
        var ended = await database.BookingSeries
            .Where(one =>
                one.Id == seriesId
                && one.VenueId == venueId
                && one.State == SeriesState.Running)
            .ExecuteUpdateAsync(
                set => set
                    .SetProperty(one => one.State, SeriesState.Ended)
                    .SetProperty(one => one.EndedAt, now)
                    .SetProperty(one => one.EndedByUserId, byUserId)
                    .SetProperty(one => one.EndReason, string.IsNullOrEmpty(reason) ? null : reason),
                cancellationToken);

        if (ended > 0)
        {
            return null;
        }

        return await database.BookingSeries
            .AnyAsync(one => one.Id == seriesId && one.VenueId == venueId, cancellationToken)
            ? SeriesErrorCodes.AlreadyEnded
            : SeriesErrorCodes.NotFound;
    }

    /// <summary>
    /// Which answer belongs to which refusal: a court or an arrangement this venue does not have
    /// is not here at all, one that has already stopped is a thing that moved on, and the rest is
    /// the caller having asked for something that is not an arrangement.
    /// </summary>
    private static ProblemHttpResult Refused(string? code) => code switch
    {
        SeriesErrorCodes.CourtUnknown or SeriesErrorCodes.NotFound => ApiProblem.Of(
            StatusCodes.Status404NotFound, code),

        SeriesErrorCodes.AlreadyEnded or SeriesErrorCodes.AlreadyStanding =>
            ApiProblem.Of(StatusCodes.Status409Conflict, code),

        _ => ApiProblem.Of(
            StatusCodes.Status400BadRequest, code ?? SeriesErrorCodes.NotAWindow),
    };

    /// <summary>A request that has been read and found to be an arrangement.</summary>
    private readonly record struct Asked(
        Guid CourtId,
        DayOfWeek Day,
        int FromHour,
        int UntilHour,
        string CustomerName,
        string? CustomerPhone,
        DateOnly StartsOn,
        DateOnly? UntilOn);
}
