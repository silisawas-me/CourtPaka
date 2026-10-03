using System.Net.Http.Json;
using CourtBooking.Api.Bookings;
using CourtBooking.Api.Data;
using CourtBooking.Api.Jobs;
using CourtBooking.Api.Localization;
using CourtBooking.Api.Tests.Infrastructure;
using CourtBooking.Api.Venues;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CourtBooking.Api.Tests;

/// <summary>
/// The month the platform bills for (PRD US-21, BR-08). What is counted, what is not, and what
/// happens when the same month is billed twice.
/// </summary>
public sealed class CommissionRunTests(ApiTestFixture api) : IClassFixture<ApiTestFixture>
{
    private readonly VenueScenario scenario = new(api);

    /// <summary>
    /// The cut-off is an instant, not "whenever the job woke up": the 2nd of the following month
    /// at two in the morning, the venue's time (PRD BR-08, BR-10).
    /// </summary>
    [Fact]
    public void The_cut_off_is_the_second_at_two_in_the_morning()
    {
        var cutOff = CommissionRun.CutOff(new DateOnly(2027, 1, 1));

        var (date, hour) = PlatformRequirements.BangkokDateAndHour(cutOff);
        Assert.Equal(new DateOnly(2027, 2, 2), date);
        Assert.Equal(2, hour);

        // And December's lands in January, not in month thirteen.
        var (turned, _) = PlatformRequirements.BangkokDateAndHour(
            CommissionRun.CutOff(new DateOnly(2027, 12, 1)));
        Assert.Equal(new DateOnly(2028, 1, 2), turned);
    }

    /// <summary>Issued on the 2nd, due on the 16th (PRD BR-09).</summary>
    [Fact]
    public void It_is_due_on_the_sixteenth()
    {
        Assert.Equal(new DateOnly(2027, 2, 16), CommissionRun.DueOn(new DateOnly(2027, 1, 1)));
    }

    /// <summary>
    /// What "settled" means, taken from PRD 6.2 one status at a time. It is what decides whether
    /// a month may be billed for a booking, so it is worth stating rather than trusting.
    /// </summary>
    [Theory]
    // Nothing left to decide the moment they end.
    [InlineData(BookingStatus.Expired, PaymentState.NotReceived, 0, true)]
    [InlineData(BookingStatus.Rejected, PaymentState.Received, 0, true)]
    // Cancelled waits on the venue answering whether the money arrived.
    [InlineData(BookingStatus.Cancelled, PaymentState.Unconfirmed, 0, false)]
    [InlineData(BookingStatus.Cancelled, PaymentState.Received, 0, true)]
    [InlineData(BookingStatus.Cancelled, PaymentState.NotReceived, 0, true)]
    // Played bookings wait a day, which is the window the venue may correct them in.
    [InlineData(BookingStatus.Completed, PaymentState.Received, 23, false)]
    [InlineData(BookingStatus.Completed, PaymentState.Received, 24, true)]
    [InlineData(BookingStatus.NoShow, PaymentState.Received, 25, true)]
    // Still live, so nothing about them is final.
    [InlineData(BookingStatus.Held, PaymentState.NotReceived, 99, false)]
    [InlineData(BookingStatus.Confirmed, PaymentState.Received, 99, false)]
    [InlineData(BookingStatus.PendingVerification, PaymentState.NotReceived, 99, false)]
    public void What_counts_as_settled(
        BookingStatus status, PaymentState payment, int hoursAfter, bool expected)
    {
        var ended = new DateTimeOffset(2027, 1, 10, 20, 0, 0, TimeSpan.Zero);

        Assert.Equal(
            expected,
            Settled.By(status, payment, ended, ended.AddHours(hoursAfter)));
    }

