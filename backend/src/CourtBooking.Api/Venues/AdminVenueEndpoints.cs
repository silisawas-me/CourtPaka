using System.Security.Claims;
using CourtBooking.Api.Data;
using CourtBooking.Api.Http;
using CourtBooking.Api.Identity;
using CourtBooking.Api.Observability;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace CourtBooking.Api.Venues;

/// <summary>
/// The platform deciding which venues may trade on it (PRD US-20).
///
/// Everything here acts on a venue from outside it, so none of it goes through the per-venue
/// permission machinery: the caller is not a member of the venue they are judging, and must not
/// have to be. It is the only part of the system that works that way.
///
/// Every decision is written down before the venue is told, and the venue is told every time.
/// A venue that finds out it was suspended by trying to work is a venue that will never trust
/// the platform again.
/// </summary>
public static class AdminVenueEndpoints
{
    public static void MapAdminVenueEndpoints(this IEndpointRouteBuilder routes)
    {
        var admin = routes.MapGroup("/admin/venues")
            .WithTags("Platform")
            .RequireAuthorization(PlatformAdmins.PolicyName);

        admin.MapGet("/", ListAsync);
        admin.MapGet("/{venueId:guid}", OneAsync);
        admin.MapPost("/{venueId:guid}/approve", ApproveAsync);
        admin.MapPost("/{venueId:guid}/reject", RejectAsync);
        admin.MapPost("/{venueId:guid}/suspend", SuspendAsync);
        admin.MapPost("/{venueId:guid}/reinstate", ReinstateAsync);

        // What the platform charges this venue (PRD US-21). The platform's own decision, so it
        // lives with the platform's own screens rather than under the venue's doors.
        admin.MapCommissionRateEndpoints();

        // And what it has billed, which is a list of its own rather than a venue's (PRD US-21).
        routes.MapAdminInvoiceEndpoints();
    }

    /// <summary>
    /// The venues, newest application first, optionally only those in one state. Waiting ones
    /// are what this screen is opened for, so they are what it is easiest to ask for.
    /// </summary>
    private static async Task<Results<Ok<AdminVenueResponse[]>, ProblemHttpResult>> ListAsync(
        string? status,
        AppDbContext database,
        CancellationToken cancellationToken)
    {
        VenueStatus? wanted = null;
        if (!string.IsNullOrEmpty(status))
        {
            if (!Enum.TryParse<VenueStatus>(status, out var parsed)
                || !Enum.GetNames<VenueStatus>().Contains(status, StringComparer.Ordinal))
            {
                return ApiProblem.Of(
                    StatusCodes.Status400BadRequest, VenueErrorCodes.InvalidStatus);
            }

            wanted = parsed;
        }

        var venues = await Described(database)
            .Where(venue => wanted == null || venue.Status == wanted)
            .OrderByDescending(venue => venue.CreatedAt)
            .ToListAsync(cancellationToken);

        return TypedResults.Ok(venues.Select(Drawn).ToArray());
    }

    /// <summary>
    /// One venue in full, with everything it said about itself and everything the platform has
    /// decided about it. This is the screen a decision is actually made on, so the tax identity
    /// is here — there is no way to judge an application without reading it (PRD US-20).
    /// </summary>
    private static async Task<Results<Ok<AdminVenueDetailResponse>, NotFound>> OneAsync(
        Guid venueId,
        AppDbContext database,
        CancellationToken cancellationToken)
    {
        var venue = await Described(database)
            .SingleOrDefaultAsync(one => one.Id == venueId, cancellationToken);

        if (venue is null)
        {
            return TypedResults.NotFound();
        }

        var history = await database.VenueStatusChanges
            .AsNoTracking()
            .Where(change => change.VenueId == venueId)
            .OrderBy(change => change.ChangedAt)
            .ThenBy(change => change.Id)
            .Select(change => new VenueStatusChangeResponse(
                change.From == null ? null : change.From.ToString(),
                change.To.ToString(),
                change.ChangedAt,
                change.Reason))
            .ToListAsync(cancellationToken);

        return TypedResults.Ok(new AdminVenueDetailResponse(
            Drawn(venue),
            new VenueBusinessResponse(
                venue.Business.PromptPayId,
                venue.Business.PromptPayAccountName,
                venue.Business.IsVatRegistered,
                venue.Business.LegalName,
                venue.Business.TaxId,
                venue.Business.TaxBranch,
                venue.Business.BillingAddress,
                venue.Business.Latitude,
                venue.Business.Longitude),
            venue.AgreementVersion,
            venue.AgreementAcceptedAt,
            [.. history]));
    }

    private static Task<Results<Ok<AdminVenueResponse>, ProblemHttpResult>> ApproveAsync(
        Guid venueId,
        ClaimsPrincipal principal,
        AppDbContext database,
        VenueStandingNotices notices,
        TimeProvider timeProvider,
        ILoggerFactory loggers,
        CancellationToken cancellationToken) =>
        DecideAsync(
            venueId, VenueStatus.Approved, reason: null,
            principal, database, notices, timeProvider, loggers, cancellationToken);

