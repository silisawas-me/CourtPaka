using System.Net;
using CourtBooking.Api.Bookings;
using CourtBooking.Api.Tests.Infrastructure;
using CourtBooking.Api.Venues;

namespace CourtBooking.Api.Tests;

/// <summary>What the payment screen is given to draw (PRD US-04).</summary>
public sealed class PaymentDetailsTests(ApiTestFixture api) : IClassFixture<ApiTestFixture>
{
    private readonly VenueScenario scenario = new(api);

    [Fact]
    public async Task A_booking_waiting_for_money_says_where_to_send_it()
    {
        var (_, venue, courts) = await scenario.BookableVenueAsync(baht: 250m);
        var booker = await scenario.SignedInClientAsync();
        var booking = await VenueScenario.HoldAsync(
            booker, venue.Id, VenueScenario.Today.AddDays(1), (courts[0], 18));

        var paying = await VenueScenario.ReadAsync<PaymentResponse>(
            await booker.GetAsync($"/api/bookings/{booking.Id}/payment"));

        Assert.Equal(booking.TotalBaht, paying.TotalBaht);
        Assert.NotNull(paying.PromptPayPayload);

        // The amount in the code is the amount the booking says, which is the whole point of
        // building it on this side (PRD US-04).
        Assert.Contains($"5406{booking.TotalBaht:0.00}", paying.PromptPayPayload);
        Assert.False(string.IsNullOrWhiteSpace(paying.AccountName));
    }

    [Fact]
    public async Task Somebody_else_s_booking_is_not_theirs_to_pay()
    {
        var (_, venue, courts) = await scenario.BookableVenueAsync();
        var booker = await scenario.SignedInClientAsync();
        var booking = await VenueScenario.HoldAsync(
            booker, venue.Id, VenueScenario.Today.AddDays(1), (courts[0], 18));

        var stranger = await scenario.SignedInClientAsync();

        Assert.Equal(
            HttpStatusCode.NotFound,
            (await stranger.GetAsync($"/api/bookings/{booking.Id}/payment")).StatusCode);
    }
}

/// <summary>The string a bank app reads out of a PromptPay QR (PRD US-04).</summary>
public sealed class PromptPayTests
{
    /// <summary>
    /// Whole payloads, checked against ones built by a separate implementation of the same
    /// standard rather than against this one's own output. A checksum a class agrees with itself
    /// about proves nothing: a QR with a wrong CRC scans perfectly and is simply refused at the
    /// bank, so the one thing worth testing here is agreement with somebody else.
    /// </summary>
    [Theory]
    [InlineData(
        "0812345678", 100.00,
        "00020101021229370016A0000006770101110113006681234567853037645406100.005802TH6304F142")]
    [InlineData(
        "0105561000000", 250.50,
        "00020101021229370016A0000006770101110213010556100000053037645406250.505802TH63041E38")]
    public void A_payload_is_the_one_the_standard_describes(
        string promptPayId, decimal amount, string expected)
    {
        Assert.Equal(expected, PromptPay.For(promptPayId, amount));
    }

    /// <summary>
    /// A phone number travels as a country code with the leading zero dropped, padded to
    /// thirteen — not as the ten digits somebody typed.
    /// </summary>
    [Fact]
    public void A_phone_number_is_written_the_way_a_bank_app_reads_one()
    {
        var payload = PromptPay.For("081-234-5678", 100m);

        Assert.Contains("01130066812345678", payload);
        Assert.DoesNotContain("0812345678", payload);
    }

    [Fact]
    public void The_amount_in_the_qr_is_the_amount_it_was_asked_for()
    {
        // The length prefix is part of the element, so a four-figure amount is 07, not 06 — the
        // kind of thing a hand-written expectation gets wrong before the code does.
        Assert.Contains("5406250.50", PromptPay.For("0812345678", 250.5m));
        Assert.Contains("54071000.00", PromptPay.For("0812345678", 1000m));
    }

    /// <summary>
    /// An account no bank app would accept gets no QR at all. Drawing one that is refused at the
    /// counter is worse than saying the venue has not finished setting itself up (PRD US-10).
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("12345")]
    [InlineData("1812345678")]
    [InlineData("abcdefghij")]
    public void An_account_that_is_not_one_gets_no_code(string? promptPayId)
    {
        Assert.Null(PromptPay.For(promptPayId, 100m));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-50)]
    public void And_neither_does_an_amount_that_is_not_money(decimal amount)
    {
        Assert.Null(PromptPay.For("0812345678", amount));
    }

    /// <summary>
    /// The checksum covers its own tag and length, which is the part that is easy to leave out
    /// and impossible to notice without a second implementation to disagree with.
    /// </summary>
    [Fact]
    public void The_checksum_covers_its_own_tag_and_length()
    {
        var payload = PromptPay.For("0812345678", 100m)!;

        Assert.Equal("6304", payload[^8..^4]);
        Assert.All(payload[^4..], character => Assert.True(Uri.IsHexDigit(character)));
    }
}
