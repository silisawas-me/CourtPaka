using CourtBooking.Api.Identity;
using CourtBooking.Api.Venues;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace CourtBooking.Api.Data;

public sealed class AppDbContext(DbContextOptions<AppDbContext> options)
    : IdentityDbContext<AppUser, Microsoft.AspNetCore.Identity.IdentityRole<Guid>, Guid>(options)
{
    public DbSet<UserConsent> UserConsents => Set<UserConsent>();

    public DbSet<Venue> Venues => Set<Venue>();

    public DbSet<VenueMembership> VenueMemberships => Set<VenueMembership>();

    public DbSet<VenueInvitation> VenueInvitations => Set<VenueInvitation>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.Entity<AppUser>(user =>
        {
            user.Property(u => u.Language).HasMaxLength(8);
            // Identity only checks uniqueness in application code; the database has to enforce it too,
            // otherwise two simultaneous registrations both succeed.
            user.HasIndex(u => u.NormalizedEmail).HasDatabaseName("EmailIndex").IsUnique();
        });

        builder.Entity<Venue>(venue =>
        {
            venue.Property(v => v.Code).HasMaxLength(6);
            venue.Property(v => v.Name).HasMaxLength(200);
            // The code prefixes document numbers, so two venues may never share one (PRD 7.4).
            venue.HasIndex(v => v.Code).IsUnique();
        });

        builder.Entity<VenueMembership>(member =>
        {
            // One row per person per venue: permissions are the venue's answer about that person.
            member.HasIndex(m => new { m.VenueId, m.UserId }).IsUnique();
            member.HasOne(m => m.Venue)
                .WithMany()
                .HasForeignKey(m => m.VenueId)
                .OnDelete(DeleteBehavior.Cascade);
            member.HasOne(m => m.User)
                .WithMany()
                .HasForeignKey(m => m.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<VenueInvitation>(invitation =>
        {
            invitation.Property(i => i.Email).HasMaxLength(256);
            invitation.Property(i => i.TokenHash).HasMaxLength(64);
            invitation.HasIndex(i => new { i.VenueId, i.Email });
            invitation.HasOne(i => i.Venue)
                .WithMany()
                .HasForeignKey(i => i.VenueId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<UserConsent>(consent =>
        {
            consent.Property(c => c.Version).HasMaxLength(32);
            consent.HasIndex(c => new { c.UserId, c.Type, c.Version });
            consent.HasOne<AppUser>()
                .WithMany(user => user.Consents)
                .HasForeignKey(c => c.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }
}
