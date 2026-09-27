using System.Net;
using System.Net.Http.Json;
using CourtBooking.Api.Bookings;
using CourtBooking.Api.Data;
using CourtBooking.Api.Localization;
using CourtBooking.Api.Tests.Infrastructure;
using CourtBooking.Api.Venues;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CourtBooking.Api.Tests;

/// <summary>A booking taken at the counter for somebody standing at it: PRD US-13, US-20, BR-04.</summary>
public sealed class CounterBookingTests(ApiTestFixture api) : IClassFixture<ApiTestFixture>
{
    private readonly VenueScenario scenario = new(api);

    private static readonly DateOnly Tomorrow = VenueScenario.Today.AddDays(1);

    [Fact]
    public async Task A_counter_booking_starts_confirmed_with_the_money_received()
    {
        var (owner, venue, courts) = await scenario.BookableVenueAsync();

        var taken = await VenueScenario.ReadAsync<VenueBookingResponse>(
            await TakeAsync(owner, venue.Id, courts[0], 18, "คุณสมชาย", "081-234-5678", "Cash"),
            HttpStatusCode.Created);

        // Both have already happened at the counter, so there is no hold to wait on and no slip
        // to check (PRD US-13).
        Assert.Equal(nameof(BookingStatus.Confirmed), taken.Status);
        Assert.Equal(nameof(PaymentState.Received), taken.PaymentState);
        Assert.Equal(nameof(BookingChannel.Staff), taken.Channel);
        Assert.Equal(BookingKinds.WalkIn, taken.Kind);
        Assert.Equal("คุณสมชาย", taken.CustomerName);
        Assert.Equal("081-234-5678", taken.CustomerPhone);

        // And it holds no account: the customer does not need one.
        Assert.Null(taken.BookerEmail);
        Assert.Null((await StoredAsync(taken.BookingId)).BookerUserId);
    }

    /// <summary>
    /// The history says who took it. A counter booking has no booker, so the staff member on the
    /// first row is the only answer to "who made this" (PRD 6.1).
    /// </summary>
    [Fact]
    public async Task The_history_says_which_member_of_staff_took_it()
    {
        var (owner, venue, courts) = await scenario.BookableVenueAsync();
        var (staff, staffEmail) = await scenario.SignedInClientWithEmailAsync();
        await scenario.InviteAndAcceptAsync(
            owner, staff, venue.Id, staffEmail, [nameof(VenuePermissions.ManageBookings)]);

        var taken = await VenueScenario.ReadAsync<VenueBookingResponse>(
            await TakeAsync(staff, venue.Id, courts[0], 18, "คุณสมชาย", null, "Transfer"),
            HttpStatusCode.Created);

        var created = Assert.Single(await scenario.HistoryAsync(taken.BookingId));
        Assert.Null(created.From);
        Assert.Equal(BookingStatus.Confirmed, created.To);

        var members = await scenario.MemberEmailsAsync(venue.Id);
        Assert.Equal(staffEmail, members[created.ChangedByUserId!.Value]);
    }

    /// <summary>
    /// The counter and the booker reach for the same hours through the same constraint, so an
    /// hour sold at the counter is gone for everybody (PRD BR-04).
    /// </summary>
    [Fact]
    public async Task An_hour_sold_at_the_counter_cannot_then_be_booked_online()
    {
        var (owner, venue, courts) = await scenario.BookableVenueAsync();
        await TakeAsync(owner, venue.Id, courts[0], 18, "คุณสมชาย", null, "Cash");

        var booker = await scenario.SignedInClientAsync();
        var refused = await booker.PostAsJsonAsync(
            "/api/bookings",
            new CreateBookingRequest(venue.Id, [new BookingSlotRequest(courts[0], Tomorrow, 18)]));

        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
    }

    [Fact]
    public async Task And_an_hour_already_held_online_cannot_be_sold_at_the_counter()
    {
        var (owner, venue, courts) = await scenario.BookableVenueAsync();
        var booker = await scenario.SignedInClientAsync();
        await VenueScenario.HoldAsync(booker, venue.Id, Tomorrow, (courts[0], 18));

        var refused = await TakeAsync(owner, venue.Id, courts[0], 18, "คุณสมชาย", null, "Cash");

        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
    }

