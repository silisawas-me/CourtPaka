using CourtBooking.Api.Data;
using CourtBooking.Api.Email;
using Microsoft.EntityFrameworkCore;

namespace CourtBooking.Api.Venues;

/// <summary>
/// Telling a venue where it stands with the platform (PRD US-20, US-17).
///
/// Separate from <see cref="VenueNotifications"/>, which decides who at a venue hears about work
/// the venue has to do and only ever writes to venues that may act. This is the opposite case:
/// the messages that matter most here go to venues that have just been stopped from acting, and
/// they go to the owner rather than to whoever holds a permission — being suspended is not work,
/// it is news about the business.
///
/// Takes no cancellation token, for the same reason nothing else that sends mail does: by the
/// time it is called the decision is committed, and a caller hanging up must not be what decides
/// whether the venue was told.
/// </summary>
public sealed class VenueStandingNotices(
    AppDbContext database,
    ITransactionalEmailSender emails,
    ILoggerFactory loggers)
{
    public async Task StandingChangedAsync(
        Guid venueId,
        VenueStatus from,
        VenueStatus to,
        string? reason)
    {
        var owners = await database.VenueMemberships
            .AsNoTracking()
            .Where(member => member.VenueId == venueId && member.Role == VenueRole.Owner)
            .Select(member => new
            {
                Address = member.User!.Email,
                member.User!.Language,
                VenueName = member.Venue!.Name,
            })
            .ToListAsync(CancellationToken.None);

        var log = loggers.CreateLogger<VenueStandingNotices>();

        foreach (var owner in owners.Where(one => !string.IsNullOrEmpty(one.Address)))
        {
            var (subject, body) = VenueLetters.Standing(owner.VenueName, from, to, reason, owner.Language);

            try
            {
                await emails.SendAsync(
                    new EmailMessage(
                        owner.Address!, owner.Language, subject, body, VenueLetters.StandingTemplate),
                    CancellationToken.None);
            }
            catch (Exception failure)
            {
                // The decision stands whether or not the message went. Said out loud so somebody
                // can tell the venue by other means, and nothing more.
                log.LogError(
                    failure,
                    "Could not tell venue {VenueId} that it moved from {From} to {To}.",
                    venueId, from, to);
            }
        }
    }
}
