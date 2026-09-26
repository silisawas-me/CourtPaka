using System.Text.RegularExpressions;

namespace CourtBooking.Api.Tests;

/// <summary>
/// PRD 8.1 lists every event the product sends, and says the list is a contract with the sink that
/// groups by template. <see cref="EventCoverageTests"/> walks the flows and checks each name in the
/// list actually arrives; this checks the other direction, which is the one that rots. An event
/// added in a hurry and never written down is an event no dashboard will ever show, and nobody
/// finds out until somebody asks a question after a deploy and the answer is not there.
///
/// It reads the source rather than running anything, so it sees every emitter — including the ones
/// on paths no test walks.
/// </summary>
public sealed class EventNamesTests
{
    /// <summary>
    /// A name written out where it is logged. Event names are lower_snake_case and every one of
    /// them has an underscore, which is what tells them apart from the diagnostics in the same
    /// files: those are written for a person to read and start with a word or a capital.
    ///
    /// The name has to be the whole first word. A template that builds its name from a value
    /// (<c>"venue_{Status}"</c>) is not matched at all rather than matched as the piece in front of
    /// the placeholder, so those are listed in <see cref="Computed"/> instead.
    /// </summary>
    private static readonly Regex Emitted = new(
        """\.Log\w+\(\s*\$?"([a-z][a-z0-9]*(?:_[a-z0-9]+)+)(?=[ "])""",
        RegexOptions.Compiled);

    /// <summary>
    /// The names built at run time, and where from. Each is a template whose first word is not a
    /// literal, which PRD 8.1 allows only because the pieces are a closed set — so the set is
    /// spelled out here and held to the list like any other name.
    /// </summary>
    private static readonly string[] Computed =
    [
        // BookingTransitions.EventName(status), for every status in PRD 6.1.
        "booking_held",
        "booking_pending_verification",
        "booking_confirmed",
        "booking_completed",
        "booking_cancelled",
        "booking_expired",
        "booking_rejected",
        "booking_no_show",

        // BookingTransitions.SettledTemplate.
        "booking_payment_settled",

        // VenueBookingEndpoints, one per BookingArrival a venue can move to (US-24). Unconfirmed
        // is where an arrival starts, so nothing ever moves to it.
        "booking_arrival_reminded",
        "booking_arrival_confirmed",
        "booking_arrival_arrived",

        // AdminVenueEndpoints, one per VenueStatus a decision lands on (US-20).
        "venue_approved",
        "venue_rejected",
        "venue_suspended",
        "venue_pending",

        // AdminUserEndpoints (US-22).
        "user_suspended",
        "user_reinstated",

        // WaitlistOffers, on settling an offer (US-27).
        "waitlist_taken",
        "waitlist_offer_lapsed",
    ];

    /// <summary>
    /// Named in PRD 8.1 and not sent by anything yet. US-21 has not been built, and its row in the
    /// PRD is marked for that reason — the names are written down first because the invoice run is
    /// the one flow whose numbers nobody can reconstruct afterwards.
    /// </summary>
    private static readonly string[] Awaited =
    [
        "commission_invoiced",
        "commission_payment_submitted",
        "commission_paid",
    ];

    [Fact]
    public void Every_event_the_code_sends_is_one_PRD_8_1_names()
    {
        var named = NamesInPrd();
        var sending = Sending();

        // A scan that found nothing would pass while checking nothing at all, which is how a guard
        // like this stops working without anybody noticing.
        Assert.True(
            sending.Count >= 30,
            $"Only found {sending.Count} events in the source, so the scan is probably looking in "
                + "the wrong place rather than the product having lost them.");

        var unnamed = sending.Keys.Where(one => !named.Contains(one)).Order().ToArray();

        Assert.True(
            unnamed.Length == 0,
            "These are sent but PRD 8.1 does not name them, so nothing downstream will know what "
                + "they are:\n  "
                + string.Join("\n  ", unnamed.Select(one => $"{one} — {sending[one]}")));
    }

    [Fact]
    public void Every_event_PRD_8_1_names_is_one_the_code_sends()
    {
        var sending = Sending();

        var missing = NamesInPrd()
            .Where(one => !sending.ContainsKey(one) && !Awaited.Contains(one))
            .Order()
            .ToArray();

        Assert.True(
            missing.Length == 0,
            "PRD 8.1 names these and nothing sends them:\n  " + string.Join("\n  ", missing));
    }

    /// <summary>Every event name in the product's source, and the file it is sent from.</summary>
    private static Dictionary<string, string> Sending()
    {
        var sending = new Dictionary<string, string>();

        foreach (var file in Directory.EnumerateFiles(SourceRoot(), "*.cs", SearchOption.AllDirectories))
        {
            // What EF and the build write is not ours, and it is large.
            if (Generated(file))
            {
                continue;
            }

            var text = File.ReadAllText(file);

            // Only files that reach for the event logger at all. Elsewhere a lower_snake_case
            // template would be somebody's diagnostic, and those are not events.
            if (!text.Contains("AppEvents.For(", StringComparison.Ordinal))
            {
                continue;
            }

            foreach (Match match in Emitted.Matches(text))
            {
                sending[match.Groups[1].Value] = Path.GetFileName(file);
            }
        }

        foreach (var name in Computed)
        {
            sending.TryAdd(name, "built at run time");
        }

        return sending;
    }

    /// <summary>The names in PRD 8.1, read out of the backticks in its table.</summary>
    private static HashSet<string> NamesInPrd()
    {
        var prd = File.ReadAllText(Path.Combine(RepoRoot(), "docs", "prd.md"));
        var from = prd.IndexOf("### 8.1", StringComparison.Ordinal);
        Assert.True(from >= 0, "PRD 8.1 is where the list of events lives, and it is not there.");

        var until = prd.IndexOf("## 9.", from, StringComparison.Ordinal);
        var section = until < 0 ? prd[from..] : prd[from..until];

        return
        [
            .. Regex.Matches(section, "`([a-z][a-z0-9]*(?:_[a-z0-9]+)+)`")
                .Select(one => one.Groups[1].Value),
        ];
    }

    private static bool Generated(string file) =>
        file.Split(Path.DirectorySeparatorChar)
            .Any(part => part is "Migrations" or "bin" or "obj");

    private static string SourceRoot() =>
        Path.Combine(RepoRoot(), "backend", "src", "CourtBooking.Api");

    /// <summary>
    /// Walks up from wherever the test binary sits until it finds the repository. Tests run from
    /// their own bin directory, and how deep that is has changed before now.
    /// </summary>
    private static string RepoRoot()
    {
        var here = new DirectoryInfo(AppContext.BaseDirectory);

        while (here is not null && !File.Exists(Path.Combine(here.FullName, "CLAUDE.md")))
        {
            here = here.Parent;
        }

        Assert.NotNull(here);
        return here.FullName;
    }
}