    /// <summary>
    /// A booking played in the month, settled by the cut-off and never billed, is what a month
    /// is made of — charged the rate in force on the day it was played (PRD BR-08).
    /// </summary>
    [Fact]
    public async Task A_month_bills_what_was_played_in_it()
    {
        var (owner, venue, courts) = await scenario.BookableVenueAsync(baht: 400m);
        var admin = await scenario.PlatformAdminAsync();
        await SetRateAsync(admin, venue.Id, 10m);

        var (_, booking) = await scenario.ConfirmedBookingAsync(owner, venue.Id, courts[0], 18);
        var month = await PlayedAndSettledAsync(booking.Id);

        var billable = await BillableAsync(venue.Id, month);

        var only = Assert.Single(billable);
        Assert.Equal(booking.Id, only.BookingId);
        Assert.Equal(400m, only.KeptBaht);

        var invoice = await BillAsync(venue.Id, month);
        Assert.NotNull(invoice);
        Assert.Equal(40m, invoice.AmountBaht);
        Assert.Equal(10m, Assert.Single(invoice.Lines).Percent);
        Assert.StartsWith("PLT-INV-", invoice.Number);
    }

    /// <summary>
    /// The third condition of BR-08, and the one that makes the run safe to repeat: a booking
    /// already on an invoice is never billed again, however many times the job runs.
    /// </summary>
    [Fact]
    public async Task A_booking_is_billed_once_however_often_the_run_happens()
    {
        var (owner, venue, courts) = await scenario.BookableVenueAsync(baht: 400m);
        var admin = await scenario.PlatformAdminAsync();
        await SetRateAsync(admin, venue.Id, 10m);

        var (_, booking) = await scenario.ConfirmedBookingAsync(owner, venue.Id, courts[0], 18);
        var month = await PlayedAndSettledAsync(booking.Id);

        Assert.NotNull(await BillAsync(venue.Id, month));

        // Nothing left to bill, so nothing is billed — not a second invoice for the same money.
        Assert.Empty(await BillableAsync(venue.Id, month));
        Assert.Null(await BillAsync(venue.Id, month));
    }

    /// <summary>
    /// A venue the platform has never agreed a rate with is not charged nought — it is not
    /// charged at all, and the booking waits for a rate (PRD US-21).
    /// </summary>
    [Fact]
    public async Task A_venue_with_no_rate_agreed_is_not_billed()
    {
        var (owner, venue, courts) = await scenario.BookableVenueAsync(baht: 400m);
        var (_, booking) = await scenario.ConfirmedBookingAsync(owner, venue.Id, courts[0], 18);
        var month = await PlayedAndSettledAsync(booking.Id);

        // There is something to bill for; there is just no rate to bill it at.
        Assert.Single(await BillableAsync(venue.Id, month));
        Assert.Null(await BillAsync(venue.Id, month));
    }

    /// <summary>The platform charges no commission on what the counter sold (PRD BR-08, S-13).</summary>
    [Fact]
    public async Task What_the_counter_sold_is_not_billed()
    {
        // Two courts, because both bookings are moved back to the same instant below and one
        // court cannot hold two at once (PRD BR-04).
        var (owner, venue, courts) = await scenario.BookableVenueAsync(courts: 2, baht: 400m);
        var admin = await scenario.PlatformAdminAsync();
        await SetRateAsync(admin, venue.Id, 10m);

        var taken = await VenueScenario.ReadAsync<VenueBookingResponse>(
            await owner.PostAsJsonAsync(
                $"/api/venues/{venue.Id}/bookings",
                new CounterBookingRequest(
                    [new BookingSlotRequest(courts[0], VenueScenario.Today.AddDays(1), 19)],
                    "คุณเอ",
                    null,
                    nameof(CounterPayment.Cash))),
            System.Net.HttpStatusCode.Created);

        var month = await PlayedAndSettledAsync(taken.BookingId);

        // An online booking of the same evening, so an empty answer means the channel was what
        // excluded it and not that the setup failed to settle anything at all.
        var (_, online) = await scenario.ConfirmedBookingAsync(owner, venue.Id, courts[1], 18);
        await PlayedAndSettledAsync(online.Id);

        var billable = await BillableAsync(venue.Id, month);

        Assert.Equal(online.Id, Assert.Single(billable).BookingId);
    }

