using System.Net;
using System.Net.Http.Json;
using CourtBooking.Api.Data;
using CourtBooking.Api.Tests.Infrastructure;
using CourtBooking.Api.Venues;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CourtBooking.Api.Tests;

/// <summary>Applying to join the platform: PRD US-10, US-14, 7.2.</summary>
public sealed class VenueApplicationTests(ApiTestFixture api) : IClassFixture<ApiTestFixture>
{
    private readonly VenueScenario scenario = new(api);

    [Fact]
    public async Task An_application_starts_waiting_and_makes_its_applicant_the_owner()
    {
        var applicant = await scenario.SignedInClientAsync();

        var venue = await scenario.CreateVenueAsync(applicant);

        Assert.Equal(nameof(VenueStatus.Pending), venue.Status);
        Assert.Equal(nameof(VenueRole.Owner), venue.Role);
    }

    [Fact]
    public async Task What_was_agreed_to_is_recorded_with_who_and_when()
    {
        var applicant = await scenario.SignedInClientAsync();
        var venue = await scenario.CreateVenueAsync(applicant);

        using var scope = api.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var stored = await database.Venues
            .AsNoTracking()
            .SingleAsync(one => one.Id == venue.Id, CancellationToken.None);

        Assert.Equal(ApiFactory.VenueAgreementVersion, stored.AgreementVersion);
        Assert.NotNull(stored.AgreementAcceptedAt);
        Assert.NotNull(stored.AgreementAcceptedByUserId);
    }

    /// <summary>
    /// What was on screen is what is agreed to. A version that moved on while the form was open
    /// is refused rather than recorded as the new one (PRD US-10, Q8).
    /// </summary>
    [Fact]
    public async Task An_agreement_that_is_not_the_one_being_asked_for_is_refused()
    {
        var applicant = await scenario.SignedInClientAsync();

        var refused = await applicant.PostAsJsonAsync(
            "/api/venues",
            VenueScenario.Application(scenario.NewCode(), agreementVersion: "1999-01-01"));

        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Equal(VenueErrorCodes.AgreementOutOfDate, await refused.ErrorCodeAsync());
    }

    [Fact]
    public async Task The_platform_says_which_agreement_it_is_asking_for()
    {
        var anyone = await scenario.SignedInClientAsync();

        var asked = await VenueScenario.ReadAsync<VenueAgreementResponse>(
            await anyone.GetAsync("/api/venues/agreement"));

        Assert.Equal(ApiFactory.VenueAgreementVersion, asked.Version);
    }

    [Theory]
    [InlineData("12345678901234", VenueErrorCodes.InvalidTaxId)]
    [InlineData("010556100000A", VenueErrorCodes.InvalidTaxId)]
    [InlineData("", VenueErrorCodes.InvalidTaxId)]
    public async Task A_tax_number_that_is_not_thirteen_digits_is_refused(string taxId, string code)
    {
        var applicant = await scenario.SignedInClientAsync();

        var refused = await applicant.PostAsJsonAsync(
            "/api/venues",
            VenueScenario.Application(
                scenario.NewCode(), VenueScenario.Business(taxId: taxId)));

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Equal(code, await refused.ErrorCodeAsync());
    }

    [Fact]
    public async Task A_branch_code_that_is_not_five_digits_is_refused()
    {
        var applicant = await scenario.SignedInClientAsync();

        var refused = await applicant.PostAsJsonAsync(
            "/api/venues",
            VenueScenario.Application(
                scenario.NewCode(), VenueScenario.Business(taxBranch: "0")));

        Assert.Equal(VenueErrorCodes.InvalidTaxBranch, await refused.ErrorCodeAsync());
    }

    /// <summary>A pin with only one half of itself is not a place (PRD US-10).</summary>
    [Theory]
    [InlineData(13.7318, null)]
    [InlineData(null, 100.5686)]
    [InlineData(95.0, 100.5686)]
    public async Task Half_a_pin_or_one_off_the_planet_is_refused(double? latitude, double? longitude)
    {
        var applicant = await scenario.SignedInClientAsync();

        var refused = await applicant.PostAsJsonAsync(
            "/api/venues",
            VenueScenario.Application(
                scenario.NewCode(),
                VenueScenario.Business(latitude: latitude, longitude: longitude)));

        Assert.Equal(VenueErrorCodes.InvalidCoordinates, await refused.ErrorCodeAsync());
    }

    [Fact]
    public async Task No_pin_at_all_is_fine()
    {
        var applicant = await scenario.SignedInClientAsync();

        var applied = await applicant.PostAsJsonAsync(
            "/api/venues",
            VenueScenario.Application(
                scenario.NewCode(),
                VenueScenario.Business(latitude: null, longitude: null)));

        Assert.Equal(HttpStatusCode.Created, applied.StatusCode);
    }

    /// <summary>
    /// A body with no business block at all answers 400 with a code, like every other malformed
    /// request, rather than falling over on a null (PRD US-23).
    /// </summary>
    [Fact]
    public async Task An_application_with_no_business_block_is_refused_rather_than_breaking()
    {
        var applicant = await scenario.SignedInClientAsync();

        var refused = await applicant.PostAsJsonAsync(
            "/api/venues",
            new
            {
                code = scenario.NewCode(),
                name = "Smash Court",
                addressLine = "1 ถนนทดสอบ",
                district = "บางรัก",
                province = "กรุงเทพมหานคร",
                business = (object?)null,
                agreementVersion = ApiFactory.VenueAgreementVersion,
            });

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.False(string.IsNullOrEmpty(await refused.ErrorCodeAsync()));
    }

