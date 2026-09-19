using CourtBooking.Api.Data;
using CourtBooking.Api.Http;
using CourtBooking.Api.Localization;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace CourtBooking.Api.Venues;

/// <summary>
/// The venue's own setup: which courts it has and when it is open (PRD US-11). Reading is open to
/// any member, because staff screens need it; changing takes ManageSettings.
/// </summary>
public static class CourtEndpoints
{
    public static void MapCourtEndpoints(this RouteGroupBuilder venue)
    {
        venue.MapGet("/courts", ListCourtsAsync).RequireAuthorization(VenuePolicies.Member);
        venue.MapPost("/courts", AddCourtAsync).RequireAuthorization(VenuePolicies.Settings);
        venue.MapPut("/courts/{courtId:guid}", UpdateCourtAsync).RequireAuthorization(VenuePolicies.Settings);
        venue.MapPut("/courts/{courtId:guid}/status", ChangeCourtStatusAsync)
            .RequireAuthorization(VenuePolicies.Settings);
        venue.MapGet("/courts/{courtId:guid}/status-history", CourtHistoryAsync)
            .RequireAuthorization(VenuePolicies.Member);

        venue.MapGet("/opening-hours", ListOpeningHoursAsync).RequireAuthorization(VenuePolicies.Member);
        venue.MapPut("/opening-hours", SetOpeningHoursAsync).RequireAuthorization(VenuePolicies.Settings);
    }

    /// <summary>
    /// The courts as they stand on a date, today unless asked otherwise: the availability grid shows
    /// days ahead, and a court taken out of use from next week is still in use until then.
    /// </summary>
    private static async Task<Ok<CourtResponse[]>> ListCourtsAsync(
        Guid venueId,
        DateOnly? on,
        AppDbContext database,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var date = on ?? PlatformRequirements.BangkokToday(timeProvider);

        var courts = await database.Courts
            .AsNoTracking()
            .Where(court => court.VenueId == venueId)
            .OrderBy(court => court.Position)
            .ThenBy(court => court.Name)
            .ToListAsync(cancellationToken);

        var changes = await StatusChangesAsync(database, venueId, date, cancellationToken);

        return TypedResults.Ok(courts
            .Select(court => new CourtResponse(
                court.Id,
                court.Name,
                court.Position,
                VenueTimeline.CourtIsActiveOn(changes, court.Id, date)))
            .ToArray());
    }

    private static async Task<Results<Created<CourtResponse>, ProblemHttpResult>> AddCourtAsync(
        Guid venueId,
        CreateCourtRequest request,
        AppDbContext database,
        CurrentVenue currentVenue,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var invalid = CourtValidation.ValidateName(request.Name);
        if (invalid is not null)
        {
            return ApiProblem.Of(StatusCodes.Status400BadRequest, invalid);
        }

        // New courts land at the end of the grid, where a venue expects to find the one just added.
        var lastPosition = await database.Courts
            .Where(court => court.VenueId == venueId)
            .MaxAsync(court => (int?)court.Position, cancellationToken) ?? -1;

        var (court, status) = Court.Open(
            venueId,
            request.Name.Trim(),
            lastPosition + 1,
            currentVenue.Require().UserId,
            PlatformRequirements.BangkokToday(timeProvider),
            timeProvider.GetUtcNow());

        database.Courts.Add(court);
        database.CourtStatusChanges.Add(status);

        if (!await SaveAsync(database, cancellationToken))
        {
            return ApiProblem.Of(StatusCodes.Status409Conflict, CourtErrorCodes.NameAlreadyUsed);
        }

        return TypedResults.Created(
            $"/api/venues/{venueId}/courts/{court.Id}",
            new CourtResponse(court.Id, court.Name, court.Position, status.Active));
    }

    private static async Task<Results<Ok<CourtResponse>, NotFound, ProblemHttpResult>> UpdateCourtAsync(
        Guid venueId,
        Guid courtId,
        UpdateCourtRequest request,
        AppDbContext database,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var invalid = CourtValidation.ValidateName(request.Name) ?? CourtValidation.ValidatePosition(request.Position);
        if (invalid is not null)
        {
            return ApiProblem.Of(StatusCodes.Status400BadRequest, invalid);
        }

        var court = await FindCourtAsync(database, venueId, courtId, cancellationToken);
        if (court is null)
        {
            return TypedResults.NotFound();
        }

        court.Name = request.Name.Trim();
        court.Position = request.Position;

        if (!await SaveAsync(database, cancellationToken))
        {
            return ApiProblem.Of(StatusCodes.Status409Conflict, CourtErrorCodes.NameAlreadyUsed);
        }

        var today = PlatformRequirements.BangkokToday(timeProvider);
        return TypedResults.Ok(await ToResponseAsync(database, court, today, cancellationToken));
    }