    [Fact]
    public async Task A_counter_booking_can_be_cancelled_like_any_other()
    {
        var (owner, venue, courts) = await scenario.BookableVenueAsync();
        var taken = await VenueScenario.ReadAsync<VenueBookingResponse>(
            await TakeAsync(owner, venue.Id, courts[0], 18, "คุณสมชาย", null, "Cash"),
            HttpStatusCode.Created);

        var cancelled = await owner.PostAsJsonAsync(
            $"/api/venues/{venue.Id}/bookings/{taken.BookingId}/cancel",
            new VenueCancelRequest(nameof(CancellationReason.CustomerRequest), null, null));

        Assert.Equal(HttpStatusCode.OK, cancelled.StatusCode);
    }

    // ---- What the counter has to say ----

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task A_customer_with_no_name_is_refused(string? name)
    {
        var (owner, venue, courts) = await scenario.BookableVenueAsync();

        var refused = await TakeAsync(owner, venue.Id, courts[0], 18, name, null, "Cash");

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Equal(BookingErrorCodes.InvalidCustomerName, await refused.ErrorCodeAsync());
    }

    [Theory]
    [InlineData("call me maybe")]
    [InlineData("0812345678901234567890")]
    public async Task A_phone_that_is_not_a_phone_is_refused(string phone)
    {
        var (owner, venue, courts) = await scenario.BookableVenueAsync();

        var refused = await TakeAsync(owner, venue.Id, courts[0], 18, "คุณสมชาย", phone, "Cash");

        Assert.Equal(BookingErrorCodes.InvalidCustomerPhone, await refused.ErrorCodeAsync());
    }

    /// <summary>
    /// The names and nothing else. Enum.TryParse would take "1" and would OR "Cash,Transfer"
    /// into a third value, and the record would keep whichever one was sent.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("1")]
    [InlineData("cash")]
    [InlineData("Cash,Transfer")]
    [InlineData("Cheque")]
    public async Task A_way_of_paying_that_is_not_one_is_refused(string? paidBy)
    {
        var (owner, venue, courts) = await scenario.BookableVenueAsync();

        var refused = await TakeAsync(owner, venue.Id, courts[0], 18, "คุณสมชาย", null, paidBy);

        Assert.Equal(BookingErrorCodes.InvalidCounterPayment, await refused.ErrorCodeAsync());
    }

    // ---- Who may, and where ----

    [Fact]
    public async Task Staff_who_only_check_slips_cannot_sell_at_the_counter()
    {
        var (owner, venue, courts) = await scenario.BookableVenueAsync();
        var checker = await scenario.StaffClientAsync(
            owner, venue.Id, nameof(VenuePermissions.VerifySlip));

        var refused = await TakeAsync(checker, venue.Id, courts[0], 18, "คุณสมชาย", null, "Cash");

        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
    }

    /// <summary>
    /// A suspension stops a venue selling, at the counter as much as online (PRD US-20) — while
    /// the same group of endpoints stays open to it for what it already sold. Two things refuse
    /// this (the route's policy and the handler's approval check); the test is about the
    /// behaviour, and passes with either one removed, which is the point of having both.
    /// </summary>
    [Fact]
    public async Task A_suspended_venue_cannot_sell_at_the_counter_either()
    {
        var (owner, venue, courts) = await scenario.BookableVenueAsync();
        await scenario.SetStatusAsync(venue.Id, VenueStatus.Suspended);

        var refused = await TakeAsync(owner, venue.Id, courts[0], 18, "คุณสมชาย", null, "Cash");
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);

