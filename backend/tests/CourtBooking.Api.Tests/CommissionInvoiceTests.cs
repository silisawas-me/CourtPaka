using System.Net;
using System.Net.Http.Json;
using CourtBooking.Api.Bookings;
using CourtBooking.Api.Data;
using CourtBooking.Api.Tests.Infrastructure;
using CourtBooking.Api.Venues;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CourtBooking.Api.Tests;

/// <summary>
/// The commission invoice as both sides deal with it (PRD US-21): the venue reads what it owes
/// and says when it has paid, and the platform decides whether it agrees.
/// </summary>
public sealed class CommissionInvoiceTests(ApiTestFixture api) : IClassFixture<ApiTestFixture>
{
    private readonly VenueScenario scenario = new(api);

    [Fact]
    public async Task A_venue_reads_what_it_owes_and_what_it_was_worked_out_from()
    {
        var (owner, venue, invoice) = await BilledAsync();

        var mine = await MineAsync(owner, venue.Id);

        var only = Assert.Single(mine.Invoices);
        Assert.Equal(invoice.Number, only.Number);
        Assert.Equal(nameof(CommissionInvoiceStatus.Issued), only.Status);

        // The bookings it was worked out from: "trust the total" is not an answer to a venue
        // asking why a month came to that much (PRD US-21).
        Assert.NotNull(only.Lines);
        Assert.NotEmpty(only.Lines);
        Assert.Equal(only.AmountBaht, only.Lines.Sum(line => line.AmountBaht));
    }

    /// <summary>
    /// Issued → the venue shows it has paid → the platform agrees. Each step is the other side's
    /// to take, and neither can take the other's (PRD US-21).
    /// </summary>
    [Fact]
    public async Task The_venue_says_it_has_paid_and_the_platform_agrees()
    {
        var (owner, venue, invoice) = await BilledAsync();
        var admin = await scenario.PlatformAdminAsync();

        var sent = await SubmitAsync(owner, venue.Id, invoice.Id);
        Assert.Equal(nameof(CommissionInvoiceStatus.PaymentSubmitted), sent.Status);
        Assert.True(sent.HasEvidence);
        Assert.NotNull(sent.SubmittedAt);

        // The platform looks at what was sent before deciding.
        var evidence = await admin.GetAsync($"/api/admin/commission/invoices/{invoice.Id}/evidence");
        Assert.Equal(HttpStatusCode.OK, evidence.StatusCode);
        Assert.Equal("image/jpeg", evidence.Content.Headers.ContentType?.MediaType);

        var paid = await VenueScenario.ReadAsync<CommissionInvoiceResponse>(
            await admin.PostAsync($"/api/admin/commission/invoices/{invoice.Id}/paid", null));

        Assert.Equal(nameof(CommissionInvoiceStatus.Paid), paid.Status);
        Assert.NotNull(paid.PaidAt);
    }

    /// <summary>
    /// The platform can send a claim of payment back, and has to say why. The invoice goes back
    /// to being issued so the venue can answer it again (PRD US-21).
    /// </summary>
    [Fact]
    public async Task A_claim_of_payment_can_be_sent_back_with_a_reason()
    {
        var (owner, venue, invoice) = await BilledAsync();
        var admin = await scenario.PlatformAdminAsync();
        await SubmitAsync(owner, venue.Id, invoice.Id);

        var silent = await admin.PostAsJsonAsync(
            $"/api/admin/commission/invoices/{invoice.Id}/refuse",
            new InvoiceRefusalRequest("   "));
        Assert.Equal(HttpStatusCode.BadRequest, silent.StatusCode);

        var sentBack = await VenueScenario.ReadAsync<CommissionInvoiceResponse>(
            await admin.PostAsJsonAsync(
                $"/api/admin/commission/invoices/{invoice.Id}/refuse",
                new InvoiceRefusalRequest("ยอดไม่ตรงกับที่เข้าบัญชี")));

        Assert.Equal(nameof(CommissionInvoiceStatus.Issued), sentBack.Status);
        Assert.Equal("ยอดไม่ตรงกับที่เข้าบัญชี", sentBack.RefusedReason);

        // And the venue can answer it again, which clears the reason it was answering.
        var again = await SubmitAsync(owner, venue.Id, invoice.Id);
        Assert.Equal(nameof(CommissionInvoiceStatus.PaymentSubmitted), again.Status);
        Assert.Null(again.RefusedReason);
    }

    /// <summary>Both of the platform's decisions answer a claim of payment, so there has to be one.</summary>
    [Fact]
    public async Task Neither_decision_means_anything_without_a_claim_to_answer()
    {
        var (_, _, invoice) = await BilledAsync();
        var admin = await scenario.PlatformAdminAsync();

        var early = await admin.PostAsync($"/api/admin/commission/invoices/{invoice.Id}/paid", null);

        Assert.Equal(HttpStatusCode.Conflict, early.StatusCode);
        Assert.Equal(VenueErrorCodes.InvoiceNotAwaitingDecision, await early.ErrorCodeAsync());
    }