    /// <summary>
    /// Takes a court out of use or puts it back, from today or a date ahead. The change is a row on
    /// the court's timeline rather than a flag, so a report about a past month still knows how many
    /// courts the venue had then (PRD US-11, US-15).
    /// </summary>
    private static async Task<Results<Ok<CourtStatusResponse>, NotFound, ProblemHttpResult>> ChangeCourtStatusAsync(
        Guid venueId,
        Guid courtId,
        ChangeCourtStatusRequest request,
        AppDbContext database,
        CurrentVenue currentVenue,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var today = PlatformRequirements.BangkokToday(timeProvider);
        var effectiveFrom = request.EffectiveFrom ?? today;

        var invalid = CourtValidation.ValidateEffectiveFrom(effectiveFrom, today);
        if (invalid is not null)
        {
            return ApiProblem.Of(StatusCodes.Status400BadRequest, invalid);
        }

        var court = await FindCourtAsync(database, venueId, courtId, cancellationToken);
        if (court is null)
        {
            return TypedResults.NotFound();
        }

        var upto = await StatusChangesAsync(database, venueId, effectiveFrom, cancellationToken);
        if (VenueTimeline.CourtIsActiveOn(upto, courtId, effectiveFrom) != request.Active)
        {
            database.CourtStatusChanges.Add(court.ChangeStatus(
                request.Active, effectiveFrom, currentVenue.Require().UserId, timeProvider.GetUtcNow()));
            await database.SaveChangesAsync(cancellationToken);
        }

        return TypedResults.Ok(await StatusResponseAsync(database, court, today, cancellationToken));
    }

    private static async Task<Results<Ok<CourtStatusChangeResponse[]>, NotFound>> CourtHistoryAsync(
        Guid venueId,
        Guid courtId,
        AppDbContext database,
        CancellationToken cancellationToken)
    {
        // A court always has at least the row written when it was added, so an empty history means
        // there is no such court at this venue.
        var history = await database.CourtStatusChanges
            .AsNoTracking()
            .Where(change => change.CourtId == courtId && change.Court!.VenueId == venueId)
            .OrderByDescending(change => change.EffectiveFrom)
            .ThenByDescending(change => change.ChangedAt)
            .Select(change => new CourtStatusChangeResponse(change.Active, change.EffectiveFrom, change.ChangedAt))
            .ToArrayAsync(cancellationToken);

        return history.Length == 0 ? TypedResults.NotFound() : TypedResults.Ok(history);
    }

    /// <summary>The week in force today, plus the newest version for each date ahead of it.</summary>
    private static async Task<Ok<OpeningHoursResponse[]>> ListOpeningHoursAsync(
        Guid venueId,
        AppDbContext database,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var today = PlatformRequirements.BangkokToday(timeProvider);
        var schedules = await SchedulesAsync(database, venueId, cancellationToken);
        var inForce = VenueTimeline.OpeningHoursOn(schedules, today);

        return TypedResults.Ok(VenueTimeline.CurrentAndUpcoming(schedules, today)
            .Select(schedule => ToResponse(schedule, schedule == inForce))
            .ToArray());
    }

    /// <summary>
    /// Publishes a week from a date. Versions are only added: correcting one before it starts leaves
    /// the old version in the database, where the report can still see what was in force when the
    /// venue sold its hours (PRD US-11).
    /// </summary>
    private static async Task<Results<Ok<OpeningHoursResponse>, ProblemHttpResult>> SetOpeningHoursAsync(
        Guid venueId,
        SetOpeningHoursRequest request,
        AppDbContext database,
        CurrentVenue currentVenue,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var today = PlatformRequirements.BangkokToday(timeProvider);
        var invalid = CourtValidation.TryReadWeek(request.Days, out var week)
                      ?? CourtValidation.ValidateEffectiveFrom(request.EffectiveFrom, today);
        if (invalid is not null)
        {
            return ApiProblem.Of(StatusCodes.Status400BadRequest, invalid);
        }

        var created = OpeningHoursSchedule.Create(
            venueId,
            request.EffectiveFrom,
            week,
            currentVenue.Require().UserId,
            timeProvider.GetUtcNow());

        // Opening an hour the venue has no price for would put it in the availability grid with
        // nothing to charge, so this rule is checked from both sides (PRD US-11).
        var unpriced = PricingValidation.PricedByExistingBands(
            created, await PricedHoursAsync(database, venueId, cancellationToken));
        if (unpriced is not null)
        {
            return ApiProblem.Of(StatusCodes.Status400BadRequest, unpriced);
        }

        database.OpeningHoursSchedules.Add(created);
        await database.SaveChangesAsync(cancellationToken);

        return TypedResults.Ok(ToResponse(created, created.EffectiveFrom <= today));
    }

