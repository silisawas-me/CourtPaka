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

/// <summary>
/// How much a venue asks of somebody by what happened last time (PRD US-28). The rule only ever
/// asks for more: a venue's own terms are the floor, which is what makes it safe left on.
/// </summary>
public sealed class DepositRiskTests(ApiTestFixture api) : IClassFixture<ApiTestFixture>
{
    private readonly VenueScenario scenario = new(api);

    private static VenueRiskRule Rule(
        bool on = true,
        int halfAt = 2,
        int fullAt = 3,
        int? from = 18,
        int? until = 22) =>
        new()
        {
            On = on,
            LookbackDays = DepositRisk.DefaultLookbackDays,
            HalfAt = halfAt,
            FullAt = fullAt,
            PeakFromHour = from,
            PeakUntilHour = until,
        };

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void Somebody_who_turns_up_is_asked_what_everybody_is_asked(int misses) =>
        Assert.Equal(
            (25, DepositReason.VenueTerms),
            DepositRisk.Asks(Rule(), venuePercent: 25, misses, atPeak: true));

    [Fact]
    public void Two_misses_puts_the_floor_at_half() =>
        Assert.Equal(
            (50, DepositReason.SomeNoShows),
            DepositRisk.Asks(Rule(), venuePercent: 25, noShows: 2, atPeak: false));

    /// <summary>
    /// The rule raises a floor; it never lowers one a venue has set for itself — and where the
    /// floor is already higher, nothing was raised, so nobody is told they were asked for more.
    /// A venue asking for the whole price is every venue until one says otherwise.
    /// </summary>
    [Theory]
    [InlineData(80, 2, false, 80)]
    [InlineData(Deposit.Everything, 2, false, Deposit.Everything)]
    [InlineData(Deposit.Everything, 5, true, Deposit.Everything)]
    public void Asking_the_ordinary_amount_is_not_asking_for_more(
        int venuePercent,
        int noShows,
        bool atPeak,
        int expected) =>
        Assert.Equal(
            (expected, DepositReason.VenueTerms),
            DepositRisk.Asks(Rule(), venuePercent, noShows, atPeak));

    [Fact]
    public void Three_misses_at_the_hours_the_venue_will_not_lose_is_the_whole_price() =>
        Assert.Equal(
            (Deposit.Everything, DepositReason.ManyNoShowsAtPeak),
            DepositRisk.Asks(Rule(), venuePercent: 25, noShows: 3, atPeak: true));

    /// <summary>Away from those hours the top tier asks what the one below it asks.</summary>
    [Fact]
    public void Three_misses_at_a_quiet_hour_is_half() =>
        Assert.Equal(
            (50, DepositReason.SomeNoShows),
            DepositRisk.Asks(Rule(), venuePercent: 25, noShows: 3, atPeak: false));

    [Fact]
    public void A_venue_that_turns_the_rule_off_asks_everybody_the_same() =>
        Assert.Equal(
            (25, DepositReason.VenueTerms),
            DepositRisk.Asks(Rule(on: false), venuePercent: 25, noShows: 9, atPeak: true));

    /// <summary>A venue that has named no peak has no hour it treats differently.</summary>
    [Fact]
    public void No_peak_named_is_no_hour_at_peak()
    {
        var rule = Rule(from: null, until: null);

        Assert.False(rule.IsPeak(19));
        Assert.Equal(
            (50, DepositReason.SomeNoShows),
            DepositRisk.Asks(rule, venuePercent: 25, noShows: 5, atPeak: false));
    }

    [Theory]
    [InlineData(18, 22, 18, true)]
    [InlineData(18, 22, 21, true)]
    [InlineData(18, 22, 22, false)]
    [InlineData(18, 22, 17, false)]
    public void Peak_is_a_half_open_range(int from, int until, int hour, bool peak) =>
        Assert.Equal(peak, Rule(from: from, until: until).IsPeak(hour));

    [Theory]
    [InlineData(0, 2, 3)]
    [InlineData(366, 2, 3)]
    [InlineData(60, 0, 3)]
    [InlineData(60, 3, 2)]
    // A rule nobody could ever meet is a rule that looks on and is not; turning it off says so.
    [InlineData(60, 2, 51)]
    [InlineData(60, 51, 60)]
    public void Thresholds_that_make_no_sense_are_not_thresholds(
        int lookback,
        int halfAt,
        int fullAt) =>
        Assert.False(DepositRisk.AreThresholds(lookback, halfAt, fullAt));

