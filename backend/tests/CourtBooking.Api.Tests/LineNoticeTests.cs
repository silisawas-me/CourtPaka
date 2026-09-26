using CourtBooking.Api.Bookings;
using CourtBooking.Api.Data;
using CourtBooking.Api.Identity;
using CourtBooking.Api.Jobs;
using CourtBooking.Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CourtBooking.Api.Tests;

/// <summary>Telling a booker on LINE: PRD US-34.</summary>
public sealed class LineNoticeTests(ApiTestFixture api) : IClassFixture<ApiTestFixture>
{
    private readonly VenueScenario scenario = new(api);

    private static readonly DateOnly Tomorrow = VenueScenario.Today.AddDays(1);

    /// <summary>
    /// The gap this story closes. A LINE account books with a phone number and never proves an
    /// address (US-01), and the letters only went to an address somebody had proved (US-06) — so
    /// this person was told nothing at all.
    /// </summary>
    [Fact]
    public async Task Somebody_who_signed_in_with_LINE_is_told_on_LINE()
    {
        api.LineMessages.IsEnabled = true;

        var (_, venue, courts) = await scenario.BookableVenueAsync();
        var (booker, lineUserId) = await scenario.SignedInWithLineAsync();
        var booking = await VenueScenario.HoldAsync(booker, venue.Id, Tomorrow, (courts[0], 18));

        await SweepAsync();

        var said = Assert.Single(api.LineMessages.To(lineUserId));
        Assert.Equal($"booker.{BookerNoticeKind.Held}", said.Template);
        Assert.Equal(SupportedLanguages.Thai, said.Language);
        Assert.Contains($"/bookings/{booking.Id}", said.Text);
        Assert.Contains(venue.Name, said.Text);

        // The receipt says which way it went, so "was this person told, and how" has an answer.
        Assert.Equal(BookerChannel.Line, await SentByAsync(booking.Id, BookerNoticeKind.Held));
    }

    /// <summary>
    /// One message, one way. A booker reachable twice told twice is a booker who stops reading
    /// either (PRD US-34).
    /// </summary>
    [Fact]
    public async Task The_address_LINE_shared_is_not_written_to_as_well()
    {
        api.LineMessages.IsEnabled = true;

        var shared = scenario.NewEmail();
        var (_, venue, courts) = await scenario.BookableVenueAsync();
        var (booker, lineUserId) = await scenario.SignedInWithLineAsync(shared);
        await VenueScenario.HoldAsync(booker, venue.Id, Tomorrow, (courts[0], 19));

        await SweepAsync();

        Assert.Single(api.LineMessages.To(lineUserId));

        // Nothing about the booking. The address still gets our own verification link when LINE
        // shares one, because LINE saying it is theirs is not them saying it (PRD US-01).
        Assert.Empty(AboutBookings(shared));
    }

    /// <summary>
    /// A deployment with no channel access token has nothing to push with, so everybody is
    /// written to as before and nothing has to be switched off (PRD US-34).
    /// </summary>
    [Fact]
    public async Task Without_a_channel_nobody_is_pushed_to()
    {
        api.LineMessages.IsEnabled = false;

        var (_, venue, courts) = await scenario.BookableVenueAsync();
        var (booker, email) = await scenario.SignedInClientWithEmailAsync();
        var booking = await VenueScenario.HoldAsync(booker, venue.Id, Tomorrow, (courts[0], 20));

        await SweepAsync();

        Assert.NotEmpty(api.Emails.To(email));
        Assert.Equal(BookerChannel.Email, await SentByAsync(booking.Id, BookerNoticeKind.Held));
    }

    /// <summary>
    /// LINE refusing is not a reason to write to an address the person may never read, and not a
    /// reason to try again — the claim was taken before the sending so that nothing sends twice
    /// (PRD US-06, US-34).
    /// </summary>
    [Fact]
    public async Task A_message_LINE_will_not_carry_is_not_turned_into_an_email()
    {
        api.LineMessages.IsEnabled = true;
        api.LineMessages.Refuses = true;

        try
        {
            var shared = scenario.NewEmail();
            var (_, venue, courts) = await scenario.BookableVenueAsync();
            var (booker, lineUserId) = await scenario.SignedInWithLineAsync(shared);
            var booking = await VenueScenario.HoldAsync(booker, venue.Id, Tomorrow, (courts[0], 21));

            await SweepAsync();
            await SweepAsync();

            Assert.Empty(api.LineMessages.To(lineUserId));
            Assert.Empty(AboutBookings(shared));

            // Claimed, so nothing tries again; and never delivered, which the receipt says by
            // having no channel on it rather than by claiming one.
            Assert.Equal(1, await ClaimedAsync(booking.Id, BookerNoticeKind.Held));
            Assert.Null(await SentByAsync(booking.Id, BookerNoticeKind.Held));
        }
        finally
        {
            api.LineMessages.Refuses = false;
        }
    }

    /// <summary>What this address was told about a booking, as opposed to about its account.</summary>
    private IReadOnlyList<Api.Email.EmailMessage> AboutBookings(string email) =>
        [.. api.Emails.To(email).Where(one => one.Template?.StartsWith("booker.") == true)];

    /// <summary>Which way a message went, or null if it never went (PRD US-34).</summary>
    private async Task<BookerChannel?> SentByAsync(Guid bookingId, BookerNoticeKind kind)
    {
        using var scope = api.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await database.BookerNotices
            .Where(one => one.BookingId == bookingId && one.Kind == kind)
            .Select(one => one.SentBy)
            .SingleAsync();
    }

    private async Task<int> ClaimedAsync(Guid bookingId, BookerNoticeKind kind)
    {
        using var scope = api.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await database.BookerNotices
            .CountAsync(one => one.BookingId == bookingId && one.Kind == kind);
    }

    /// <summary>One pass of the booker mail, the way the caretaker runs it.</summary>
    private async Task SweepAsync()
    {
        using var scope = api.CreateScope();
        await scope.ServiceProvider.GetRequiredService<BookerMail>()
            .SendDueAsync(DateTimeOffset.UtcNow, CancellationToken.None);
    }
}