    /// <summary>
    /// A booking that settled after its own month was billed is carried into the next one rather
    /// than lost — which is what BR-08's first condition, "played before the month was out",
    /// allows for.
    /// </summary>
    [Fact]
    public async Task Something_that_settled_late_is_carried_into_the_next_month()
    {
        var (owner, venue, courts) = await scenario.BookableVenueAsync(baht: 400m);
        var admin = await scenario.PlatformAdminAsync();
        await SetRateAsync(admin, venue.Id, 10m);

        var (_, booking) = await scenario.ConfirmedBookingAsync(owner, venue.Id, courts[0], 18);
        var month = await PlayedAndSettledAsync(booking.Id);

        // The month after the one it was played in still finds it, because nothing billed it.
        var next = month.AddMonths(1);
        var carried = await BillableAsync(venue.Id, next);

        var only = Assert.Single(carried);
        Assert.Equal(booking.Id, only.BookingId);

        // Still charged against the month it was played in, not the one billing it.
        Assert.Equal(month, new DateOnly(only.ServedOn.Year, only.ServedOn.Month, 1));
        Assert.NotEqual(next, month);
    }

    /// <summary>
    /// The job asks which months are due rather than trusting the clock it woke up on, so a
    /// platform that was switched off over the turn of the month catches up (PRD US-21).
    /// </summary>
    [Fact]
    public void The_job_catches_up_on_months_nobody_billed()
    {
        // The third of March: February's cut-off has passed, March's has not.
        var now = PlatformRequirements.BangkokHour(new DateOnly(2027, 3, 3), 9);

        var due = MonthlyBilling.DueMonths(now).ToArray();

        Assert.Contains(new DateOnly(2027, 2, 1), due);
        Assert.Contains(new DateOnly(2027, 1, 1), due);
        Assert.DoesNotContain(new DateOnly(2027, 3, 1), due);

        // On the first of March, February's cut-off has not arrived yet.
        var early = MonthlyBilling.DueMonths(
            PlatformRequirements.BangkokHour(new DateOnly(2027, 3, 1), 9)).ToArray();
        Assert.DoesNotContain(new DateOnly(2027, 2, 1), early);
    }

    /// <summary>
    /// A rate the platform agreed long ago. The endpoint will not take a date in the past — that
    /// is the point of it — so a test that needs a rate already in force when a booking was
    /// played writes it the way history got there.
    /// </summary>
    private async Task SetRateAsync(HttpClient admin, Guid venueId, decimal percent)
    {
        using var scope = api.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var who = await database.Users.FirstAsync();

        database.CommissionRates.Add(new CommissionRate
        {
            VenueId = venueId,
            Percent = percent,
            EffectiveFrom = VenueScenario.Today.AddYears(-2),
            SetByUserId = who.Id,
            SetAt = DateTimeOffset.UtcNow.AddYears(-2),
        });

        await database.SaveChangesAsync();
    }

    /// <summary>
    /// Puts a booking's hours in the past and lets the clock settle it, then answers the month
    /// it was played in. The run reads the world; this is how the world gets that way.
    /// </summary>
    private async Task<DateOnly> PlayedAndSettledAsync(Guid bookingId)
    {
        await scenario.StartsInAsync(bookingId, TimeSpan.FromDays(-40));

        using var scope = api.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var first = await database.BookingSlots
            .Where(slot => slot.BookingId == bookingId)
            .MinAsync(slot => slot.StartsAt);

        var day = PlatformRequirements.BangkokDateAndHour(first).Date;
        return new DateOnly(day.Year, day.Month, 1);
    }

    private async Task<List<Billable>> BillableAsync(Guid venueId, DateOnly month)
    {
        using var scope = api.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await CommissionRun.BillableAsync(
            database, venueId, month, CancellationToken.None);
    }

    private async Task<CommissionInvoice?> BillAsync(Guid venueId, DateOnly month)
    {
        using var scope = api.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var venue = await database.Venues.SingleAsync(one => one.Id == venueId);

        await using var transaction = await database.Database.BeginTransactionAsync();
        var invoice = await CommissionRun.BillAsync(
            database, venue, month, DateTimeOffset.UtcNow, CancellationToken.None);

        if (invoice is null)
        {
            await transaction.RollbackAsync();
            return null;
        }

        await database.SaveChangesAsync();
        await transaction.CommitAsync();
        return invoice;
    }
}