        // The day itself is still readable: the counter has old bookings to deal with.
        var day = await owner.GetAsync(
            $"/api/venues/{venue.Id}/bookings?date={Tomorrow:yyyy-MM-dd}");
        Assert.Equal(HttpStatusCode.OK, day.StatusCode);
    }

    [Fact]
    public async Task Nor_can_a_venue_still_waiting_to_be_approved()
    {
        var (owner, venue, courts) = await scenario.BookableVenueAsync();
        await scenario.SetStatusAsync(venue.Id, VenueStatus.Pending);

        var refused = await TakeAsync(owner, venue.Id, courts[0], 18, "คุณสมชาย", null, "Cash");

        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Equal(VenueErrorCodes.NotApproved, await refused.ErrorCodeAsync());
    }

    [Fact]
    public async Task Another_venue_s_court_cannot_be_sold_here()
    {
        var (owner, venue, _) = await scenario.BookableVenueAsync();
        var (_, _, elsewhere) = await scenario.BookableVenueAsync();

        var refused = await TakeAsync(owner, venue.Id, elsewhere[0], 18, "คุณสมชาย", null, "Cash");

        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
    }

    // ---- The two shapes a booking can take, held apart by the database ----

    /// <summary>
    /// A booking with neither a booker nor a customer is a booking nobody can be told about or
    /// refunded, so the database refuses one however it is written (PRD US-13).
    /// </summary>
    [Fact]
    public async Task The_database_refuses_a_counter_booking_with_no_customer()
    {
        var (_, venue, courts) = await scenario.BookableVenueAsync();

        using var scope = api.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var policyId = await database.CancellationPolicies
            .Where(policy => policy.VenueId == venue.Id)
            .Select(policy => policy.Id)
            .FirstAsync();

        var now = DateTimeOffset.UtcNow;
        database.Bookings.Add(new Booking
        {
            VenueId = venue.Id,
            Channel = BookingChannel.Staff,
            Status = BookingStatus.Confirmed,
            CreatedAt = now,
            HoldExpiresAt = now,
            TotalBaht = 200m,
            DepositBaht = 200m,
            DepositReason = DepositReason.VenueTerms,
            CancellationPolicyId = policyId,
        });

        var refused = await Assert.ThrowsAsync<DbUpdateException>(
            () => database.SaveChangesAsync());
        Assert.Contains("CK_Bookings_BookerOrCustomer", refused.InnerException?.Message);

        Assert.NotNull(courts);
    }

    // ---- The clock, which is the one thing the counter does differently ----

    /// <summary>
    /// Somebody standing at the counter can walk onto a court whose hour has already started, as
    /// long as it has not ended (PRD US-13). Tested against the rule itself with a clock it is
    /// handed, so the answer does not depend on what time the suite happens to run.
    /// </summary>
    [Fact]
    public void The_counter_may_sell_an_hour_that_has_started()
    {
        var date = VenueScenario.Today.AddDays(1);
        var tenPast = PlatformRequirements.BangkokHour(date, 18).AddMinutes(10);

        Assert.Null(BookingValidation.Validate(
            [new BookingSlotRequest(Guid.NewGuid(), date, 18)],
            tenPast,
            date,
            BookingChannel.Staff));
    }

    [Fact]
    public void But_not_one_that_is_already_over()
    {
        var date = VenueScenario.Today.AddDays(1);
        var afterwards = PlatformRequirements.BangkokHour(date, 19).AddMinutes(5);

        Assert.Equal(
            BookingErrorCodes.HourAlreadyOver,
            BookingValidation.Validate(
                [new BookingSlotRequest(Guid.NewGuid(), date, 18)],
                afterwards,
                date,
                BookingChannel.Staff));
    }

    /// <summary>Online keeps its half-hour lead, because a booker has to get there (PRD S-25).</summary>
    [Fact]
    public void Online_still_needs_the_lead_time()
    {
        var date = VenueScenario.Today.AddDays(1);
        var tenBefore = PlatformRequirements.BangkokHour(date, 18).AddMinutes(-10);

        Assert.Equal(
            BookingErrorCodes.StartsTooSoon,
            BookingValidation.Validate(
                [new BookingSlotRequest(Guid.NewGuid(), date, 18)],
                tenBefore,
                date,
                BookingChannel.Online));
    }

    private static Task<HttpResponseMessage> TakeAsync(
        HttpClient client,
        Guid venueId,
        Guid courtId,
        int hour,
        string? name,
        string? phone,
        string? paidBy) =>
        client.PostAsJsonAsync(
            $"/api/venues/{venueId}/bookings",
            new CounterBookingRequest(
                [new BookingSlotRequest(courtId, Tomorrow, hour)], name, phone, paidBy));

    private async Task<Booking> StoredAsync(Guid bookingId)
    {
        using var scope = api.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await database.Bookings.AsNoTracking().SingleAsync(one => one.Id == bookingId);
    }
}
