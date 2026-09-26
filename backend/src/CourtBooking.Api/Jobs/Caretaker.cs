using CourtBooking.Api.Bookings;
using CourtBooking.Api.Data;
using CourtBooking.Api.Observability;
using CourtBooking.Api.Venues;
using Microsoft.Extensions.Options;

namespace CourtBooking.Api.Jobs;

/// <summary>
/// The work nobody asks for (PRD 9.2, US-17 S-23, US-06).
///
/// Until this existed, a hold only ran out when somebody happened to read the hours it was
/// sitting on — so a hold on a quiet court could keep those hours, and its booker locked out of
/// booking again (S-22), for as long as nobody looked. The writes still release what is in their
/// way, because they must: this does not replace that, it covers the hours nobody asks about.
///
/// Each chore runs in its own scope and its own try/catch. A chore that throws must not take the
/// loop down with it — the next tick is the recovery, and a caretaker that died quietly in the
/// night is worse than the work it was doing.
/// </summary>
public sealed class Caretaker(
    IServiceScopeFactory scopes,
    TimeProvider timeProvider,
    IOptions<AppOptions> options,
    ILoggerFactory loggers) : BackgroundService
{
    private readonly ILogger<Caretaker> log = loggers.CreateLogger<Caretaker>();

    protected override async Task ExecuteAsync(CancellationToken stopping)
    {
        var every = TimeSpan.FromSeconds(options.Value.CaretakerIntervalSeconds);
        using var ticks = new PeriodicTimer(every, timeProvider);

        log.LogInformation("The caretaker is awake, and will look around every {Every}.", every);

        // The first sweep happens on the first tick rather than at startup: a deployment that
        // rolls several instances should not have all of them sweeping at the same instant.
        while (await SafeWaitAsync(ticks, stopping))
        {
            await SweepAsync(stopping);
        }
    }

    /// <summary>One pass of everything, each chore on its own.</summary>
    private async Task SweepAsync(CancellationToken stopping)
    {
        await DoAsync("holds that ran out", HoldsThatRanOutAsync, stopping);
        await DoAsync("slips about to be played", SlipsAboutToBePlayedAsync, stopping);

        // After the holds that ran out, so hours let go of this sweep are offered in it.
        await DoAsync("hours somebody was waiting for", WaitlistOffersAsync, stopping);

        // Last, so a hold this sweep let go of is told about in the same sweep.
        await DoAsync("telling bookers", TellBookersAsync, stopping);

        // Once a month this writes invoices; every other tick it finds nothing due and does
        // nothing. After the rest, because what it bills for is what the rest has settled.
        await DoAsync("the month's commission", BillTheMonthAsync, stopping);
    }

    /// <summary>
    /// Hours that came back, offered to whoever asked for them first (PRD US-27). The offer is an
    /// ordinary hold, so the booker hears about it through the same post as any other.
    /// </summary>
    private async Task WaitlistOffersAsync(
        AppDbContext database,
        IServiceProvider services,
        CancellationToken stopping) =>
        await services.GetRequiredService<WaitlistOffers>().WorkAsync(stopping);

    /// <summary>
    /// What happened to each booker's bookings since the last sweep, and the reminder before
    /// play (PRD US-06). A message arrives at most one interval after the thing it is about.
    /// </summary>
    private async Task TellBookersAsync(
        AppDbContext database,
        IServiceProvider services,
        CancellationToken stopping) =>
        await services.GetRequiredService<BookerMail>()
            .SendDueAsync(timeProvider.GetUtcNow(), stopping);

    /// <summary>
    /// The commission invoices for any month whose cut-off has passed and which nobody has
    /// billed yet (PRD US-21, BR-08). Almost every tick finds nothing to do.
    /// </summary>
    private async Task BillTheMonthAsync(
        AppDbContext database,
        IServiceProvider services,
        CancellationToken stopping) =>
        await services.GetRequiredService<MonthlyBilling>()
            .IssueDueAsync(timeProvider.GetUtcNow(), stopping);

    /// <summary>
    /// Hours held by a booking whose fifteen minutes are up, given back (PRD BR-02, 9.2). The
    /// booking moves to Expired and the move is recorded, the same as when a write trips over it.
    /// </summary>
    private async Task HoldsThatRanOutAsync(
        AppDbContext database,
        IServiceProvider services,
        CancellationToken stopping)
    {
        var expired = await BookedSlots.ReleaseAllLapsedAsync(
            database, timeProvider.GetUtcNow(), stopping);

        foreach (var bookingId in expired)
        {
            AppEvents.For(loggers).LogInformation(
                "booking_expired {BookingId} {By}", bookingId, "caretaker");
        }
    }

    /// <summary>
    /// A slip nobody has looked at, on hours about to be played (PRD US-17, S-23). The venue is
    /// told once — the row is claimed before the message goes out, so a second instance, or the
    /// next tick, finds nothing left to send rather than sending it again.
    /// </summary>
    private async Task SlipsAboutToBePlayedAsync(
        AppDbContext database,
        IServiceProvider services,
        CancellationToken stopping)
    {
        var now = timeProvider.GetUtcNow();
        var notifications = services.GetRequiredService<VenueNotifications>();

        var waiting = await WaitingSlips.AboutToBePlayedAsync(database, now, stopping);

        foreach (var (venueId, bookingId) in waiting)
        {
            await notifications.SlipStillWaitingAsync(venueId, bookingId);
            AppEvents.For(loggers).LogInformation(
                "slip_reminder_sent {BookingId} {VenueId}", bookingId, venueId);
        }
    }

    private async Task DoAsync(
        string chore,
        Func<AppDbContext, IServiceProvider, CancellationToken, Task> work,
        CancellationToken stopping)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            await work(
                scope.ServiceProvider.GetRequiredService<AppDbContext>(),
                scope.ServiceProvider,
                stopping);
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested)
        {
            // Shutting down. Not a failure, and nothing to say about it.
        }
        catch (Exception failure)
        {
            log.LogError(failure, "The caretaker could not finish {Chore} this time.", chore);
        }
    }

    /// <summary>Waiting for the next tick, where being stopped is an answer rather than a throw.</summary>
    private static async Task<bool> SafeWaitAsync(PeriodicTimer ticks, CancellationToken stopping)
    {
        try
        {
            return await ticks.WaitForNextTickAsync(stopping);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}
