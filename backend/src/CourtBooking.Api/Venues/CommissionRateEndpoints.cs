using System.Security.Claims;
using CourtBooking.Api.Data;
using CourtBooking.Api.Http;
using CourtBooking.Api.Identity;
using CourtBooking.Api.Localization;
using CourtBooking.Api.Observability;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace CourtBooking.Api.Venues;

/// <summary>
/// What the platform charges each venue (PRD US-21).
///
/// The platform's own, not the venue's: a venue cannot set what it is charged, and does not need
/// to be a member of anything to be charged it. So this sits with the rest of the admin screens
/// rather than under <c>/api/venues/{id}</c>, where every door asks about membership.
///
/// Rates are only ever added. Setting a new one does not replace the old one — the old one is
/// what every month already invoiced was charged at, and what a month not yet invoiced will be
/// charged at for the days before the new one starts.
/// </summary>
public static class CommissionRateEndpoints
{
    public static void MapCommissionRateEndpoints(this RouteGroupBuilder admin)
    {
        admin.MapGet("/{venueId:guid}/commission", RatesAsync);
        admin.MapPost("/{venueId:guid}/commission", SetAsync);
    }

    /// <summary>
    /// Every rate this venue has had, newest first, and which one is in force today. The history
    /// is the answer to "why was this month's invoice that much", so it is not hidden behind
    /// anything — the screen that sets a rate is the screen that has to show the last one.
    /// </summary>
    private static async Task<Results<Ok<CommissionRatesResponse>, NotFound>> RatesAsync(
        Guid venueId,
        AppDbContext database,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        if (!await database.Venues.AnyAsync(venue => venue.Id == venueId, cancellationToken))
        {
            return TypedResults.NotFound();
        }

        return TypedResults.Ok(
            await ReadAsync(venueId, database, timeProvider, cancellationToken));
    }

    /// <summary>Every rate this venue has had, and the one in force on the venue's today.</summary>
    private static async Task<CommissionRatesResponse> ReadAsync(
        Guid venueId,
        AppDbContext database,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var rates = await database.CommissionRates
            .AsNoTracking()
            .Where(rate => rate.VenueId == venueId)
            .Include(rate => rate.SetBy)
            .ToListAsync(cancellationToken);

        var today = PlatformRequirements.BangkokToday(timeProvider);

        return new CommissionRatesResponse(
            Commission.InForceOn(rates, today)?.Percent,
            [
                .. rates
                    .OrderByDescending(rate => rate.EffectiveFrom)
                    .ThenByDescending(rate => rate.SetAt)
                    .Select(rate => new CommissionRateResponse(
                        rate.Percent,
                        rate.EffectiveFrom,
                        rate.SetAt,
                        // Null once that account has been closed; the rate stands either way.
                        rate.SetBy != null && rate.SetBy.DeletedAt == null
                            ? rate.SetBy.Email
                            : null,
                        rate.Note)),
            ]);
    }

    /// <summary>
    /// Agrees a rate from a date. The date may be in the future — the platform tells a venue
    /// about a change before it starts — but not in the past: a day that has been played has
    /// already been charged at whatever was in force then, and moving that afterwards would
    /// change what a venue owes for a month it has already been told about (PRD BR-08).
    /// </summary>
    private static async Task<Results<Ok<CommissionRatesResponse>, NotFound, ProblemHttpResult>>
        SetAsync(
            Guid venueId,
            SetCommissionRateRequest request,
            ClaimsPrincipal principal,
            AppDbContext database,
            TimeProvider timeProvider,
            ILoggerFactory loggers,
            CancellationToken cancellationToken)
    {
        if (!Commission.IsARate(request.Percent))
        {
            return ApiProblem.Of(StatusCodes.Status400BadRequest, VenueErrorCodes.InvalidRate);
        }

        var today = PlatformRequirements.BangkokToday(timeProvider);
        if (request.EffectiveFrom < today)
        {
            return ApiProblem.Of(StatusCodes.Status400BadRequest, VenueErrorCodes.RateStartsInThePast);
        }

        var note = request.Note?.Trim();
        if (note is { Length: > CommissionRate.NoteMaxLength })
        {
            return ApiProblem.Of(StatusCodes.Status400BadRequest, VenueErrorCodes.InvalidRate);
        }

        if (!await database.Venues.AnyAsync(venue => venue.Id == venueId, cancellationToken))
        {
            return TypedResults.NotFound();
        }

        database.CommissionRates.Add(new CommissionRate
        {
            VenueId = venueId,
            Percent = request.Percent,
            EffectiveFrom = request.EffectiveFrom,
            SetByUserId = CallerId.Of(principal),
            SetAt = timeProvider.GetUtcNow(),
            Note = string.IsNullOrEmpty(note) ? null : note,
        });

        await database.SaveChangesAsync(cancellationToken);

        AppEvents.For(loggers).LogInformation(
            "commission_rate_set {VenueId} {Percent} {EffectiveFrom}",
            venueId, request.Percent, request.EffectiveFrom);

        // The whole history back, because the screen that just added a rate is the screen that
        // shows them all — and the one in force may not be the one just entered.
        return TypedResults.Ok(
            await ReadAsync(venueId, database, timeProvider, cancellationToken));
    }
}