    /// <summary>
    /// The status rows that can decide whether a court was in use on a date — everything up to it,
    /// because the newest one at or before the date is the one that counts.
    /// </summary>
    internal static Task<List<CourtStatusChange>> StatusChangesAsync(
        AppDbContext database,
        Guid venueId,
        DateOnly on,
        CancellationToken cancellationToken) =>
        database.CourtStatusChanges
            .AsNoTracking()
            .Where(change => change.Court!.VenueId == venueId && change.EffectiveFrom <= on)
            .ToListAsync(cancellationToken);

    /// <summary>
    /// The bands a booking made now would be charged, as the rules read them. It goes through the
    /// pricing endpoints' own resolver so both halves of the paired rule agree on which list is in
    /// force, down to the tie-break.
    /// </summary>
    private static async Task<List<BandHours>> PricedHoursAsync(
        AppDbContext database,
        Guid venueId,
        CancellationToken cancellationToken)
    {
        var prices = await PricingEndpoints.InForcePricesAsync(database, venueId, cancellationToken);

        return prices?.Bands
            .Select(band => new BandHours(band.Day, band.FromHour, band.ToHour, band.BahtPerHour))
            .ToList() ?? [];
    }

    /// <summary>Every version the venue has published, which is what an owner's page shows.</summary>
    internal static Task<List<OpeningHoursSchedule>> SchedulesAsync(
        AppDbContext database,
        Guid venueId,
        CancellationToken cancellationToken) =>
        database.OpeningHoursSchedules
            .AsNoTracking()
            .Where(schedule => schedule.VenueId == venueId)
            .Include(schedule => schedule.Days)
            .ToListAsync(cancellationToken);

    /// <summary>
    /// Only the week in force on a date. Versions are append-only, so a venue that republishes its
    /// hours accumulates them forever; asking for one keeps that history out of the answer. The
    /// order is the same tie-break <see cref="VenueTimeline.OpeningHoursOn"/> applies in memory.
    /// </summary>
    internal static Task<OpeningHoursSchedule?> ScheduleOnAsync(
        AppDbContext database,
        Guid venueId,
        DateOnly on,
        CancellationToken cancellationToken) =>
        database.OpeningHoursSchedules
            .AsNoTracking()
            .Where(schedule => schedule.VenueId == venueId && schedule.EffectiveFrom <= on)
            .OrderByDescending(schedule => schedule.EffectiveFrom)
            .ThenByDescending(schedule => schedule.CreatedAt)
            .Include(schedule => schedule.Days)
            .FirstOrDefaultAsync(cancellationToken);

    private static Task<Court?> FindCourtAsync(
        AppDbContext database,
        Guid venueId,
        Guid courtId,
        CancellationToken cancellationToken) =>
        database.Courts.SingleOrDefaultAsync(
            court => court.Id == courtId && court.VenueId == venueId, cancellationToken);

    /// <summary>
    /// Two courts may not share a name at one venue. The database decides, so simultaneous adds
    /// cannot both pass a check-then-write.
    /// </summary>
    private static async Task<bool> SaveAsync(AppDbContext database, CancellationToken cancellationToken)
    {
        try
        {
            await database.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateException exception) when (DbErrors.IsUniqueViolation(exception))
        {
            return false;
        }
    }

    /// <summary>Reads back the whole timeline, so the answer cannot disagree with what was stored.</summary>
    private static async Task<CourtStatusResponse> StatusResponseAsync(
        AppDbContext database,
        Court court,
        DateOnly today,
        CancellationToken cancellationToken)
    {
        var changes = await database.CourtStatusChanges
            .AsNoTracking()
            .Where(change => change.CourtId == court.Id)
            .ToListAsync(cancellationToken);

        var scheduled = changes
            .Where(change => change.EffectiveFrom > today)
            .OrderBy(change => change.EffectiveFrom)
            .ThenBy(change => change.ChangedAt)
            .Select(change => new CourtStatusChangeResponse(change.Active, change.EffectiveFrom, change.ChangedAt))
            .ToArray();

        return new CourtStatusResponse(
            court.Id, VenueTimeline.CourtIsActiveOn(changes, court.Id, today), scheduled);
    }

    private static async Task<CourtResponse> ToResponseAsync(
        AppDbContext database,
        Court court,
        DateOnly on,
        CancellationToken cancellationToken)
    {
        var changes = await StatusChangesAsync(database, court.VenueId, on, cancellationToken);
        return new CourtResponse(
            court.Id, court.Name, court.Position, VenueTimeline.CourtIsActiveOn(changes, court.Id, on));
    }

    private static OpeningHoursResponse ToResponse(OpeningHoursSchedule schedule, bool inForce) =>
        new(
            schedule.Id,
            schedule.EffectiveFrom,
            inForce,
            schedule.Days
                .OrderBy(day => day.Day)
                .Select(day => new OpeningHoursDayResponse(day.Day.ToString(), day.OpensHour, day.ClosesHour))
                .ToArray());
}