    /// <summary>
    /// Late is not a status: an invoice that is late is still waiting to be paid, and PRD US-21
    /// asks for both at once.
    /// </summary>
    [Theory]
    [InlineData(CommissionInvoiceStatus.Issued, 1, true)]
    [InlineData(CommissionInvoiceStatus.PaymentSubmitted, 1, true)]
    [InlineData(CommissionInvoiceStatus.Issued, 0, false)]
    [InlineData(CommissionInvoiceStatus.Issued, -1, false)]
    [InlineData(CommissionInvoiceStatus.Paid, 30, false)]
    public void Late_is_shown_beside_the_status_not_instead_of_it(
        CommissionInvoiceStatus status, int daysPastDue, bool expected)
    {
        var due = new DateOnly(2027, 2, 16);

        Assert.Equal(expected, InvoiceStanding.IsOverdue(status, due, due.AddDays(daysPastDue)));
    }

    /// <summary>
    /// Saying the platform has been paid is the owner's alone (PRD US-14 puts it in the list no
    /// permission can be granted for), and a venue may not see another venue's invoices at all.
    /// </summary>
    [Fact]
    public async Task Only_the_owner_may_say_it_has_paid_and_only_for_their_own_venue()
    {
        var (owner, venue, invoice) = await BilledAsync();
        var staff = await scenario.StaffClientAsync(
            owner, venue.Id, nameof(VenuePermissions.ManageBookings));

        // Staff may read what the venue owes — it is the venue's own business.
        Assert.Equal(
            HttpStatusCode.OK,
            (await staff.GetAsync($"/api/venues/{venue.Id}/commission")).StatusCode);

        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(staff, venue.Id, invoice.Id)).StatusCode);

        // And another venue's owner is nobody here.
        var (stranger, elsewhere, _) = await scenario.BookableVenueAsync();
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await stranger.GetAsync($"/api/venues/{venue.Id}/commission")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(stranger, elsewhere.Id, invoice.Id)).StatusCode);
    }

    /// <summary>
    /// The evidence is a picture of somebody's bank account, so only the platform may look at it
    /// (PDPA, PRD 8). The venue already knows what it sent.
    /// </summary>
    [Fact]
    public async Task Only_the_platform_may_look_at_what_was_sent()
    {
        var (owner, venue, invoice) = await BilledAsync();
        await SubmitAsync(owner, venue.Id, invoice.Id);

        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await owner.GetAsync($"/api/admin/commission/invoices/{invoice.Id}/evidence")).StatusCode);
    }

    /// <summary>What is not a picture is not evidence, whatever the uploader called it.</summary>
    [Fact]
    public async Task Something_that_is_not_a_picture_is_refused()
    {
        var (owner, venue, invoice) = await BilledAsync();

        var refused = await SendAsync(owner, venue.Id, invoice.Id, "not a picture"u8.ToArray());

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Equal(SlipErrorCodes.UnsupportedFile, await refused.ErrorCodeAsync());
    }

    /// <summary>A venue billed for a month, and the owner who has to pay it.</summary>
    private async Task<(HttpClient Owner, VenueResponse Venue, CommissionInvoice Invoice)> BilledAsync()
    {
        var (owner, venue, courts) = await scenario.BookableVenueAsync(baht: 400m);
        var (_, booking) = await scenario.ConfirmedBookingAsync(owner, venue.Id, courts[0], 18);
        await scenario.StartsInAsync(booking.Id, TimeSpan.FromDays(-40));

        using var scope = api.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var who = await database.Users.FirstAsync();

        database.CommissionRates.Add(new CommissionRate
        {
            VenueId = venue.Id,
            Percent = 10m,
            EffectiveFrom = VenueScenario.Today.AddYears(-2),
            SetByUserId = who.Id,
            SetAt = DateTimeOffset.UtcNow.AddYears(-2),
        });
        await database.SaveChangesAsync();

        var played = await database.BookingSlots
            .Where(slot => slot.BookingId == booking.Id)
            .MinAsync(slot => slot.StartsAt);
        var month = new DateOnly(played.Year, played.Month, 1);

        var stored = await database.Venues.SingleAsync(one => one.Id == venue.Id);
        await using var transaction = await database.Database.BeginTransactionAsync();
        var invoice = await CommissionRun.BillAsync(
            database, stored, month, DateTimeOffset.UtcNow, CancellationToken.None);
        await database.SaveChangesAsync();
        await transaction.CommitAsync();

        Assert.NotNull(invoice);
        return (owner, venue, invoice);
    }

    private static async Task<CommissionInvoiceResponse> SubmitAsync(
        HttpClient owner, Guid venueId, Guid invoiceId) =>
        await VenueScenario.ReadAsync<CommissionInvoiceResponse>(
            await SendAsync(owner, venueId, invoiceId));

    private static async Task<HttpResponseMessage> SendAsync(
        HttpClient client, Guid venueId, Guid invoiceId, byte[]? bytes = null)
    {
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(bytes ?? VenueScenario.Jpeg());
        file.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/jpeg");
        form.Add(file, "file", "transfer.jpg");

        return await client.PostAsync(
            $"/api/venues/{venueId}/commission/{invoiceId}/payment", form);
    }

    private static async Task<VenueCommissionResponse> MineAsync(HttpClient client, Guid venueId) =>
        await VenueScenario.ReadAsync<VenueCommissionResponse>(
            await client.GetAsync($"/api/venues/{venueId}/commission"));
}
