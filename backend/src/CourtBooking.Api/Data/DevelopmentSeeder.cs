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
            var court = new Court
            {
                VenueId = venueId,
                Name = $"Court {index + 1}",
                Position = index,
                IsActive = true,
                CreatedAt = now,
            };

            database.Courts.Add(court);
            database.CourtStatusChanges.Add(new CourtStatusChange
            {
                CourtId = court.Id,
                Active = true,
                EffectiveFrom = today,
                ChangedByUserId = ownerId,
                ChangedAt = now,
            });
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

        var schedule = new OpeningHoursSchedule
        {
            VenueId = venueId,
            // Dated well back, so the seeded week is in force however long the database has existed.
            EffectiveFrom = PlatformRequirements.BangkokToday(time).AddYears(-1),
            CreatedByUserId = ownerId,
            CreatedAt = time.GetUtcNow(),
        };

        schedule.Days.AddRange(Enum.GetValues<DayOfWeek>().Select(day => new OpeningHoursDay
        {
            ScheduleId = schedule.Id,
            Day = day,
            OpensHour = OpensHour,
            ClosesHour = ClosesHour,
        }));

        database.OpeningHoursSchedules.Add(schedule);
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
