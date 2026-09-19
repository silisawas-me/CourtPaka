using CourtBooking.Api.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace CourtBooking.Api.Data;

public sealed class AppDbContext(DbContextOptions<AppDbContext> options)
    : IdentityDbContext<AppUser, Microsoft.AspNetCore.Identity.IdentityRole<Guid>, Guid>(options)
{
    public DbSet<UserConsent> UserConsents => Set<UserConsent>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.Entity<AppUser>()
            .Property(user => user.Language)
            .HasMaxLength(8);

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