    private static Task<Results<Ok<AdminVenueResponse>, ProblemHttpResult>> RejectAsync(
        Guid venueId,
        VenueDecisionRequest request,
        ClaimsPrincipal principal,
        AppDbContext database,
        VenueStandingNotices notices,
        TimeProvider timeProvider,
        ILoggerFactory loggers,
        CancellationToken cancellationToken) =>
        DecideAsync(
            venueId, VenueStatus.Rejected, request.Reason,
            principal, database, notices, timeProvider, loggers, cancellationToken);

    private static Task<Results<Ok<AdminVenueResponse>, ProblemHttpResult>> SuspendAsync(
        Guid venueId,
        VenueDecisionRequest request,
        ClaimsPrincipal principal,
        AppDbContext database,
        VenueStandingNotices notices,
        TimeProvider timeProvider,
        ILoggerFactory loggers,
        CancellationToken cancellationToken) =>
        DecideAsync(
            venueId, VenueStatus.Suspended, request.Reason,
            principal, database, notices, timeProvider, loggers, cancellationToken);

    private static Task<Results<Ok<AdminVenueResponse>, ProblemHttpResult>> ReinstateAsync(
        Guid venueId,
        ClaimsPrincipal principal,
        AppDbContext database,
        VenueStandingNotices notices,
        TimeProvider timeProvider,
        ILoggerFactory loggers,
        CancellationToken cancellationToken) =>
        DecideAsync(
            venueId, VenueStatus.Approved, reason: null,
            principal, database, notices, timeProvider, loggers, cancellationToken);

    /// <summary>
    /// Every decision, through one door. The move is checked against the table, written with its
    /// record in one transaction, and only then is the venue told — a message about a decision
    /// that was rolled back cannot be taken back (PRD US-17).
    /// </summary>
    private static async Task<Results<Ok<AdminVenueResponse>, ProblemHttpResult>> DecideAsync(
        Guid venueId,
        VenueStatus decided,
        string? reason,
        ClaimsPrincipal principal,
        AppDbContext database,
        VenueStandingNotices notices,
        TimeProvider timeProvider,
        ILoggerFactory loggers,
        CancellationToken cancellationToken)
    {
        var written = reason?.Trim();
        if (written is { Length: > VenueStatusChange.ReasonMaxLength })
        {
            return ApiProblem.Of(StatusCodes.Status400BadRequest, VenueErrorCodes.ReasonTooLong);
        }

        var venue = await database.Venues
            .SingleOrDefaultAsync(one => one.Id == venueId, cancellationToken);

        if (venue is null)
        {
            return ApiProblem.Of(StatusCodes.Status404NotFound, VenueErrorCodes.NotFound);
        }

        var from = venue.Status;

        if (!VenueStatusTransitions.CanMove(from, decided))
        {
            return ApiProblem.Of(
                StatusCodes.Status409Conflict, VenueErrorCodes.StatusCannotMoveThere);
        }

        if (VenueStatusTransitions.MustSayWhy(from, decided) && string.IsNullOrEmpty(written))
        {
            return ApiProblem.Of(StatusCodes.Status400BadRequest, VenueErrorCodes.ReasonRequired);
        }

        var now = timeProvider.GetUtcNow();
        var admin = CallerId.Of(principal);

        await using var transaction = await database.Database.BeginTransactionAsync(
            cancellationToken);

        // Conditional on where it stood when the move was checked, so two admins pressing at once
        // do not both write a decision over a venue that has already moved.
        var moved = await database.Venues
            .Where(one => one.Id == venueId && one.Status == from)
            .ExecuteUpdateAsync(
                set => set.SetProperty(one => one.Status, decided), cancellationToken);

        if (moved == 0)
        {
            await transaction.RollbackAsync(cancellationToken);
            return ApiProblem.Of(
                StatusCodes.Status409Conflict, VenueErrorCodes.StatusCannotMoveThere);
        }

        database.VenueStatusChanges.Add(new VenueStatusChange
        {
            VenueId = venueId,
            From = from,
            To = decided,
            ChangedAt = now,
            ChangedByUserId = admin,
            Reason = string.IsNullOrEmpty(written) ? null : written,
        });

        await database.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        // The name inside the template, not a placeholder in it: a sink that groups by template
        // would otherwise see approvals, refusals and suspensions as one event (PRD 8).
        AppEvents.For(loggers).LogInformation(
            $"venue_{decided.ToString().ToLowerInvariant()}" + " {VenueId} {From}", venueId, from);

        // After the commit, and never before it (PRD US-17).
        await notices.StandingChangedAsync(venueId, from, decided, written);

        venue.Status = decided;
        return TypedResults.Ok(Drawn(venue));
    }

    private static IQueryable<Venue> Described(AppDbContext database) =>
        database.Venues.AsNoTracking();

    private static AdminVenueResponse Drawn(Venue venue) =>
        new(
            venue.Id,
            venue.Code,
            venue.Name,
            venue.AddressLine,
            venue.District,
            venue.Province,
            venue.Status.ToString(),
            venue.CreatedAt);
}
