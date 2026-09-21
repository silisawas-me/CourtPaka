using CourtBooking.Api.Bookings;
using CourtBooking.Api.Identity;
using CourtBooking.Api.Venues;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace CourtBooking.Api.Data;

public sealed class AppDbContext(DbContextOptions<AppDbContext> options)
    : IdentityDbContext<AppUser, Microsoft.AspNetCore.Identity.IdentityRole<Guid>, Guid>(options)
{
    public DbSet<UserConsent> UserConsents => Set<UserConsent>();

    public DbSet<AccountStatusChange> AccountStatusChanges => Set<AccountStatusChange>();

    public DbSet<Venue> Venues => Set<Venue>();

    public DbSet<VenueStatusChange> VenueStatusChanges => Set<VenueStatusChange>();

    public DbSet<VenueMembership> VenueMemberships => Set<VenueMembership>();

    public DbSet<VenueInvitation> VenueInvitations => Set<VenueInvitation>();

    public DbSet<Court> Courts => Set<Court>();

    public DbSet<CourtClosure> CourtClosures => Set<CourtClosure>();

    public DbSet<CourtStatusChange> CourtStatusChanges => Set<CourtStatusChange>();

    public DbSet<OpeningHoursSchedule> OpeningHoursSchedules => Set<OpeningHoursSchedule>();

    public DbSet<OpeningHoursDay> OpeningHoursDays => Set<OpeningHoursDay>();

    public DbSet<PriceList> PriceLists => Set<PriceList>();

    public DbSet<PriceBand> PriceBands => Set<PriceBand>();

    public DbSet<CancellationPolicy> CancellationPolicies => Set<CancellationPolicy>();

    public DbSet<CancellationTier> CancellationTiers => Set<CancellationTier>();

    public DbSet<Booking> Bookings => Set<Booking>();

    public DbSet<BookingSlot> BookingSlots => Set<BookingSlot>();

    public DbSet<PaymentSlip> PaymentSlips => Set<PaymentSlip>();

    public DbSet<BookingStatusChange> BookingStatusChanges => Set<BookingStatusChange>();

    public DbSet<RefundRecord> RefundRecords => Set<RefundRecord>();

    public DbSet<BookerNotice> BookerNotices => Set<BookerNotice>();

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

                // One venue has exactly one of each of these and they change by being corrected,
                // not by being versioned, so they sit in the venue's own row (PRD US-10).
                venue.ComplexProperty(one => one.Business, business =>
                {
                    business.Property(b => b.PromptPayId)
                        .HasMaxLength(VenueBusiness.PromptPayIdMaxLength);
                    business.Property(b => b.PromptPayAccountName)
                        .HasMaxLength(VenueBusiness.AccountNameMaxLength);
                    business.Property(b => b.LegalName)
                        .HasMaxLength(VenueBusiness.LegalNameMaxLength);
                    business.Property(b => b.TaxId).HasMaxLength(VenueBusiness.TaxIdLength);
                    business.Property(b => b.TaxBranch).HasMaxLength(VenueBusiness.TaxBranchLength);
                    business.Property(b => b.BillingAddress)
                        .HasMaxLength(VenueBusiness.BillingAddressMaxLength);
                });
            venue.Property(v => v.Code).HasMaxLength(6);
            venue.Property(v => v.Name).HasMaxLength(200);
            venue.Property(v => v.AddressLine).HasMaxLength(200);
            venue.Property(v => v.District).HasMaxLength(100);
            venue.Property(v => v.Province).HasMaxLength(100);
            // Orders the default listing, which is what a booker sees before typing anything. A
            // typed search is ILIKE '%term%', which no b-tree can serve — it is a scan bounded by
            // PublicVenueEndpoints.MaxResults, and wants a trigram index if it ever gets slow.
            venue.HasIndex(v => new { v.Province, v.District });
            // The code prefixes document numbers, so two venues may never share one (PRD 7.4).
            venue.HasIndex(v => v.Code).IsUnique();
        });

        builder.Entity<RefundRecord>(refund =>
        {
            refund.Property(record => record.AmountBaht).HasPrecision(10, 2);

            // The rule about how much is owed is the endpoint's; that an amount is money at all
            // is the column's, so no future write path can get it wrong (PRD US-18).
            refund.ToTable(table => table.HasCheckConstraint(
                "CK_RefundRecords_AmountIsMoney", "\"AmountBaht\" > 0"));
            refund.Property(record => record.Note).HasMaxLength(RefundRecord.NoteMaxLength);
            refund.Property(record => record.VoidReason).HasMaxLength(RefundRecord.NoteMaxLength);

            // Read one booking at a time, always: what has been sent back is a sum over these.
            refund.HasIndex(record => record.BookingId);

            // And across every booking, newest only, when the caretaker tells bookers (US-06).
            refund.HasIndex(record => record.RecordedAt);

            // Nothing may take these with it. A record that disappears when its booking does is
            // weaker evidence than one that cannot be edited, and PDPA deletion (PRD 8, S-15) is
            // anonymising the person, not removing what the venue paid out.
            refund.HasOne(record => record.Booking)
                .WithMany()
                .HasForeignKey(record => record.BookingId)
                .OnDelete(DeleteBehavior.Restrict);

            refund.HasOne(record => record.RecordedBy)
                .WithMany()
                .HasForeignKey(record => record.RecordedByUserId)
                .OnDelete(DeleteBehavior.Restrict);

            // Who reversed money is worth as much as who sent it, so it is a real key too.
            refund.HasOne<AppUser>()
                .WithMany()
                .HasForeignKey(record => record.VoidedByUserId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<VenueMembership>(member =>
        {
            // Said here as well as on the property, so the column a migration writes carries it
            // too — otherwise a membership that already existed would be opted out of a notice
            // nobody asked to stop (PRD US-17).
            member.Property(m => m.WantsSlipEmails).HasDefaultValue(true);

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
            invitation.Property(i => i.NormalizedEmail).HasMaxLength(256);
            invitation.Property(i => i.TokenHash).HasMaxLength(64);
            // The database enforces "one live invitation per address", so a race cannot create two.
            invitation.HasIndex(i => new { i.VenueId, i.NormalizedEmail })
                .IsUnique()
                .HasFilter("\"AcceptedAt\" IS NULL");
            invitation.HasOne(i => i.Venue)
                .WithMany()
                .HasForeignKey(i => i.VenueId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<Court>(court =>
        {
            court.Property(c => c.Name).HasMaxLength(50);
            // Two courts with the same name are indistinguishable on a booking; the database says no
            // so that two simultaneous adds cannot both pass a check in application code.
            court.HasIndex(c => new { c.VenueId, c.Name }).IsUnique();
            court.HasOne(c => c.Venue)
                .WithMany()
                .HasForeignKey(c => c.VenueId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<CourtStatusChange>(change =>
        {
            change.HasIndex(c => new { c.CourtId, c.EffectiveFrom });
            change.HasOne(c => c.Court)
                .WithMany()
                .HasForeignKey(c => c.CourtId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<OpeningHoursSchedule>(schedule =>
        {
            // Versions are only added, so a date may hold more than one; the newest wins.
            schedule.HasIndex(s => new { s.VenueId, s.EffectiveFrom });
            schedule.HasOne(s => s.Venue)
                .WithMany()
                .HasForeignKey(s => s.VenueId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<OpeningHoursDay>(day =>
        {
            day.HasIndex(d => new { d.ScheduleId, d.Day }).IsUnique();
            day.HasOne(d => d.Schedule)
                .WithMany(s => s.Days)
                .HasForeignKey(d => d.ScheduleId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<Booking>(booking =>
        {
            booking.Property(b => b.TotalBaht).HasPrecision(10, 2);
            booking.Property(b => b.RefundDueBaht).HasPrecision(10, 2);
            booking.Property(b => b.CustomerName).HasMaxLength(Booking.CustomerNameMaxLength);
            booking.Property(b => b.CustomerPhone).HasMaxLength(Booking.CustomerPhoneMaxLength);

            // A booking is one of two shapes and never half of both: online, with the account
            // that made it; or at the counter, with the customer's name and how they paid
            // (PRD US-13). Held apart by the database rather than by every path that writes one,
            // because a booking with neither a booker nor a customer is a booking nobody can be
            // told about, refunded, or asked about.
            booking.ToTable(table => table.HasCheckConstraint(
                "CK_Bookings_BookerOrCustomer",
                "(\"Channel\" = 1 AND \"BookerUserId\" IS NOT NULL AND \"CustomerName\" IS NULL)"
                + " OR (\"Channel\" = 2 AND \"BookerUserId\" IS NULL"
                + " AND \"CustomerName\" IS NOT NULL AND \"PaidAtCounter\" IS NOT NULL)"));
            // The booker's own history (US-05), and the check that they hold only one (PRD S-22).
            booking.HasIndex(b => new { b.BookerUserId, b.Status });
            booking.HasIndex(b => new { b.VenueId, b.CreatedAt });
            booking.HasOne(b => b.Venue)
                .WithMany()
                .HasForeignKey(b => b.VenueId)
                .OnDelete(DeleteBehavior.Cascade);
            booking.HasOne(b => b.Booker)
                .WithMany()
                .HasForeignKey(b => b.BookerUserId)
                .OnDelete(DeleteBehavior.Restrict);
            // The policy this booking is refunded under stays readable for as long as the booking
            // does, so a venue publishing a new one cannot delete the terms someone booked under.
            booking.HasOne(b => b.CancellationPolicy)
                .WithMany()
                .HasForeignKey(b => b.CancellationPolicyId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<BookingSlot>(slot =>
        {
            slot.Property(s => s.BahtPerHour).HasPrecision(10, 2);
            slot.HasIndex(s => new { s.CourtId, s.StartsAt });
            // Hours about to be played, across every court: the reminder before play (US-06).
            slot.HasIndex(s => s.StartsAt).HasFilter("\"IsActive\"");
            slot.HasOne(s => s.Booking)
                .WithMany(b => b.Slots)
                .HasForeignKey(s => s.BookingId)
                .OnDelete(DeleteBehavior.Cascade);
            slot.HasOne(s => s.Court)
                .WithMany()
                .HasForeignKey(s => s.CourtId)
                .OnDelete(DeleteBehavior.Restrict);
            // Two people may not hold the same court at the same time. The rule is the database's,
            // not the application's, so simultaneous writes cannot both pass a check (PRD BR-04,
            // 9.2). The constraint itself is written in the migration: EF has no model for one.
        });

        builder.Entity<CourtClosure>(closure =>
        {
            closure.Property(c => c.Reason).HasMaxLength(CourtClosure.ReasonMaxLength);

            // Every read is "what is shut on this court around this time", from the grid, the
            // settings screen and the write that takes an hour.
            closure.HasIndex(c => new { c.CourtId, c.StartsAt });

            closure.HasOne(c => c.Court)
                .WithMany()
                .HasForeignKey(c => c.CourtId)
                .OnDelete(DeleteBehavior.Cascade);

            closure.HasOne(c => c.CreatedBy)
                .WithMany()
                .HasForeignKey(c => c.CreatedByUserId)
                .OnDelete(DeleteBehavior.Restrict);

            closure.HasOne<AppUser>()
                .WithMany()
                .HasForeignKey(c => c.LiftedByUserId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<VenueStatusChange>(change =>
        {
            change.Property(c => c.Reason).HasMaxLength(VenueStatusChange.ReasonMaxLength);

            // A venue's own history, oldest first, which is how the platform reads it.
            change.HasIndex(c => new { c.VenueId, c.ChangedAt });

            change.HasOne(c => c.Venue)
                .WithMany()
                .HasForeignKey(c => c.VenueId)
                .OnDelete(DeleteBehavior.Restrict);

            change.HasOne(c => c.ChangedBy)
                .WithMany()
                .HasForeignKey(c => c.ChangedByUserId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<BookingStatusChange>(change =>
        {
            change.Property(c => c.Reason).HasMaxLength(BookingStatusChange.ReasonMaxLength);
            // A booking's history, oldest first, which is how US-12 and US-22 will read it.
            change.HasIndex(c => new { c.BookingId, c.ChangedAt });

            // Counting cancellations by reason is a report of its own (US-15) and a check the
            // court-closing screen has to make (US-11), and both scan every row of a venue's
            // history, so the reason is worth an index where it is set.
            change.HasIndex(c => c.Cause).HasFilter("\"Cause\" IS NOT NULL");
            // The history lives exactly as long as the booking it describes. Nothing deletes a
            // booking — a booker asking to be forgotten is anonymised, not erased (PRD 8) — so
            // this cascade is what happens when a venue is removed with everything under it.
            change.HasOne(c => c.Booking)
                .WithMany(b => b.StatusChanges)
                .HasForeignKey(c => c.BookingId)
                .OnDelete(DeleteBehavior.Cascade);
            // Restrict, like every other reference to an account: a booker asking to be
            // forgotten is anonymised rather than deleted (PRD 8), so the row stays and keeps
            // pointing at the same, now anonymous, person.
            change.HasOne(c => c.ChangedBy)
                .WithMany()
                .HasForeignKey(c => c.ChangedByUserId)
                .OnDelete(DeleteBehavior.Restrict);

            // The caretaker reads the latest moves across every booking to tell bookers about
            // them (PRD US-06), and only the latest, so it asks by time alone.
            change.HasIndex(c => c.ChangedAt);
        });

        builder.Entity<BookerNotice>(notice =>
        {
            // The claim: one message per thing it is about, whoever tries to send it (PRD US-06).
            notice.HasIndex(n => new { n.SourceId, n.Kind }).IsUnique();
            notice.HasIndex(n => n.BookingId);
            notice.HasOne(n => n.Booking)
                .WithMany()
                .HasForeignKey(n => n.BookingId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<PaymentSlip>(slip =>
        {
            slip.Property(s => s.StoredName).HasMaxLength(128);
            slip.Property(s => s.ContentType).HasMaxLength(100);
            slip.Property(s => s.Sha256).HasMaxLength(64);
            // The newest slip of a booking, and the duplicate check across a venue's slips.
            slip.HasIndex(s => new { s.BookingId, s.UploadedAt });
            slip.HasIndex(s => s.Sha256);
            slip.HasOne(s => s.Booking)
                .WithMany()
                .HasForeignKey(s => s.BookingId)
                .OnDelete(DeleteBehavior.Cascade);
            slip.HasOne(s => s.UploadedBy)
                .WithMany()
                .HasForeignKey(s => s.UploadedByUserId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<PriceList>(list =>
        {
            // Read newest-first to find the one in force.
            list.HasIndex(l => new { l.VenueId, l.CreatedAt });
            list.HasOne(l => l.Venue)
                .WithMany()
                .HasForeignKey(l => l.VenueId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<PriceBand>(band =>
        {
            // Baht to two decimals (PRD BR-05); a float would drift on a month of bookings.
            band.Property(b => b.BahtPerHour).HasPrecision(10, 2);
            band.HasIndex(b => new { b.PriceListId, b.Day, b.FromHour });
            band.HasOne(b => b.PriceList)
                .WithMany(l => l.Bands)
                .HasForeignKey(b => b.PriceListId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<CancellationPolicy>(policy =>
        {
            policy.HasIndex(p => new { p.VenueId, p.CreatedAt });
            policy.HasOne(p => p.Venue)
                .WithMany()
                .HasForeignKey(p => p.VenueId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<CancellationTier>(tier =>
        {
            tier.HasIndex(t => new { t.PolicyId, t.HoursBefore }).IsUnique();
            tier.HasOne(t => t.Policy)
                .WithMany(p => p.Tiers)
                .HasForeignKey(t => t.PolicyId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<AccountStatusChange>(change =>
        {
            change.Property(c => c.Reason).HasMaxLength(AccountStatusChange.ReasonMaxLength);
            change.HasIndex(c => new { c.UserId, c.ChangedAt });
            // Restrict both ways, like every reference to an account: a person asking to be
            // forgotten is anonymised, not deleted (PRD 8), and the record of why they were
            // stopped must not go with them.
            change.HasOne(c => c.User)
                .WithMany()
                .HasForeignKey(c => c.UserId)
                .OnDelete(DeleteBehavior.Restrict);
            change.HasOne(c => c.ChangedBy)
                .WithMany()
                .HasForeignKey(c => c.ChangedByUserId)
                .OnDelete(DeleteBehavior.Restrict);
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