    [Theory]
    [InlineData(null, 22)]
    [InlineData(18, null)]
    [InlineData(22, 18)]
    [InlineData(18, 25)]
    public void Peak_hours_that_are_not_a_range_are_refused(int? from, int? until) =>
        Assert.False(VenueRiskRule.IsAWindow(from, until));

    /// <summary>
    /// End to end: somebody who has been recorded as not turning up twice is asked for half of
    /// the next booking, and told which rule it was — the page turns the name into a sentence,
    /// because the server does not send people words (PRD US-23).
    /// </summary>
    [Fact]
    public async Task A_booker_who_has_missed_twice_is_asked_for_half()
    {
        var (owner, venue, courts) = await scenario.BookableVenueAsync(courts: 2, baht: 400m);
        await owner.PutAsJsonAsync($"/api/venues/{venue.Id}/deposit", new DepositRequest(25));

        var booker = await scenario.SignedInClientAsync();
        var first = await VenueScenario.HoldAsync(
            booker, venue.Id, VenueScenario.Today.AddDays(1), (courts[0], 9));
        Assert.Equal(100m, first.DepositBaht);
        Assert.Equal(nameof(DepositReason.VenueTerms), first.DepositReason);

        await MissedAsync(first.Id, 2);

        // One hold at a time (PRD S-22), so the first one is let go before the next is taken.
        await booker.PostAsync($"/api/bookings/{first.Id}/cancel", null);

        var second = await VenueScenario.HoldAsync(
            booker, venue.Id, VenueScenario.Today.AddDays(1), (courts[1], 9));

        Assert.Equal(200m, second.DepositBaht);
        Assert.Equal(nameof(DepositReason.SomeNoShows), second.DepositReason);
    }

    /// <summary>
    /// And at the hours the venue said it cannot afford to lose, the whole price — which is the
    /// point of the rule: those are the hours a miss actually costs something (PRD US-28).
    /// </summary>
    [Fact]
    public async Task A_booker_who_has_missed_three_times_pays_for_peak_hours_in_full()
    {
        var (owner, venue, courts) = await scenario.BookableVenueAsync(courts: 2, baht: 400m);
        await owner.PutAsJsonAsync($"/api/venues/{venue.Id}/deposit", new DepositRequest(25));
        await owner.PutAsJsonAsync(
            $"/api/venues/{venue.Id}/risk-rule",
            new RiskRuleRequest(true, 60, 2, 3, 18, 22));

        var booker = await scenario.SignedInClientAsync();
        var first = await VenueScenario.HoldAsync(
            booker, venue.Id, VenueScenario.Today.AddDays(1), (courts[0], 9));
        await MissedAsync(first.Id, 3);
        await booker.PostAsync($"/api/bookings/{first.Id}/cancel", null);

        var peak = await VenueScenario.HoldAsync(
            booker, venue.Id, VenueScenario.Today.AddDays(1), (courts[1], 19));

        Assert.Equal(peak.TotalBaht, peak.DepositBaht);
        Assert.Equal(nameof(DepositReason.ManyNoShowsAtPeak), peak.DepositReason);
    }

    [Fact]
    public async Task A_venue_that_turns_the_rule_off_stops_counting()
    {
        var (owner, venue, courts) = await scenario.BookableVenueAsync(courts: 2, baht: 400m);
        await owner.PutAsJsonAsync($"/api/venues/{venue.Id}/deposit", new DepositRequest(25));
        await owner.PutAsJsonAsync(
            $"/api/venues/{venue.Id}/risk-rule",
            new RiskRuleRequest(false, 60, 2, 3, null, null));

        var booker = await scenario.SignedInClientAsync();
        var first = await VenueScenario.HoldAsync(
            booker, venue.Id, VenueScenario.Today.AddDays(1), (courts[0], 9));
        await MissedAsync(first.Id, 5);
        await booker.PostAsync($"/api/bookings/{first.Id}/cancel", null);

        var second = await VenueScenario.HoldAsync(
            booker, venue.Id, VenueScenario.Today.AddDays(1), (courts[1], 9));

        Assert.Equal(100m, second.DepositBaht);
        Assert.Equal(nameof(DepositReason.VenueTerms), second.DepositReason);
    }

