using System.Security.Claims;
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
    public static void MapCourtEndpoints(this RouteGroupBuilder venues)
    {
        var venue = venues.MapGroup("/{venueId:guid}");

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

    private static async Task<Ok<CourtResponse[]>> ListCourtsAsync(
        Guid venueId,
        AppDbContext database,
        CancellationToken cancellationToken)
    {
        var courts = await database.Courts
            .AsNoTracking()
            .Where(court => court.VenueId == venueId)
            .OrderBy(court => court.Position)
            .ThenBy(court => court.Name)
            .ToListAsync(cancellationToken);

        return TypedResults.Ok(courts.Select(ToResponse).ToArray());
    }

    private static async Task<Results<Created<CourtResponse>, ProblemHttpResult>> AddCourtAsync(
        Guid venueId,
        CreateCourtRequest request,
        ClaimsPrincipal principal,
        AppDbContext database,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var invalid = CourtValidation.ValidateName(request.Name);
        if (invalid is not null)
        {
            return ApiProblem.Of(StatusCodes.Status400BadRequest, invalid);
        }

        var now = timeProvider.GetUtcNow();
        // New courts land at the end of the grid, where a venue expects to find the one just added.
        var lastPosition = await database.Courts
            .Where(court => court.VenueId == venueId)
            .MaxAsync(court => (int?)court.Position, cancellationToken) ?? -1;

        var court = new Court
        {
            VenueId = venueId,
            Name = request.Name.Trim(),
            Position = lastPosition + 1,
            IsActive = true,
            CreatedAt = now,
        };

        database.Courts.Add(court);
        database.CourtStatusChanges.Add(new CourtStatusChange
        {
            CourtId = court.Id,
            Active = true,
            EffectiveFrom = PlatformRequirements.BangkokToday(timeProvider),
            ChangedByUserId = UserId(principal),
            ChangedAt = now,
        });

        if (!await SaveAsync(database, cancellationToken))
        {
            return ApiProblem.Of(StatusCodes.Status409Conflict, CourtErrorCodes.NameAlreadyUsed);
        }

        return TypedResults.Created($"/api/venues/{venueId}/courts/{court.Id}", ToResponse(court));
    }

    private static async Task<Results<Ok<CourtResponse>, NotFound, ProblemHttpResult>> UpdateCourtAsync(
        Guid venueId,
        Guid courtId,
        UpdateCourtRequest request,
        AppDbContext database,
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

        return TypedResults.Ok(ToResponse(court));
    }

    /// <summary>
    /// Takes a court out of use or puts it back, from today. The date is recorded, so a report about
    /// a past month still knows how many courts the venue had then (PRD US-11, US-15).
    /// </summary>
    private static async Task<Results<Ok<CourtResponse>, NotFound>> ChangeCourtStatusAsync(
        Guid venueId,
        Guid courtId,
        ChangeCourtStatusRequest request,
        ClaimsPrincipal principal,
        AppDbContext database,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var court = await FindCourtAsync(database, venueId, courtId, cancellationToken);
        if (court is null)
        {
            return TypedResults.NotFound();
        }

        if (court.IsActive == request.Active)
        {
            // Nothing changed, so the history gains nothing either.
            return TypedResults.Ok(ToResponse(court));
        }

        court.IsActive = request.Active;
        database.CourtStatusChanges.Add(new CourtStatusChange
        {
            CourtId = court.Id,
            Active = request.Active,
            EffectiveFrom = PlatformRequirements.BangkokToday(timeProvider),
            ChangedByUserId = UserId(principal),
            ChangedAt = timeProvider.GetUtcNow(),
        });

        await database.SaveChangesAsync(cancellationToken);
        return TypedResults.Ok(ToResponse(court));
    }

    private static async Task<Results<Ok<CourtStatusChangeResponse[]>, NotFound>> CourtHistoryAsync(
        Guid venueId,
        Guid courtId,
        AppDbContext database,
        CancellationToken cancellationToken)
    {
        if (!await database.Courts.AnyAsync(
                court => court.Id == courtId && court.VenueId == venueId, cancellationToken))
        {
            return TypedResults.NotFound();
        }

        var history = await database.CourtStatusChanges
            .AsNoTracking()
            .Where(change => change.CourtId == courtId)
            .OrderByDescending(change => change.EffectiveFrom)
            .ThenByDescending(change => change.ChangedAt)
            .Select(change => new CourtStatusChangeResponse(change.Active, change.EffectiveFrom, change.ChangedAt))
            .ToListAsync(cancellationToken);

        return TypedResults.Ok(history.ToArray());
    }

    /// <summary>The week in force today, plus any version dated ahead of it.</summary>
    private static async Task<Ok<OpeningHoursResponse[]>> ListOpeningHoursAsync(
        Guid venueId,
        AppDbContext database,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var today = PlatformRequirements.BangkokToday(timeProvider);
        var schedules = await database.OpeningHoursSchedules
            .AsNoTracking()
            .Where(schedule => schedule.VenueId == venueId)
            .Include(schedule => schedule.Days)
            .OrderBy(schedule => schedule.EffectiveFrom)
            .ToListAsync(cancellationToken);

        var inForce = schedules.LastOrDefault(schedule => schedule.EffectiveFrom <= today);
        var current = schedules
            .Where(schedule => schedule == inForce || schedule.EffectiveFrom > today)
            .Select(schedule => ToResponse(schedule, schedule == inForce))
            .ToArray();

        return TypedResults.Ok(current);
    }

    /// <summary>
    /// Publishes a week from a date. An existing version for that same date is replaced, so an owner
    /// correcting a mistake before it starts does not leave two versions behind (PRD US-11).
    /// </summary>
    private static async Task<Results<Ok<OpeningHoursResponse>, ProblemHttpResult>> SetOpeningHoursAsync(
        Guid venueId,
        SetOpeningHoursRequest request,
        ClaimsPrincipal principal,
        AppDbContext database,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var invalid = CourtValidation.TryReadWeek(request.Days, out var week);
        if (invalid is not null)
        {
            return ApiProblem.Of(StatusCodes.Status400BadRequest, invalid);
        }

        var today = PlatformRequirements.BangkokToday(timeProvider);
        if (request.EffectiveFrom < today)
        {
            return ApiProblem.Of(StatusCodes.Status400BadRequest, CourtErrorCodes.EffectiveDateInThePast);
        }

        await database.OpeningHoursSchedules
            .Where(schedule => schedule.VenueId == venueId && schedule.EffectiveFrom == request.EffectiveFrom)
            .ExecuteDeleteAsync(cancellationToken);

        var created = new OpeningHoursSchedule
        {
            VenueId = venueId,
            EffectiveFrom = request.EffectiveFrom,
            CreatedByUserId = UserId(principal),
            CreatedAt = timeProvider.GetUtcNow(),
        };
        created.Days.AddRange(week.Select(day => new OpeningHoursDay
        {
            ScheduleId = created.Id,
            Day = day.Day,
            OpensHour = day.OpensHour,
            ClosesHour = day.ClosesHour,
        }));

        database.OpeningHoursSchedules.Add(created);
        await database.SaveChangesAsync(cancellationToken);

        return TypedResults.Ok(ToResponse(created, created.EffectiveFrom <= today));
    }

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

    private static Guid UserId(ClaimsPrincipal principal) =>
        Guid.Parse(principal.FindFirstValue(ClaimTypes.NameIdentifier)!);

    private static CourtResponse ToResponse(Court court) =>
        new(court.Id, court.Name, court.Position, court.IsActive);

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