    /// <summary>
    /// Where the money lands is not something an owner may delegate, so the details endpoint —
    /// which staff with ManageSettings may reach — cannot move it (PRD US-14).
    /// </summary>
    [Fact]
    public async Task The_details_endpoint_cannot_move_where_the_money_goes()
    {
        var (owner, venue, _) = await scenario.BookableVenueAsync();
        var staff = await scenario.StaffClientAsync(
            owner, venue.Id, nameof(VenuePermissions.ManageSettings));

        var before = await VenueScenario.ReadAsync<VenueBusinessResponse>(
            await owner.GetAsync($"/api/venues/{venue.Id}/business"));

        // Sent anyway, the way a hand-rolled client would: the field is not on the contract, so
        // it has to be ignored rather than quietly honoured.
        var changed = await staff.PutAsJsonAsync(
            $"/api/venues/{venue.Id}",
            new
            {
                name = "Renamed Court",
                addressLine = "2 ถนนใหม่",
                district = "ปทุมวัน",
                province = "กรุงเทพมหานคร",
                business = new { promptPayId = "0899999999" },
            });
        Assert.Equal(HttpStatusCode.NoContent, changed.StatusCode);

        var after = await VenueScenario.ReadAsync<VenueBusinessResponse>(
            await owner.GetAsync($"/api/venues/{venue.Id}/business"));
        Assert.Equal(before.PromptPayId, after.PromptPayId);
    }

    [Fact]
    public async Task Where_the_money_goes_is_the_owner_s_alone()
    {
        var (owner, venue, _) = await scenario.BookableVenueAsync();
        var staff = await scenario.StaffClientAsync(
            owner, venue.Id, nameof(VenuePermissions.ManageSettings));

        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await staff.GetAsync($"/api/venues/{venue.Id}/business")).StatusCode);
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await staff.PutAsJsonAsync(
                $"/api/venues/{venue.Id}/business", VenueScenario.Business())).StatusCode);
    }

    [Fact]
    public async Task The_owner_can_correct_where_the_money_goes()
    {
        var (owner, venue, _) = await scenario.BookableVenueAsync();

        var saved = await VenueScenario.ReadAsync<VenueBusinessResponse>(
            await owner.PutAsJsonAsync(
                $"/api/venues/{venue.Id}/business",
                VenueScenario.Business(promptPayId: "0899999999")));

        Assert.Equal("0899999999", saved.PromptPayId);

        var read = await VenueScenario.ReadAsync<VenueBusinessResponse>(
            await owner.GetAsync($"/api/venues/{venue.Id}/business"));
        Assert.Equal("0899999999", read.PromptPayId);
    }

    /// <summary>
    /// The rule that makes a refusal answerable: a venue that was turned away is otherwise frozen,
    /// and freezing it out of fixing what it was turned away for would be a door with no handle
    /// (PRD US-10, US-20).
    /// </summary>
    [Fact]
    public async Task A_venue_that_was_turned_away_can_still_be_put_right_and_ask_again()
    {
        var applicant = await scenario.SignedInClientAsync();
        var venue = await scenario.CreateVenueAsync(applicant);
        await scenario.SetStatusAsync(venue.Id, VenueStatus.Rejected);

        var corrected = await applicant.PutAsJsonAsync(
            $"/api/venues/{venue.Id}/business",
            VenueScenario.Business(promptPayId: "0877777777"));
        Assert.Equal(HttpStatusCode.OK, corrected.StatusCode);

        var again = await VenueScenario.ReadAsync<VenueResponse>(
            await applicant.PostAsync($"/api/venues/{venue.Id}/resubmit", null));
        Assert.Equal(nameof(VenueStatus.Pending), again.Status);
    }

    [Fact]
    public async Task A_suspended_venue_is_not_a_question_it_can_answer()
    {
        var applicant = await scenario.SignedInClientAsync();
        var venue = await scenario.CreateVenueAsync(applicant);
        await scenario.SetStatusAsync(venue.Id, VenueStatus.Suspended);

        // Suspended is the platform holding a working venue still, not asking it something.
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await applicant.PutAsJsonAsync(
                $"/api/venues/{venue.Id}/business", VenueScenario.Business())).StatusCode);
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await applicant.PostAsync($"/api/venues/{venue.Id}/resubmit", null)).StatusCode);
    }

    [Fact]
    public async Task A_venue_that_was_not_turned_away_has_nothing_to_ask_again()
    {
        var applicant = await scenario.SignedInClientAsync();
        var venue = await scenario.CreateVenueAsync(applicant);

        var refused = await applicant.PostAsync($"/api/venues/{venue.Id}/resubmit", null);

        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Equal(VenueErrorCodes.NotRefused, await refused.ErrorCodeAsync());
    }

    [Fact]
    public async Task Another_venue_s_owner_cannot_read_this_one_s_tax_identity()
    {
        var (_, venue, _) = await scenario.BookableVenueAsync();
        var stranger = await scenario.SignedInClientAsync();

        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await stranger.GetAsync($"/api/venues/{venue.Id}/business")).StatusCode);
    }
}
