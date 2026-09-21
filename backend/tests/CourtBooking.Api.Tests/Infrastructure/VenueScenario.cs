using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Web;
using CourtBooking.Api.Data;
using CourtBooking.Api.Bookings;
using CourtBooking.Api.Identity;
using CourtBooking.Api.Localization;
using CourtBooking.Api.Venues;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CourtBooking.Api.Tests.Infrastructure;

/// <summary>
/// Getting to the point a venue test starts from: a signed-in person, a venue they own, and staff
/// who joined it. Every test in the run shares one API, so each scenario makes its own addresses.
/// </summary>
public sealed class VenueScenario(ApiTestFixture api)
{
    public const string Password = "CorrectHorse1";

    public string NewEmail() => $"venue-{Guid.NewGuid():N}@example.com";

    public string NewCode() => Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();

    /// <summary>
    /// A signed-in account. Its address is verified unless the test is about what happens when it
    /// is not: registering leaves it unverified, and signing in works either way (PRD US-01).
    /// </summary>
    public async Task<HttpClient> SignedInClientAsync(bool verifyEmail = true) =>
        (await SignedInClientWithEmailAsync(verifyEmail)).Client;

    public async Task<(HttpClient Client, string Email)> SignedInClientWithEmailAsync(
        bool verifyEmail = true)
    {
        var client = api.CreateClient();
        var email = NewEmail();
        var registration = await client.PostAsJsonAsync(
            "/api/auth/register",
            new RegisterRequest(email, Password, ApiFactory.PrivacyPolicyVersion, SupportedLanguages.Thai, null));
        Assert.Equal(HttpStatusCode.Created, registration.StatusCode);

        var login = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(email, Password));
        Assert.Equal(HttpStatusCode.NoContent, login.StatusCode);

        if (verifyEmail)
        {
            await ConfirmEmailAsync(email);
        }

