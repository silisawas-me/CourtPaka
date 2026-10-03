using CourtBooking.Api.Bookings;
using CourtBooking.Api.Documents;
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

    public DbSet<OwnerInvitation> OwnerInvitations => Set<OwnerInvitation>();

    public DbSet<Court> Courts => Set<Court>();

    public DbSet<CourtClosure> CourtClosures => Set<CourtClosure>();

    public DbSet<WaitlistEntry> WaitlistEntries => Set<WaitlistEntry>();

    public DbSet<BookingSeries> BookingSeries => Set<BookingSeries>();

    public DbSet<SeriesMiss> SeriesMisses => Set<SeriesMiss>();

    public DbSet<PackageType> PackageTypes => Set<PackageType>();

    public DbSet<HourPackage> HourPackages => Set<HourPackage>();

    public DbSet<PackageEntry> PackageEntries => Set<PackageEntry>();

    public DbSet<ShopItem> ShopItems => Set<ShopItem>();

    public DbSet<ShopSale> ShopSales => Set<ShopSale>();

    public DbSet<ShopSaleLine> ShopSaleLines => Set<ShopSaleLine>();

    public DbSet<StockEntry> StockEntries => Set<StockEntry>();

    public DbSet<Spend> Spends => Set<Spend>();

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

    public DbSet<BookingArrivalChange> BookingArrivalChanges => Set<BookingArrivalChange>();

    public DbSet<BookingSlotChange> BookingSlotChanges => Set<BookingSlotChange>();

    public DbSet<PaymentReceipt> PaymentReceipts => Set<PaymentReceipt>();

    public DbSet<DailyClosing> DailyClosings => Set<DailyClosing>();

    public DbSet<RefundRecord> RefundRecords => Set<RefundRecord>();

    public DbSet<BookerNotice> BookerNotices => Set<BookerNotice>();

    public DbSet<Complaint> Complaints => Set<Complaint>();

    public DbSet<SlipViewing> SlipViewings => Set<SlipViewing>();

    public DbSet<MembershipChange> MembershipChanges => Set<MembershipChange>();

    public DbSet<DocumentSeries> DocumentSeries => Set<DocumentSeries>();

    public DbSet<CommissionRate> CommissionRates => Set<CommissionRate>();

    public DbSet<CommissionInvoice> CommissionInvoices => Set<CommissionInvoice>();

    public DbSet<CommissionInvoiceLine> CommissionInvoiceLines => Set<CommissionInvoiceLine>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.Entity<AppUser>()
            .Property(user => user.DisplayName)
            .HasMaxLength(AppUser.DisplayNameMaxLength);

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
            // Same reason as Booking.Arrival: a venue that existed before this waits fifteen
            // minutes like everybody else, not zero (PRD US-24).
            venue.Property(v => v.GraceMinutes).HasDefaultValue(VenueDecisions.DefaultGraceMinutes);

            // The CLR default of an int is 0, and a venue that asks for nothing up front holds
            // hours for nothing. Said here so that every venue already on the platform keeps
            // asking for the whole price, which is what it has always asked for (PRD US-28).
            venue.Property(v => v.DepositPercent).HasDefaultValue(Deposit.Everything);

            // Beside the venue in its own row: there is one set of these and it is changed by
            // changing it, not by adding a version (the bookings snapshot what they were asked).
            // No database defaults, on purpose: a default of true on a bool whose CLR default is
            // false is a column that can never be written false (the trap this file has met
            // before). The migration fills the venues that existed and then drops the default.
            venue.ComplexProperty(one => one.Risk);
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

            // Money, so it is stored like every other amount (PRD US-18).
            member.Property(m => m.RefundLimitBaht).HasPrecision(10, 2);

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

        builder.Entity<OwnerInvitation>(invitation =>
        {
            invitation.Property(i => i.Email).HasMaxLength(OwnerInvitation.EmailMaxLength);
            invitation.Property(i => i.NormalizedEmail).HasMaxLength(OwnerInvitation.EmailMaxLength);
            invitation.Property(i => i.Language).HasMaxLength(8);
            // One live invitation per address, held by the database as a venue's is.
            invitation.HasIndex(i => i.NormalizedEmail).IsUnique().HasFilter("\"AcceptedAt\" IS NULL");
        });

        builder.Entity<VenueInvitation>(invitation =>
        {
            invitation.Property(i => i.Email).HasMaxLength(256);
            invitation.Property(i => i.NormalizedEmail).HasMaxLength(256);
            invitation.Property(i => i.Name).HasMaxLength(100);
            invitation.Property(i => i.Phone).HasMaxLength(20);
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
            booking.Property(b => b.DepositBaht).HasPrecision(10, 2);
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
                + " AND \"CustomerName\" IS NOT NULL"
                // How they paid, or the reason it is theirs without money changing hands
                // today: a standing group's week is confirmed before anybody has paid for it
                // and the desk takes the money when they turn up (PRD US-30), and a booking
                // on a package was paid for when the package was sold (PRD US-31).
                + " AND (\"PaidAtCounter\" IS NOT NULL OR \"SeriesId\" IS NOT NULL"
                + " OR \"PackageId\" IS NOT NULL))"));
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

            booking.Property(b => b.PackageBaht).HasPrecision(10, 2);

            // Paid with hours or paid with money, never partly both (PRD US-31): a booking with
            // hours on it owes nothing, so hours and an amount of money would be two answers to
            // the same question.
            booking.ToTable(table => table.HasCheckConstraint(
                "CK_Bookings_PackagePaysForItWhole",
                "(\"PackageId\" IS NULL AND \"PackageHours\" = 0 AND \"PackageBaht\" = 0)"
                // Nothing, once it has been cancelled and every hour has gone back: what it kept
                // is what it earned, and it kept none of it (PRD US-31).
                + " OR (\"PackageId\" IS NOT NULL AND \"PackageHours\" > 0"
                + " AND \"PackageBaht\" >= 0)"));

            booking.HasIndex(b => b.PackageId);
            booking.HasOne(b => b.Package)
                .WithMany()
                .HasForeignKey(b => b.PackageId)
                .OnDelete(DeleteBehavior.Restrict);

            // Which weeks an arrangement has already made (PRD US-30). The job asks this of every
            // running arrangement on every sweep, so it is the index that answers it.
            booking.HasIndex(b => b.SeriesId);
            booking.HasOne(b => b.Series)
                .WithMany()
                .HasForeignKey(b => b.SeriesId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<BookingSeries>(series =>
        {
            series.Property(one => one.CustomerName).HasMaxLength(Booking.CustomerNameMaxLength);
            series.Property(one => one.CustomerPhone).HasMaxLength(Booking.CustomerPhoneMaxLength);
            series.Property(one => one.EndReason).HasMaxLength(BookingStatusChange.ReasonMaxLength);

            // The hours are a window, and the same one every week — guarded here as well as in the
            // rules, because a backwards window would ask the floor for hours that do not exist.
            series.ToTable(table => table.HasCheckConstraint(
                "CK_BookingSeries_HoursAreAWindow",
                "\"FromHour\" >= 0 AND \"UntilHour\" <= 24 AND \"FromHour\" < \"UntilHour\""));

            // Every read is "what does this venue have standing", newest first.
            series.HasIndex(one => new { one.VenueId, one.State, one.CreatedAt });

            // One group per court, per day, per pair of hours, at the database rather than only
            // in the handler: a second one could never have a single week of it, and every week
            // it missed would be written off for good.
            series.HasIndex(one => new
                {
                    one.VenueId,
                    one.CourtId,
                    one.Day,
                    one.FromHour,
                    one.UntilHour,
                })
                .IsUnique()
                .HasFilter($"\"State\" = {(int)SeriesState.Running}");

            series.HasOne(one => one.Venue)
                .WithMany()
                .HasForeignKey(one => one.VenueId)
                .OnDelete(DeleteBehavior.Cascade);

            series.HasOne(one => one.Court)
                .WithMany()
                .HasForeignKey(one => one.CourtId)
                .OnDelete(DeleteBehavior.Restrict);

            // The arrangement this one took over from. Restrict: the thread between them is the
            // only thing that says the group did not simply appear one week under new terms.
            series.HasOne(one => one.Replaced)
                .WithMany()
                .HasForeignKey(one => one.ReplacedSeriesId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<SeriesMiss>(miss =>
        {
            miss.Property(one => one.Refusal).HasMaxLength(Series.RefusalMaxLength);

            // One row per week, at the database: a week noticed twice would be told to the venue
            // twice, and the job leans on this to know which weeks it has already given up on.
            miss.HasIndex(one => new { one.SeriesId, one.Date }).IsUnique();

            miss.HasOne(one => one.Series)
                .WithMany()
                .HasForeignKey(one => one.SeriesId)
                .OnDelete(DeleteBehavior.Cascade);
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

        builder.Entity<WaitlistEntry>(entry =>
        {
            // Every read is "who is waiting on this venue, for this day, in the order they
            // asked" — the queue as it is offered and as it is shown (PRD US-27).
            entry.HasIndex(one => new { one.VenueId, one.Date, one.State, one.AskedAt });

            // One place per booker per venue per day, at the database rather than only in the
            // handler: a queue with the same name in it twice offers the same hour twice.
            entry.HasIndex(one => new { one.VenueId, one.BookerUserId, one.Date })
                .IsUnique()
                .HasFilter($"\"State\" IN ({(int)WaitlistState.Waiting}, {(int)WaitlistState.Offered})");

            entry.HasOne(one => one.Venue)
                .WithMany()
                .HasForeignKey(one => one.VenueId)
                .OnDelete(DeleteBehavior.Cascade);

            entry.HasOne(one => one.Booker)
                .WithMany()
                .HasForeignKey(one => one.BookerUserId)
                .OnDelete(DeleteBehavior.Cascade);

            // The hold an offer put aside. Restrict, not cascade: a booking is never deleted, and
            // if one ever were, losing the queue it came from with it would hide what happened.
            entry.HasOne(one => one.OfferedBooking)
                .WithMany()
                .HasForeignKey(one => one.OfferedBookingId)
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

        builder.Entity<Booking>(booking =>
        {
            // Said here as well as on the property, or every booking that already exists would
            // come out of the migration as arrival 0, which is no arrival at all (CLAUDE.md's
            // EF trap, found in US-17).
            booking.Property(b => b.Arrival).HasDefaultValue(BookingArrival.Unconfirmed);
        });

        builder.Entity<BookingArrivalChange>(change =>
        {
            // Read one booking at a time, oldest first: the counter asks "what did we know about
            // this person and when" (PRD US-24).
            change.HasIndex(c => new { c.BookingId, c.ChangedAt });
            change.HasOne(c => c.Booking)
                .WithMany()
                .HasForeignKey(c => c.BookingId)
                .OnDelete(DeleteBehavior.Cascade);
            // Restrict, like every other reference to an account (PRD 8, S-15).
            change.HasOne(c => c.ChangedBy)
                .WithMany()
                .HasForeignKey(c => c.ChangedByUserId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<BookingSlotChange>(change =>
        {
            change.Property(c => c.BahtPerHour).HasPrecision(10, 2);
            // Read one booking at a time, oldest first: "which court were we on at eight" is a
            // question about one evening (PRD US-29).
            change.HasIndex(c => new { c.BookingId, c.ChangedAt });
            change.HasOne(c => c.Booking)
                .WithMany()
                .HasForeignKey(c => c.BookingId)
                .OnDelete(DeleteBehavior.Cascade);
            // A court cannot be deleted, only taken out of use, so nothing here ever dangles —
            // and Restrict says out loud that a court named in an audit row is not disposable.
            change.HasOne(c => c.FromCourt)
                .WithMany()
                .HasForeignKey(c => c.FromCourtId)
                .OnDelete(DeleteBehavior.Restrict);
            change.HasOne(c => c.ToCourt)
                .WithMany()
                .HasForeignKey(c => c.ToCourtId)
                .OnDelete(DeleteBehavior.Restrict);
            // Restrict, like every other reference to an account (PRD 8, S-15).
            change.HasOne(c => c.ChangedBy)
                .WithMany()
                .HasForeignKey(c => c.ChangedByUserId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<DocumentSeries>(series =>
        {
            series.Property(s => s.SeriesCode).HasMaxLength(Documents.DocumentSeries.SeriesCodeMaxLength);

            // One count per series, and the index is what makes taking a number from it safe:
            // two writers creating the same series at once meet here, and the loser reads the
            // winner's row instead of writing a second count (PRD 7.4).
            series.HasIndex(s => new { s.SeriesCode, s.Kind, s.Year }).IsUnique();
        });

        builder.Entity<CommissionRate>(rate =>
        {
            rate.Property(r => r.Percent).HasPrecision(5, 2);
            rate.Property(r => r.Note).HasMaxLength(CommissionRate.NoteMaxLength);

            // Read one venue at a time, newest first: an invoice asks "what was the rate on this
            // day", and a screen asks "what is it now and what was it before" (PRD US-21).
            rate.HasIndex(r => new { r.VenueId, r.EffectiveFrom });
            rate.HasOne(r => r.Venue)
                .WithMany()
                .HasForeignKey(r => r.VenueId)
                .OnDelete(DeleteBehavior.Cascade);
            // Restrict, like every other reference to an account (PRD 8, S-15).
            rate.HasOne(r => r.SetBy)
                .WithMany()
                .HasForeignKey(r => r.SetByUserId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<CommissionInvoice>(invoice =>
        {
            invoice.Property(i => i.AmountBaht).HasPrecision(10, 2);
            invoice.Property(i => i.Number).HasMaxLength(64);
            invoice.Property(i => i.RefusedReason).HasMaxLength(CommissionInvoice.ReasonMaxLength);

            // A document number is how a venue and the platform talk about one invoice, so two
            // of anything carrying the same number is not something to find out later (PRD 7.4).
            invoice.HasIndex(i => i.Number).IsUnique();

            // One invoice per venue per month. The run is built to be safe to repeat, and this
            // is the answer if it ever is not (PRD US-21).
            invoice.HasIndex(i => new { i.VenueId, i.Month }).IsUnique();

            invoice.HasOne(i => i.Venue)
                .WithMany()
                .HasForeignKey(i => i.VenueId)
                .OnDelete(DeleteBehavior.Restrict);
            invoice.HasOne(i => i.PaidBy)
                .WithMany()
                .HasForeignKey(i => i.PaidByUserId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<CommissionInvoiceLine>(line =>
        {
            line.Property(l => l.KeptBaht).HasPrecision(10, 2);
            line.Property(l => l.Percent).HasPrecision(5, 2);
            line.Property(l => l.AmountBaht).HasPrecision(10, 2);

            // The rule that a booking is billed once, kept by the database rather than by the
            // run being careful (PRD BR-08).
            line.HasIndex(l => l.BookingId).IsUnique();

            line.HasOne(l => l.Invoice)
                .WithMany(i => i.Lines)
                .HasForeignKey(l => l.InvoiceId)
                .OnDelete(DeleteBehavior.Cascade);
            line.HasOne(l => l.Booking)
                .WithMany()
                .HasForeignKey(l => l.BookingId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<PaymentReceipt>(receipt =>
        {
            receipt.Property(r => r.AmountBaht).HasPrecision(10, 2);
            receipt.Property(r => r.Note).HasMaxLength(PaymentReceipt.NoteMaxLength);
            // Counting a venue's day is one query over its own money, by the day it counts in
            // — which is its own day unless that one had already been counted (PRD US-26).
            receipt.HasIndex(r => new { r.VenueId, r.CountsOn });
            receipt.HasIndex(r => r.BookingId);
            receipt.HasIndex(r => r.PackageId);
            receipt.HasIndex(r => r.SaleId);
            receipt.HasOne(r => r.Booking)
                .WithMany()
                .HasForeignKey(r => r.BookingId)
                .OnDelete(DeleteBehavior.Cascade);
            receipt.HasOne(r => r.Package)
                .WithMany()
                .HasForeignKey(r => r.PackageId)
                .OnDelete(DeleteBehavior.Cascade);
            receipt.HasOne(r => r.Sale)
                .WithMany()
                .HasForeignKey(r => r.SaleId)
                .OnDelete(DeleteBehavior.Cascade);
            receipt.HasOne(r => r.Venue)
                .WithMany()
                .HasForeignKey(r => r.VenueId)
                .OnDelete(DeleteBehavior.Cascade);
            receipt.HasOne(r => r.ReceivedBy)
                .WithMany()
                .HasForeignKey(r => r.ReceivedByUserId)
                .OnDelete(DeleteBehavior.Restrict);
            // Money that came in is money that came in: nothing below zero, nothing free.
            receipt.ToTable(table => table.HasCheckConstraint(
                "CK_PaymentReceipts_AmountIsMoney", "\"AmountBaht\" > 0"));

            // And it came in for exactly one thing: a booking, a package being sold (US-31), or
            // something bought across the counter (US-32). Money that names none of them is money
            // nobody can account for afterwards, and money that names two would be counted twice
            // by whichever reader asked second.
            receipt.ToTable(table => table.HasCheckConstraint(
                "CK_PaymentReceipts_ForOneThing",
                "(CASE WHEN \"BookingId\" IS NULL THEN 0 ELSE 1 END"
                + " + CASE WHEN \"PackageId\" IS NULL THEN 0 ELSE 1 END"
                + " + CASE WHEN \"SaleId\" IS NULL THEN 0 ELSE 1 END) = 1"));
        });

        builder.Entity<PackageType>(type =>
        {
            type.Property(one => one.Name).HasMaxLength(PackageType.NameMaxLength);
            type.Property(one => one.PriceBaht).HasPrecision(10, 2);

            // What is on the board now, which is every read this table has.
            type.HasIndex(one => new { one.VenueId, one.WithdrawnAt, one.CreatedAt });

            // The same bounds the rules keep, kept again where they cannot be got round: hours
            // that are worth nothing, or a price of nothing, would divide by nothing later.
            type.ToTable(table => table.HasCheckConstraint(
                "CK_PackageTypes_IsAnOffer",
                $"\"Hours\" > 0 AND \"Hours\" <= {Packages.MostHours}"
                + " AND \"PriceBaht\" > 0"
                + $" AND \"ValidForDays\" > 0 AND \"ValidForDays\" <= {Packages.MostDays}"));

            type.HasOne(one => one.Venue)
                .WithMany()
                .HasForeignKey(one => one.VenueId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<HourPackage>(package =>
        {
            package.Property(one => one.CustomerName).HasMaxLength(Booking.CustomerNameMaxLength);
            package.Property(one => one.CustomerPhone).HasMaxLength(Booking.CustomerPhoneMaxLength);
            package.Property(one => one.PriceBaht).HasPrecision(10, 2);

            // "What has this venue sold, newest first" and "whose hours run out soon" are the two
            // questions asked of this table.
            package.HasIndex(one => new { one.VenueId, one.SoldAt });
            package.HasIndex(one => new { one.VenueId, one.ExpiresOn, one.ExpiredAt });

            package.ToTable(table => table.HasCheckConstraint(
                "CK_HourPackages_WasSold",
                "\"HoursSold\" > 0 AND \"PriceBaht\" > 0"));

            package.HasOne(one => one.Venue)
                .WithMany()
                .HasForeignKey(one => one.VenueId)
                .OnDelete(DeleteBehavior.Cascade);

            // The offer it was sold from stays readable for as long as the package does, the same
            // way a booking keeps the cancellation terms it was made under (BR-05).
            package.HasOne(one => one.Type)
                .WithMany()
                .HasForeignKey(one => one.PackageTypeId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<PackageEntry>(entry =>
        {
            // Reading a package is reading its movements in order; the balance is their sum.
            entry.HasIndex(one => new { one.PackageId, one.At });

            // Nothing moves by nothing: a row of zero hours is a row that says nothing happened.
            entry.ToTable(table => table.HasCheckConstraint(
                "CK_PackageEntries_HoursMoved", "\"Hours\" <> 0"));

            entry.HasOne(one => one.Package)
                .WithMany(one => one.Entries)
                .HasForeignKey(one => one.PackageId)
                .OnDelete(DeleteBehavior.Cascade);

            entry.HasOne(one => one.Booking)
                .WithMany()
                .HasForeignKey(one => one.BookingId)
                .OnDelete(DeleteBehavior.Restrict);

            entry.HasOne(one => one.By)
                .WithMany()
                .HasForeignKey(one => one.ByUserId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<ShopItem>(item =>
        {
            item.Property(one => one.Name).HasMaxLength(ShopItem.NameMaxLength);
            item.Property(one => one.Unit).HasMaxLength(ShopItem.UnitMaxLength);
            item.Property(one => one.PriceBaht).HasPrecision(10, 2);

            // What is on the board now, which is every read this table has.
            item.HasIndex(one => new { one.VenueId, one.WithdrawnAt, one.Name });

            item.ToTable(table => table.HasCheckConstraint(
                "CK_ShopItems_IsSomethingToSell",
                "\"PriceBaht\" > 0 AND (\"TellMeAt\" IS NULL OR \"TellMeAt\" >= 0)"));

            item.HasOne(one => one.Venue)
                .WithMany()
                .HasForeignKey(one => one.VenueId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<ShopSale>(sale =>
        {
            sale.Property(one => one.TotalBaht).HasPrecision(10, 2);
            sale.Property(one => one.CancelReason).HasMaxLength(BookingStatusChange.ReasonMaxLength);

            // "What did this venue sell today" and "what did this booking buy" are the two
            // questions asked of it.
            sale.HasIndex(one => new { one.VenueId, one.SoldAt });
            sale.HasIndex(one => one.BookingId);

            sale.ToTable(table => table.HasCheckConstraint(
                "CK_ShopSales_CameToSomething", "\"TotalBaht\" > 0"));

            sale.HasOne(one => one.Venue)
                .WithMany()
                .HasForeignKey(one => one.VenueId)
                .OnDelete(DeleteBehavior.Cascade);

            // Restrict, not cascade: a booking is never deleted, and losing what somebody bought
            // beside it would lose money that was taken.
            sale.HasOne(one => one.Booking)
                .WithMany()
                .HasForeignKey(one => one.BookingId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<ShopSaleLine>(line =>
        {
            line.Property(one => one.Name).HasMaxLength(ShopItem.NameMaxLength);
            line.Property(one => one.EachBaht).HasPrecision(10, 2);
            line.Ignore(one => one.Baht);

            line.ToTable(table => table.HasCheckConstraint(
                "CK_ShopSaleLines_IsALine",
                $"\"Quantity\" > 0 AND \"Quantity\" <= {Shop.MostOfOneThing}"
                + " AND \"EachBaht\" > 0"));

            line.HasOne(one => one.Sale)
                .WithMany(one => one.Lines)
                .HasForeignKey(one => one.SaleId)
                .OnDelete(DeleteBehavior.Cascade);

            // The line keeps the name it was sold under, so the item it points at is only for
            // counting stock — and it stays readable for as long as the sale does.
            line.HasOne(one => one.Item)
                .WithMany()
                .HasForeignKey(one => one.ItemId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<StockEntry>(entry =>
        {
            entry.Property(one => one.Reason).HasMaxLength(StockEntry.ReasonMaxLength);

            // Reading an item's stock is reading its movements in order; the balance is their sum.
            entry.HasIndex(one => new { one.ItemId, one.At });

            entry.ToTable(table => table.HasCheckConstraint(
                "CK_StockEntries_SomethingMoved", "\"Quantity\" <> 0"));

            entry.HasOne(one => one.Item)
                .WithMany()
                .HasForeignKey(one => one.ItemId)
                .OnDelete(DeleteBehavior.Cascade);

            entry.HasOne(one => one.Sale)
                .WithMany()
                .HasForeignKey(one => one.SaleId)
                .OnDelete(DeleteBehavior.Restrict);

            // The delivery it arrived on, the same way a sale is the sale it went out on: the row
            // that says the stock came in and the row that says what it cost are one purchase, and
            // an id with nothing holding it to the other end is a note, not a link.
            entry.HasOne(one => one.Spend)
                .WithMany()
                .HasForeignKey(one => one.SpendId)
                .OnDelete(DeleteBehavior.Restrict);

            entry.HasOne(one => one.By)
                .WithMany()
                .HasForeignKey(one => one.ByUserId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<Spend>(spend =>
        {
            spend.Property(one => one.AmountBaht).HasPrecision(10, 2);
            spend.Property(one => one.Note).HasMaxLength(Spend.NoteMaxLength);
            spend.Property(one => one.VoidReason).HasMaxLength(Spend.NoteMaxLength);

            // "What did this venue pay out, and when" is the whole of what is asked of it —
            // asked twice, because a report reads the date the venue says the money was paid and
            // the drawer reads the moment the notes left it, and they are not the same day.
            spend.HasIndex(one => new { one.VenueId, one.PaidOn });
            spend.HasIndex(one => new { one.VenueId, one.RecordedAt });

            spend.ToTable(table => table.HasCheckConstraint(
                "CK_Spends_IsMoney", "\"AmountBaht\" > 0"));

            spend.HasOne(one => one.Venue)
                .WithMany()
                .HasForeignKey(one => one.VenueId)
                .OnDelete(DeleteBehavior.Cascade);

            spend.HasOne(one => one.RecordedBy)
                .WithMany()
                .HasForeignKey(one => one.RecordedByUserId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<DailyClosing>(closing =>
        {
            closing.Property(c => c.OpeningFloatBaht).HasPrecision(10, 2);
            closing.Property(c => c.ExpectedCashBaht).HasPrecision(10, 2);
            closing.Property(c => c.CountedCashBaht).HasPrecision(10, 2);
            closing.Property(c => c.DifferenceBaht).HasPrecision(10, 2);
            closing.Property(c => c.Note).HasMaxLength(DailyClosing.NoteMaxLength);
            // A day is closed once, though a shift may be counted before it (thai-fit T2). The
            // database is what makes two people closing the day at the same time into one close.
            closing.HasIndex(c => new { c.VenueId, c.Date })
                .IsUnique()
                .HasFilter("\"EndsDay\"")
                .HasDatabaseName("IX_DailyClosings_VenueId_Date_EndsDay");
            closing.HasIndex(c => new { c.VenueId, c.Date, c.ClosedAt });
            closing.HasOne(c => c.Venue)
                .WithMany()
                .HasForeignKey(c => c.VenueId)
                .OnDelete(DeleteBehavior.Cascade);
            closing.HasOne(c => c.ClosedBy)
                .WithMany()
                .HasForeignKey(c => c.ClosedByUserId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<Complaint>(complaint =>
        {
            complaint.Property(c => c.Details).HasMaxLength(Complaint.DetailsMaxLength);
            complaint.Property(c => c.Resolution).HasMaxLength(Complaint.ResolutionMaxLength);

            // Resolved means resolved by somebody, at a time, with what was done (PRD US-22).
            complaint.ToTable(table => table.HasCheckConstraint(
                "CK_Complaints_ResolvedSaysHow",
                "(\"Status\" = 1 AND \"ResolvedAt\" IS NULL AND \"Resolution\" IS NULL)"
                + " OR (\"Status\" = 2 AND \"ResolvedAt\" IS NOT NULL"
                + " AND \"ResolvedByUserId\" IS NOT NULL AND \"Resolution\" IS NOT NULL)"));

            // The queue: open first, newest first.
            complaint.HasIndex(c => new { c.Status, c.OpenedAt });
            complaint.HasIndex(c => c.BookingId);

            // Nothing takes a complaint with it: it is the record of a dispute (PRD 8).
            complaint.HasOne(c => c.Booking)
                .WithMany()
                .HasForeignKey(c => c.BookingId)
                .OnDelete(DeleteBehavior.Restrict);
            complaint.HasOne(c => c.OpenedBy)
                .WithMany()
                .HasForeignKey(c => c.OpenedByUserId)
                .OnDelete(DeleteBehavior.Restrict);
            complaint.HasOne(c => c.ResolvedBy)
                .WithMany()
                .HasForeignKey(c => c.ResolvedByUserId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<MembershipChange>(change =>
        {
            change.Property(c => c.RefundLimitBefore).HasPrecision(10, 2);
            change.Property(c => c.RefundLimitAfter).HasPrecision(10, 2);
            // A venue's roster over time, read one venue at a time (PRD 8, US-14).
            change.HasIndex(c => new { c.VenueId, c.ChangedAt });
            change.HasOne(c => c.Venue)
                .WithMany()
                .HasForeignKey(c => c.VenueId)
                .OnDelete(DeleteBehavior.Restrict);
            change.HasOne(c => c.User)
                .WithMany()
                .HasForeignKey(c => c.UserId)
                .OnDelete(DeleteBehavior.Restrict);
            change.HasOne(c => c.ChangedBy)
                .WithMany()
                .HasForeignKey(c => c.ChangedByUserId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<SlipViewing>(viewing =>
        {
            viewing.HasIndex(v => new { v.ComplaintId, v.ViewedAt });
            viewing.HasOne(v => v.Complaint)
                .WithMany()
                .HasForeignKey(v => v.ComplaintId)
                .OnDelete(DeleteBehavior.Restrict);
            viewing.HasOne(v => v.Slip)
                .WithMany()
                .HasForeignKey(v => v.SlipId)
                .OnDelete(DeleteBehavior.Restrict);
            viewing.HasOne(v => v.ViewedBy)
                .WithMany()
                .HasForeignKey(v => v.ViewedByUserId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<BookerNotice>(notice =>
        {
            // The claim: one message per thing it is about, whoever tries to send it (PRD US-06).
            notice.HasIndex(n => new { n.SourceId, n.Kind }).IsUnique();
            notice.Property(n => n.SentBy).HasConversion<int?>();
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
