using CourtBooking.Api.Identity;
using CourtBooking.Api.Localization;

namespace CourtBooking.Api.Venues;

/// <summary>
/// A court shut for a stretch of time — a flooded floor, a tournament, a repair (PRD US-11).
///
/// Different from a court being taken out of use, which is a date the court stops existing as far
/// as the grid is concerned and stays that way until somebody says otherwise. A closure has an end
/// built in: the court comes back by itself, and the timeline the utilisation report reads
/// (<see cref="CourtStatusChange"/>) is left alone, because a court closed for an afternoon was
/// still a court the venue had that month.
///
/// Rows are added and never rewritten. Lifting one early is recorded on the row rather than by
/// deleting it: what the venue said at the time is part of why an evening had no bookings.
/// </summary>
public sealed class CourtClosure
{
    /// <summary>Why the court is shut. Long enough to be useful, short enough to read in a row.</summary>
    public const int ReasonMaxLength = 200;

    public Guid Id { get; init; } = Guid.CreateVersion7();

    public required Guid CourtId { get; init; }

    /// <summary>
    /// The first instant the court is shut, and the first instant it is back. Stored as UTC like
    /// every other instant here, and always on whole hours, because an hour is the smallest thing
    /// this system sells — half a closed hour would still be an hour nobody can book.
    /// </summary>
    public required DateTimeOffset StartsAt { get; init; }

    public required DateTimeOffset EndsAt { get; init; }

    /// <summary>The venue's own words. Required: a court shut for no stated reason helps nobody.</summary>
    public required string Reason { get; init; }

    public required Guid CreatedByUserId { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>
    /// When the venue ended the closure early, if it did. A lifted closure stops shutting hours
    /// but stays in the table.
    /// </summary>
    public DateTimeOffset? LiftedAt { get; set; }

    public Guid? LiftedByUserId { get; set; }

    public Court? Court { get; init; }

    public AppUser? CreatedBy { get; init; }

    /// <summary>Whether this closure is still shutting anything.</summary>
    public bool Stands => LiftedAt is null;

    /// <summary>
    /// Whether this closure covers a given hour of a given day. The hour is named the way the rest
    /// of the system names hours — a Thai date and an hour of it — and turned into the instant it
    /// begins, so the comparison happens in the one time zone that matters (PRD BR-10).
    /// </summary>
    public bool Covers(DateOnly date, int hour)
    {
        var at = PlatformRequirements.BangkokHour(date, hour);
        return Stands && StartsAt <= at && at < EndsAt;
    }
}