        return (client, email);
    }

    /// <summary>
    /// Marks the address verified without the round trip through the emailed link, which
    /// AuthEndpointTests covers on its own.
    /// </summary>
    public async Task ConfirmEmailAsync(string email)
    {
        using var scope = api.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await database.Users
            .Where(user => user.Email == email)
            .ExecuteUpdateAsync(setters => setters.SetProperty(user => user.EmailConfirmed, true));
    }

    public async Task<VenueResponse> CreateVenueAsync(HttpClient client) =>
        await ReadAsync<VenueResponse>(
            await client.PostAsJsonAsync("/api/venues", Application(NewCode())),
            HttpStatusCode.Created);

    /// <summary>
    /// A whole application, for a test that is about something else. The parts a test does care
    /// about it passes itself (PRD US-10).
    /// </summary>
    public static CreateVenueRequest Application(
        string code,
        VenueBusinessRequest? business = null,
        string? agreementVersion = null) =>
        new(
            code,
            "Smash Court",
            "1 ถนนทดสอบ",
            "บางรัก",
            "กรุงเทพมหานคร",
            business ?? Business(),
            agreementVersion ?? ApiFactory.VenueAgreementVersion);

    /// <summary>Where the money goes and who the venue is for tax (PRD US-10).</summary>
    public static VenueBusinessRequest Business(
        string promptPayId = "0812345678",
        string taxId = "0105561000000",
        string taxBranch = VenueBusiness.HeadOfficeBranch,
        bool vatRegistered = true,
        double? latitude = 13.7318,
        double? longitude = 100.5686) =>
        new(
            promptPayId,
            "บริษัท ทดสอบ จำกัด",
            vatRegistered,
            "บริษัท ทดสอบ จำกัด",
            taxId,
            taxBranch,
            "1 ถนนทดสอบ แขวงสีลม เขตบางรัก กรุงเทพมหานคร 10500",
            latitude,
            longitude);

    /// <summary>
    /// Checks the status the endpoint promised and hands back the body, so a test that is about
    /// something else does not spell out both every time.
    /// </summary>
    public static async Task<T> ReadAsync<T>(
        HttpResponseMessage response,
        HttpStatusCode expected = HttpStatusCode.OK)
    {
        Assert.Equal(expected, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }

    public async Task<VenueInvitationResponse> InviteAsync(
        HttpClient owner,
        Guid venueId,
        string email,
        string[]? permissions = null) =>
        await ReadAsync<VenueInvitationResponse>(
            await owner.PostAsJsonAsync(
                $"/api/venues/{venueId}/invitations", new InviteMemberRequest(email, permissions)),
            HttpStatusCode.Created);

    public async Task<VenueInvitationResponse> InviteAndAcceptAsync(
        HttpClient owner,
        HttpClient invited,
        Guid venueId,
        string email,
        string[]? permissions = null)
    {
        var invitation = await InviteAsync(owner, venueId, email, permissions);
        var accepted = await invited.PostAsJsonAsync(
            "/api/venues/invitations/accept",
            new AcceptInvitationRequest(invitation.Id, ReadInvitationToken(email)));
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        return invitation;
    }

    /// <summary>A signed-in staff member of the venue, holding exactly the permissions named.</summary>
    public async Task<HttpClient> StaffClientAsync(HttpClient owner, Guid venueId, params string[] permissions)
    {
        var (client, email) = await SignedInClientWithEmailAsync();
        await InviteAndAcceptAsync(owner, client, venueId, email, permissions);
        return client;
    }

    public string ReadInvitationToken(string email)
    {
        // The link is on a line of its own, with words before and after it in either language.
        var body = api.Emails.LastTo(email).Body;
        var link = body[body.IndexOf("http", StringComparison.Ordinal)..]
            .Split((char[])['\n', '\r', ' '], 2)[0];
        var url = new Uri(link);
        return HttpUtility.ParseQueryString(url.Query)["token"]!;
    }

    public async Task SetStatusAsync(Guid venueId, VenueStatus status)
    {
        // Platform Admin approval arrives with US-20; until then the test sets the status directly.
        using var scope = api.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await database.Venues
            .Where(venue => venue.Id == venueId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(venue => venue.Status, status));
    }

    /// <summary>
    /// A venue a booker can actually use: approved, open 06:00–22:00 every day at one price, with
    /// the courts asked for. It is what both the grid and the booking tests start from.
    /// </summary>
    public async Task<(HttpClient Owner, VenueResponse Venue, Guid[] CourtIds)> BookableVenueAsync(
        int courts = 1,
        decimal baht = 200m)
    {
        var owner = await SignedInClientAsync();
        var venue = await CreateVenueAsync(owner);
        await SetHoursAsync(owner, venue.Id, Today);
        await SetPricesAsync(owner, venue.Id, AllWeek(6, 22, baht));

        var courtIds = new List<Guid>(courts);
        for (var number = 1; number <= courts; number++)
        {
            var court = await ReadAsync<CourtResponse>(
                await owner.PostAsJsonAsync(
                    $"/api/venues/{venue.Id}/courts", new CreateCourtRequest($"Court {number}")),
                HttpStatusCode.Created);
            courtIds.Add(court.Id);
        }

        // Venue approval arrives with US-20; until then the test sets the status directly.
        await SetStatusAsync(venue.Id, VenueStatus.Approved);

        return (owner, venue, [.. courtIds]);
    }

    /// <summary>The grid one day at a venue, as a booker with no account reads it.</summary>
    public async Task<AvailabilityResponse> ReadAvailabilityAsync(
        HttpClient client,
        Guid venueId,
        DateOnly date) =>
        await ReadAsync<AvailabilityResponse>(
            await client.GetAsync($"/api/venues/{venueId}/availability?date={date:yyyy-MM-dd}"));

    /// <summary>A booking's recorded history, oldest first (PRD 6.1).</summary>
    public async Task<BookingStatusChange[]> HistoryAsync(Guid bookingId)
    {
        using var scope = api.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await database.BookingStatusChanges
            .AsNoTracking()
            .Where(change => change.BookingId == bookingId)
            .OrderBy(change => change.ChangedAt)
            .ThenBy(change => change.Id)
            .ToArrayAsync();
    }

    /// <summary>Takes court-hours the way a booker does, and answers what they now hold.</summary>
    public static async Task<BookingResponse> HoldAsync(
        HttpClient client,
        Guid venueId,
        DateOnly date,
        params (Guid CourtId, int Hour)[] slots) =>
        await ReadAsync<BookingResponse>(
            await client.PostAsJsonAsync(
                "/api/bookings",
                new CreateBookingRequest(
                    venueId,
                    [.. slots.Select(slot => new BookingSlotRequest(slot.CourtId, date, slot.Hour))])),
            HttpStatusCode.Created);

    /// <summary>
    /// The first bytes of a JPEG, which is all the server reads to recognise one, and a tail that
    /// is this call's alone — two slips sharing bytes is a thing the venue is shown (PRD BR-07),
    /// so a test that did not ask for it should not stumble into it.
    /// </summary>
    public static byte[] Jpeg() =>
        [0xFF, 0xD8, 0xFF, 0xE0, .. "JFIF"u8, .. Guid.CreateVersion7().ToByteArray()];

    /// <summary>Sends a slip the way the booker's page does (PRD US-04).</summary>
    public static async Task<HttpResponseMessage> UploadAsync(
        HttpClient client,
        Guid bookingId,
        byte[] bytes,
        string contentType = "image/jpeg",
        string fileName = "slip.jpg")
    {
        // Awaited rather than handed back: the form has to outlive the request, and a Task
        // returned from here would leave it disposed before the bytes were read.
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        form.Add(file, "file", fileName);

        return await client.PostAsync($"/api/bookings/{bookingId}/slip", form);
    }

    /// <summary>
    /// Winds a held booking's clock back so the test can see what happens once it lapses, which is
    /// otherwise fifteen minutes away.
    /// </summary>
    public async Task LapseHoldAsync(Guid bookingId)
    {
        using var scope = api.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await database.Bookings
            .Where(booking => booking.Id == bookingId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(
                booking => booking.HoldExpiresAt, DateTimeOffset.UtcNow.AddMinutes(-1)));
    }

    /// <summary>
    /// A booking that has been paid for and is waiting for the venue to look at it (PRD US-04).
    /// </summary>
    public async Task<(HttpClient Booker, BookingResponse Booking)> WaitingBookingAsync(
        Guid venueId,
        Guid courtId,
        int hour,
        byte[]? slip = null)
    {
        var booker = await SignedInClientAsync();
        var booking = await HoldAsync(booker, venueId, Today.AddDays(1), (courtId, hour));

        var sent = await UploadAsync(booker, booking.Id, slip ?? Jpeg());
        if (sent.StatusCode != HttpStatusCode.OK)
        {
            throw new InvalidOperationException($"Could not send a slip: {sent.StatusCode}");
        }

        return (booker, booking);
    }

    /// <summary>
    /// Somebody else takes the hours a booking has let go. Written straight to the database: a
    /// test that has moved a booking's hours around no longer lines up with the grid, and what is
    /// being tested is the constraint that stops two bookings holding one court-hour (PRD BR-04).
    /// </summary>
    public async Task<Guid> SomebodyElseTakesAsync(Guid bookingId)
    {
        using var scope = api.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var released = await database.Bookings
            .AsNoTracking()
            .Include(booking => booking.Slots)
            .Where(booking => booking.Id == bookingId)
            .SelectMany(booking => booking.Slots.Where(slot => !slot.IsActive))
            .ToListAsync();

        var original = await database.Bookings.AsNoTracking()
            .SingleAsync(booking => booking.Id == bookingId);

        var taker = new Booking
        {
            VenueId = original.VenueId,
            BookerUserId = original.BookerUserId,
            Channel = original.Channel,
            Status = BookingStatus.Confirmed,
            PaymentState = PaymentState.Received,
            CreatedAt = DateTimeOffset.UtcNow,
            HoldExpiresAt = DateTimeOffset.UtcNow.AddMinutes(15),
            TotalBaht = released.Sum(slot => slot.BahtPerHour),
            CancellationPolicyId = original.CancellationPolicyId,
        };

        taker.Slots.AddRange(released.Select(slot => new BookingSlot
        {
            BookingId = taker.Id,
            CourtId = slot.CourtId,
            StartsAt = slot.StartsAt,
            EndsAt = slot.EndsAt,
            BahtPerHour = slot.BahtPerHour,
            IsActive = true,
        }));

        database.Bookings.Add(taker);
        await database.SaveChangesAsync();
        return taker.Id;
    }

    /// <summary>The address of each member of this venue, for checking who was written to.</summary>
    /// <summary>
    /// Somebody who acts for the platform (PRD US-20). The address is the one in configuration;
    /// the account behind it is an ordinary one, because being an admin is not something the
    /// account carries.
    /// </summary>
    public async Task<HttpClient> PlatformAdminAsync()
    {
        var client = api.CreateClient();
        var email = ApiFactory.PlatformAdminEmail;

        // One account, shared by every test in the run: it is named in configuration, so there
        // is only one of it. Registering again is fine and answers 409.
        await client.PostAsJsonAsync(
            "/api/auth/register",
            new RegisterRequest(
                email, Password, ApiFactory.PrivacyPolicyVersion, SupportedLanguages.Thai, null));

        // Confirmed, because an unconfirmed address is not an admin at all — which is the rule
        // PlatformAdminTests checks on its own.
        await ConfirmEmailAsync(email);

        var login = await client.PostAsJsonAsync(
            "/api/auth/login", new LoginRequest(email, Password));
        Assert.Equal(HttpStatusCode.NoContent, login.StatusCode);

        return client;
    }

    public async Task<Dictionary<Guid, string>> MemberEmailsAsync(Guid venueId)
    {
        using var scope = api.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        // Projected in the query: ToDictionary's selectors run once the rows are here, and by
        // then the navigation nobody asked for is null.
        return await database.VenueMemberships
            .Where(member => member.VenueId == venueId)
            .Select(member => new { member.UserId, Email = member.User!.Email! })
            .ToDictionaryAsync(member => member.UserId, member => member.Email);
    }

    /// <summary>Whether any of a booking's hours are still held against its court (PRD BR-04).</summary>
    public async Task<bool> HoldsItsHoursAsync(Guid bookingId) =>
        await HoursStillHeldAsync(bookingId) > 0;

    /// <summary>How many of a booking's hours it still holds, for the decisions that free some.</summary>
    public async Task<int> HoursStillHeldAsync(Guid bookingId)
    {
        using var scope = api.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await database.BookingSlots.CountAsync(
            slot => slot.BookingId == bookingId && slot.IsActive);
    }

    /// <summary>The booking as it is stored, for the fields no endpoint hands back.</summary>
    public async Task<Booking> StoredBookingAsync(Guid bookingId)
    {
        using var scope = api.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await database.Bookings.AsNoTracking().SingleAsync(booking => booking.Id == bookingId);
    }

    /// <summary>
    /// Moves a booking's hours into the past, so a test can see what a venue that checks the slip
    /// only after the court was played meets (PRD 6.1).
    /// </summary>
    public Task PlayOutAsync(Guid bookingId) => StartsInAsync(bookingId, TimeSpan.FromDays(-2));

    /// <summary>
    /// Moves a booking's hours so the first of them is exactly this far from now, keeping the
    /// gaps between them. The cancellation terms are measured in how much notice was given
    /// (PRD US-11, BR-06), so a test that stood on a calendar date instead would pass or fail by
    /// the hour it happened to run at.
    /// </summary>
    public async Task StartsInAsync(Guid bookingId, TimeSpan fromNow)
    {
        using var scope = api.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var slots = database.BookingSlots.Where(slot => slot.BookingId == bookingId);
        var shift = await slots.MinAsync(slot => slot.StartsAt) - (DateTimeOffset.UtcNow + fromNow);

        await slots.ExecuteUpdateAsync(setters => setters
            .SetProperty(slot => slot.StartsAt, slot => slot.StartsAt - shift)
            .SetProperty(slot => slot.EndsAt, slot => slot.EndsAt - shift));
    }

    /// <summary>
    /// Writes off every hold whose time is up, the way a booking at those hours would. Lets a test
    /// see what a booker meets once something has recorded the expiry, not only implied it.
    /// </summary>
    public async Task ExpireLapsedHoldsAsync()
    {
        using var scope = api.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await BookedSlots.ReleaseOwnLapsedAsync(
            database,
            await database.Bookings
                // Held is only ever an online booking, which always has a booker (PRD US-13).
                .Where(booking => booking.Status == BookingStatus.Held)
                .Select(booking => booking.BookerUserId!.Value)
                .FirstAsync(),
            DateTimeOffset.UtcNow,
            CancellationToken.None);
    }

    public async Task<VenueMemberResponse[]> GetMembersAsync(HttpClient client, Guid venueId) =>
        await ReadAsync<VenueMemberResponse[]>(await client.GetAsync($"/api/venues/{venueId}/members"));

    /// <summary>
    /// The day the platform is on, which is the day the server will compare a date against. Tests
    /// that publish settings have to agree with it or the settings are dated in the past.
    /// </summary>
    public static DateOnly Today => PlatformRequirements.BangkokToday(TimeProvider.System);

    /// <summary>New details for a venue whose address the test does not care about.</summary>
    public static UpdateVenueRequest Details(string name) =>
        new(name, "2 ถนนใหม่", "ปทุมวัน", "กรุงเทพมหานคร");

    /// <summary>The same hours seven days a week, with one day optionally closed.</summary>
    public static OpeningHoursDayRequest[] Week(int opens, int closes, DayOfWeek? closedOn = null) =>
        Enum.GetValues<DayOfWeek>()
            .Select(day => day == closedOn
                ? new OpeningHoursDayRequest(day.ToString(), null, null)
                : new OpeningHoursDayRequest(day.ToString(), opens, closes))
            .ToArray();

    public static async Task SetHoursAsync(
        HttpClient client,
        Guid venueId,
        DateOnly from,
        int opens = 6,
        int closes = 22,
        DayOfWeek? closedOn = null)
    {
        var response = await client.PutAsJsonAsync(
            $"/api/venues/{venueId}/opening-hours",
            new SetOpeningHoursRequest(from, Week(opens, closes, closedOn)));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    /// <summary>One price for every hour of every day, which is what most tests want.</summary>
    public static PriceBandRequest[] AllWeek(int from, int to, decimal baht) =>
        Enum.GetValues<DayOfWeek>()
            .Select(day => new PriceBandRequest(day.ToString(), from, to, baht))
            .ToArray();

    public static async Task<PriceListResponse> SetPricesAsync(
        HttpClient client,
        Guid venueId,
        IEnumerable<PriceBandRequest> bands) =>
        await ReadAsync<PriceListResponse>(
            await client.PutAsJsonAsync($"/api/venues/{venueId}/prices", new SetPricesRequest(bands.ToArray())));
}