    /// <summary>A miss older than the venue's window is not one it still counts.</summary>
    [Fact]
    public async Task Misses_outside_the_window_are_forgotten()
    {
        var (owner, venue, courts) = await scenario.BookableVenueAsync(courts: 2, baht: 400m);
        await owner.PutAsJsonAsync($"/api/venues/{venue.Id}/deposit", new DepositRequest(25));
        await owner.PutAsJsonAsync(
            $"/api/venues/{venue.Id}/risk-rule",
            new RiskRuleRequest(true, LookbackDays: 7, 2, 3, null, null));

        var booker = await scenario.SignedInClientAsync();
        var first = await VenueScenario.HoldAsync(
            booker, venue.Id, VenueScenario.Today.AddDays(1), (courts[0], 9));
        await MissedAsync(first.Id, 3, daysAgo: 30);
        await booker.PostAsync($"/api/bookings/{first.Id}/cancel", null);

        var second = await VenueScenario.HoldAsync(
            booker, venue.Id, VenueScenario.Today.AddDays(1), (courts[1], 9));

        Assert.Equal(100m, second.DepositBaht);
        Assert.Equal(nameof(DepositReason.VenueTerms), second.DepositReason);
    }

    [Theory]
    [InlineData(0, 2, 3, null, null, VenueErrorCodes.InvalidRiskRule)]
    [InlineData(60, 3, 2, null, null, VenueErrorCodes.InvalidRiskRule)]
    [InlineData(60, 2, 3, 22, 18, VenueErrorCodes.InvalidPeakHours)]
    public async Task A_rule_that_makes_no_sense_is_refused(
        int lookback,
        int halfAt,
        int fullAt,
        int? from,
        int? until,
        string code)
    {
        var (owner, venue, _) = await scenario.BookableVenueAsync();

        var refused = await owner.PutAsJsonAsync(
            $"/api/venues/{venue.Id}/risk-rule",
            new RiskRuleRequest(true, lookback, halfAt, fullAt, from, until));

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Equal(code, await refused.ErrorCodeAsync());
    }

    [Fact]
    public async Task The_rule_a_venue_set_is_the_rule_it_reads_back()
    {
        var (owner, venue, _) = await scenario.BookableVenueAsync();

        await owner.PutAsJsonAsync(
            $"/api/venues/{venue.Id}/risk-rule",
            new RiskRuleRequest(true, 90, 1, 4, 17, 21));

        var read = await VenueScenario.ReadAsync<VenueResponse>(
            await owner.GetAsync($"/api/venues/{venue.Id}"));

        Assert.Equal(new RiskRuleResponse(true, 90, 1, 4, 17, 21), read.Risk);
    }

    /// <summary>Every venue starts with the rule PRD US-28 describes.</summary>
    [Fact]
    public async Task A_new_venue_counts_the_way_the_product_says()
    {
        var (_, venue, _) = await scenario.BookableVenueAsync();

        Assert.Equal(
            new RiskRuleResponse(true, 60, 2, 3, null, null),
            venue.Risk);
    }

    /// <summary>
    /// Records misses against a booking, on days that have been and gone. Written straight to the
    /// database: what a no-show does to a booking is US-13's to test, and this is about what the
    /// count of them does to the next booking.
    /// </summary>
    private async Task MissedAsync(Guid bookingId, int times, int daysAgo = 3)
    {
        using var scope = api.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var original = await database.Bookings
            .AsNoTracking()
            .Include(booking => booking.Slots)
            .SingleAsync(booking => booking.Id == bookingId);

        for (var number = 0; number < times; number++)
        {
            var day = VenueScenario.Today.AddDays(-daysAgo - number);
            var starts = PlatformRequirements.BangkokHour(day, 9);

            var missed = new Booking
            {
                VenueId = original.VenueId,
                BookerUserId = original.BookerUserId,
                Channel = original.Channel,
                Status = BookingStatus.NoShow,
                PaymentState = PaymentState.Received,
                CreatedAt = starts.AddDays(-1),
                HoldExpiresAt = starts.AddDays(-1),
                CancellationPolicyId = original.CancellationPolicyId,
                TotalBaht = original.TotalBaht,
                DepositBaht = original.DepositBaht,
                DepositReason = DepositReason.VenueTerms,
            };

            missed.Slots.Add(new BookingSlot
            {
                BookingId = missed.Id,
                CourtId = original.Slots[0].CourtId,
                StartsAt = starts,
                EndsAt = starts.AddHours(1),
                BahtPerHour = original.Slots[0].BahtPerHour,
                // A no-show holds no hours: they went back on sale (PRD BR-04).
                IsActive = false,
            });

            database.Bookings.Add(missed);
        }

        await database.SaveChangesAsync();
    }
}
