using CourtBooking.Api.Identity;
using CourtBooking.Api.Localization;
using CourtBooking.Api.Venues;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace CourtBooking.Api.Data;

/// <summary>
/// Gives a local stack something to sign in to and manage, so the dev loop needs no manual setup.
/// Only ever runs when App:SeedDevelopmentData is on, which no deployed environment sets.
/// </summary>
public static class DevelopmentSeeder
{
    public const string OwnerEmail = "owner@courtpaka.local";
    public const string StaffEmail = "staff@courtpaka.local";
    public const string Password = "DevPassword1";
    public const string VenueCode = "DEV01";
    public const int CourtCount = 4;
    public const int OpensHour = 6;
    public const int ClosesHour = 22;
    public const int PeakFromHour = 18;

    public static async Task SeedAsync(IServiceProvider services, CancellationToken cancellationToken = default)
    {
        using var scope = services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        var time = scope.ServiceProvider.GetRequiredService<TimeProvider>();

        var owner = await EnsureUserAsync(users, OwnerEmail);
        var staff = await EnsureUserAsync(users, StaffEmail);

        var venue = await database.Venues.SingleOrDefaultAsync(v => v.Code == VenueCode, cancellationToken);
        if (venue is null)
        {
            venue = new Venue
            {
                Code = VenueCode,
                Name = "Development Court",
                AddressLine = "123 ถนนสุขุมวิท",
                District = "วัฒนา",
                Province = "กรุงเทพมหานคร",
                Status = VenueStatus.Approved,
                CreatedAt = time.GetUtcNow(),
            };
            database.Venues.Add(venue);
        }

        await EnsureMembershipAsync(database, venue.Id, owner.Id, VenueRole.Owner, VenuePermissions.None, time);
        await EnsureMembershipAsync(
            database, venue.Id, staff.Id, VenueRole.Staff, VenuePermissions.StaffDefault, time);

        // Courts and a week of opening hours, so the availability screens have something to show
        // without anyone setting a venue up by hand first.
        await EnsureCourtsAsync(database, venue.Id, owner.Id, time, cancellationToken);
        await EnsureOpeningHoursAsync(database, venue.Id, owner.Id, time, cancellationToken);
        await EnsurePricesAsync(database, venue.Id, owner.Id, time, cancellationToken);
        await EnsureCancellationPolicyAsync(database, venue.Id, owner.Id, time, cancellationToken);

        await database.SaveChangesAsync(cancellationToken);
    }

    private static async Task EnsureCourtsAsync(
        AppDbContext database,
        Guid venueId,
        Guid ownerId,
        TimeProvider time,
        CancellationToken cancellationToken)
    {
        if (await database.Courts.AnyAsync(court => court.VenueId == venueId, cancellationToken))
        {
            return;
        }

        var now = time.GetUtcNow();
        var today = PlatformRequirements.BangkokToday(time);

        for (var index = 0; index < CourtCount; index++)
        {
            // Through the same factory the endpoint uses, so the court and its first status row are
            // always written together.
            var (court, status) = Court.Open(venueId, $"Court {index + 1}", index, ownerId, today, now);
            database.Courts.Add(court);
            database.CourtStatusChanges.Add(status);
        }
    }

    private static async Task EnsureOpeningHoursAsync(
        AppDbContext database,
        Guid venueId,
        Guid ownerId,
        TimeProvider time,
        CancellationToken cancellationToken)
    {
        if (await database.OpeningHoursSchedules.AnyAsync(
                schedule => schedule.VenueId == venueId, cancellationToken))
        {
            return;
        }

        var week = Enum.GetValues<DayOfWeek>().Select(day => new WeekdayHours(day, OpensHour, ClosesHour));

        database.OpeningHoursSchedules.Add(OpeningHoursSchedule.Create(
            venueId,
            PlatformRequirements.BangkokToday(time),
            week,
            ownerId,
            time.GetUtcNow()));
    }

    private static async Task EnsurePricesAsync(
        AppDbContext database,
        Guid venueId,
        Guid ownerId,
        TimeProvider time,
        CancellationToken cancellationToken)
    {
        if (await database.PriceLists.AnyAsync(list => list.VenueId == venueId, cancellationToken))
        {
            return;
        }

        // Cheaper before the evening rush, the way a Thai venue usually prices its courts.
        var bands = Enum.GetValues<DayOfWeek>()
            .SelectMany(day => new[]
            {
                new BandHours(day, OpensHour, PeakFromHour, 200m),
                new BandHours(day, PeakFromHour, ClosesHour, 300m),
            });

        database.PriceLists.Add(PriceList.Create(venueId, bands, ownerId, time.GetUtcNow()));
    }

    /// <summary>
    /// The venue here is written straight to the context rather than through the endpoint, so the
    /// terms it would have been created with have to be written too (PRD S-11).
    /// </summary>
    private static async Task EnsureCancellationPolicyAsync(
        AppDbContext database,
        Guid venueId,
        Guid ownerId,
        TimeProvider time,
        CancellationToken cancellationToken)
    {
        if (await database.CancellationPolicies.AnyAsync(
                policy => policy.VenueId == venueId, cancellationToken))
        {
            return;
        }

        database.CancellationPolicies.Add(CancellationPolicy.Create(
            venueId, CancellationPolicy.Default, ownerId, time.GetUtcNow()));
    }

    private static async Task<AppUser> EnsureUserAsync(UserManager<AppUser> users, string email)
    {
        var existing = await users.FindByEmailAsync(email);
        if (existing is not null)
        {
            return existing;
        }

        var user = new AppUser
        {
            UserName = email,
            Email = email,
            EmailConfirmed = true,
            Language = SupportedLanguages.Thai,
        };

        var result = await users.CreateAsync(user, Password);
        if (!result.Succeeded)
        {
            throw new InvalidOperationException(
                $"Could not seed {email}: {string.Join(", ", result.Errors.Select(error => error.Description))}");
        }

        return user;
    }

    private static async Task EnsureMembershipAsync(
        AppDbContext database,
        Guid venueId,
        Guid userId,
        VenueRole role,
        VenuePermissions permissions,
        TimeProvider time)
    {
        if (await database.VenueMemberships.AnyAsync(m => m.VenueId == venueId && m.UserId == userId))
        {
            return;
        }

        database.VenueMemberships.Add(new VenueMembership
        {
            VenueId = venueId,
            UserId = userId,
            Role = role,
            Permissions = permissions,
            CreatedAt = time.GetUtcNow(),
        });
    }
}
