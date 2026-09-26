using CourtBooking.Api.Data;
using CourtBooking.Api.Http;
using CourtBooking.Api.Localization;
using CourtBooking.Api.Observability;
using CourtBooking.Api.Venues;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace CourtBooking.Api.Bookings;

/// <summary>
/// Hours sold in advance (PRD US-31).
///
/// Two different things live here. The board — what a venue offers — is a setting, and is changed
/// the way every other price is: by putting up a new one, never by editing the old, because
/// packages already sold point at the terms they were sold on (BR-05). The packages themselves are
/// money and customers, so they sit behind the permission the rest of the day's money sits behind.
///
/// Selling one is money in on the day it is sold, written as an ordinary <see cref="PaymentReceipt"/>
/// so the till count and the day's takings see it without being taught about packages. What it is
/// not is revenue: until the hours are spent the venue owes them, and the number that says how many
/// it owes is on the dashboard (⚠️ S-27).
/// </summary>
public static class PackageEndpoints
{
    public static void MapPackageEndpoints(this RouteGroupBuilder venue)
    {
        var packages = venue.MapGroup("/packages");

        // Reading what has been sold names the customers who bought it, so it is behind the
        // permission the day's list is behind (PDPA, as PRD US-13). Still open at a suspended
        // venue: hours somebody has already paid for are hours the venue still owes them.
        packages.RequireAuthorization(
            VenuePolicies.NeedsEvenWhenSuspended(VenuePermissions.ManageBookings));

        packages.MapGet("/", SoldAsync);

        // Selling is selling, which is the thing a suspension stops (PRD US-20).
        packages.MapPost("/", SellAsync)
            .RequireAuthorization(VenuePolicies.Needs(VenuePermissions.ManageBookings));

        // The board is a setting, like a price list (PRD US-14). Reading it is part of selling,
        // so it stays with this group; changing it is not.
        packages.MapGet("/types", BoardAsync);
        packages.MapPost("/types", OfferAsync)
            .RequireAuthorization(VenuePolicies.Needs(VenuePermissions.ManageSettings));
        packages.MapPost("/types/{typeId:guid}/withdraw", WithdrawAsync)
            .RequireAuthorization(VenuePolicies.Needs(VenuePermissions.ManageSettings));
    }

    /// <summary>What this venue offers. Withdrawn offers come too, marked, because a package that
    /// was sold from one is still being spent and somebody will ask what it was.</summary>
    private static async Task<Ok<PackageTypeResponse[]>> BoardAsync(
        Guid venueId,
        AppDbContext database,
        CancellationToken cancellationToken) =>
        TypedResults.Ok(
            await database.PackageTypes
                .AsNoTracking()
                .Where(one => one.VenueId == venueId)
                .OrderBy(one => one.WithdrawnAt != null)
                .ThenBy(one => one.PriceBaht)
                .Select(one => new PackageTypeResponse(
                    one.Id,
                    one.Name,
                    one.Hours,
                    one.PriceBaht,
                    Packages.PerHour(one.PriceBaht, one.Hours),
                    one.ValidForDays,
                    one.WithdrawnAt))
                .ToArrayAsync(cancellationToken));

    /// <summary>Puts an offer on the board.</summary>
    private static async Task<Results<Created<PackageTypeResponse>, ProblemHttpResult>> OfferAsync(
        Guid venueId,
        PackageTypeRequest request,
        CurrentVenue venue,
        AppDbContext database,
        TimeProvider timeProvider,
        ILoggerFactory loggers,
        CancellationToken cancellationToken)
    {
        var membership = venue.Require();

        if (venue.Status != VenueStatus.Approved)
        {
            return ApiProblem.Of(StatusCodes.Status403Forbidden, VenueErrorCodes.NotApproved);
        }

        var hours = request.Hours ?? 0;
        var price = request.PriceBaht ?? 0m;
        var days = request.ValidForDays ?? 0;

        if (Packages.Refusal(request.Name, hours, price, days) is { } wrong)
        {
            return ApiProblem.Of(StatusCodes.Status400BadRequest, wrong);
        }

        var offer = new PackageType
        {
            VenueId = venueId,
            Name = request.Name!.Trim(),
            Hours = hours,
            PriceBaht = price,
            ValidForDays = days,
            CreatedAt = timeProvider.GetUtcNow(),
            CreatedByUserId = membership.UserId,
        };

        database.PackageTypes.Add(offer);
        await database.SaveChangesAsync(cancellationToken);

        AppEvents.For(loggers).LogInformation(
            "package_offered {TypeId} {VenueId} {Hours} {PriceBaht}",
            offer.Id,
            venueId,
            offer.Hours,
            offer.PriceBaht);

        return TypedResults.Created(
            $"/api/venues/{venueId}/packages/types",
            Drawn(offer));
    }

    /// <summary>
    /// Takes an offer off the board. One way: an offer is replaced, not restored, which is what
    /// keeps a sold package's terms readable (BR-05).
    /// </summary>
    private static async Task<Results<Ok<PackageTypeResponse>, ProblemHttpResult>> WithdrawAsync(
        Guid venueId,
        Guid typeId,
        CurrentVenue venue,
        AppDbContext database,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var membership = venue.Require();
        var now = timeProvider.GetUtcNow();

        var withdrawn = await database.PackageTypes
            .Where(one => one.Id == typeId && one.VenueId == venueId && one.WithdrawnAt == null)
            .ExecuteUpdateAsync(
                set => set
                    .SetProperty(one => one.WithdrawnAt, now)
                    .SetProperty(one => one.WithdrawnByUserId, membership.UserId),
                cancellationToken);

        var offer = await database.PackageTypes
            .AsNoTracking()
            .SingleOrDefaultAsync(
                one => one.Id == typeId && one.VenueId == venueId, cancellationToken);

        if (offer is null)
        {
            return ApiProblem.Of(StatusCodes.Status404NotFound, PackageErrorCodes.TypeUnknown);
        }

        return withdrawn == 0
            ? ApiProblem.Of(
                StatusCodes.Status409Conflict, PackageErrorCodes.TypeAlreadyWithdrawn)
            : TypedResults.Ok(Drawn(offer));
    }

    /// <summary>
    /// What this venue has sold, newest first, with what is left on each. The balance is the sum
    /// of the movements rather than a number anybody keeps (PRD US-31).
    ///
    /// Everything still worth something, and only the last few that are finished with. Packages
    /// are kept for ever, and a year of spent ones under the ones being spent buries the list.
    /// </summary>
    private static async Task<Ok<HourPackageResponse[]>> SoldAsync(
        Guid venueId,
        AppDbContext database,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var today = PlatformRequirements.BangkokToday(timeProvider);

        var live = await SoldRowsAsync(
            database.HourPackages.Where(one =>
                one.VenueId == venueId
                && one.ExpiredAt == null
                && one.Entries.Sum(entry => (int?)entry.Hours) > 0),
            today,
            cancellationToken);

        var finished = await SoldRowsAsync(
            database.HourPackages
                .Where(one =>
                    one.VenueId == venueId
                    && (one.ExpiredAt != null
                        || (one.Entries.Sum(entry => (int?)entry.Hours) ?? 0) <= 0))
                .OrderByDescending(one => one.SoldAt)
                .Take(Packages.FinishedShown),
            today,
            cancellationToken);

        return TypedResults.Ok<HourPackageResponse[]>([.. live, .. finished]);
    }

    /// <summary>
    /// Sells one. The money is in the till the moment it is written down, so it is a receipt like
    /// any other and the day's count sees it (PRD US-26) — but it is not revenue until the hours
    /// are spent, which is why nothing here touches a booking (⚠️ S-27).
    /// </summary>
    private static async Task<Results<Created<HourPackageResponse>, ProblemHttpResult>> SellAsync(
        Guid venueId,
        SellPackageRequest request,
        CurrentVenue venue,
        AppDbContext database,
        TimeProvider timeProvider,
        ILoggerFactory loggers,
        CancellationToken cancellationToken)
    {
        var membership = venue.Require();

        if (venue.Status != VenueStatus.Approved)
        {
            return ApiProblem.Of(StatusCodes.Status403Forbidden, VenueErrorCodes.NotApproved);
        }

        var name = request.CustomerName?.Trim();
        if (string.IsNullOrEmpty(name) || name.Length > Booking.CustomerNameMaxLength)
        {
            return ApiProblem.Of(
                StatusCodes.Status400BadRequest, BookingErrorCodes.InvalidCustomerName);
        }

        var phone = request.CustomerPhone?.Trim();
        if (!string.IsNullOrEmpty(phone)
            && (phone.Length > Booking.CustomerPhoneMaxLength
                || !phone.All(letter => char.IsAsciiDigit(letter) || letter is '+' or '-' or ' ')))
        {
            return ApiProblem.Of(
                StatusCodes.Status400BadRequest, BookingErrorCodes.InvalidCustomerPhone);
        }

        // The names and nothing else, the way every other door here reads an enum.
        if (request.PaidBy is not { } paidBy
            || !Enum.GetNames<PaymentMethod>().Contains(paidBy, StringComparer.Ordinal))
        {
            return ApiProblem.Of(
                StatusCodes.Status400BadRequest, BookingErrorCodes.InvalidCounterPayment);
        }

        // Only what is on the board now. An offer that has been withdrawn is one the venue has
        // stopped making, and selling from it would be selling terms nobody is offering.
        var offer = await database.PackageTypes
            .AsNoTracking()
            .SingleOrDefaultAsync(
                one => one.Id == request.PackageTypeId
                    && one.VenueId == venueId
                    && one.WithdrawnAt == null,
                cancellationToken);

        if (offer is null)
        {
            return ApiProblem.Of(StatusCodes.Status404NotFound, PackageErrorCodes.TypeUnknown);
        }

        var now = timeProvider.GetUtcNow();
        var today = PlatformRequirements.BangkokToday(timeProvider);

        var package = new HourPackage
        {
            VenueId = venueId,
            PackageTypeId = offer.Id,
            CustomerName = name,
            CustomerPhone = string.IsNullOrEmpty(phone) ? null : phone,

            // Copied in, not looked up later: the board can change tomorrow (BR-05).
            HoursSold = offer.Hours,
            PriceBaht = offer.PriceBaht,
            ExpiresOn = today.AddDays(offer.ValidForDays),
            SoldAt = now,
            SoldByUserId = membership.UserId,
        };

        // The hours going in, and the money coming in, written together. The first row of the
        // ledger is the sale itself — a balance with no row saying where it came from is a
        // number somebody has to be trusted on.
        database.HourPackages.Add(package);
        database.PackageEntries.Add(new PackageEntry
        {
            PackageId = package.Id,
            Hours = package.HoursSold,
            Move = PackageMove.Sold,
            At = now,
            ByUserId = membership.UserId,
        });

        database.PaymentReceipts.Add(new PaymentReceipt
        {
            PackageId = package.Id,
            VenueId = venueId,
            AmountBaht = package.PriceBaht,
            Method = Enum.Parse<PaymentMethod>(paidBy),
            ReceivedAt = now,
            ReceivedByUserId = membership.UserId,
        });

        await database.SaveChangesAsync(cancellationToken);

        AppEvents.For(loggers).LogInformation(
            "package_sold {PackageId} {VenueId} {Hours} {PriceBaht} {Method}",
            package.Id,
            venueId,
            package.HoursSold,
            package.PriceBaht,
            paidBy);

        return TypedResults.Created(
            $"/api/venues/{venueId}/packages",
            await OneAsync(database, venueId, package.Id, today, cancellationToken));
    }

    /// <summary>
    /// Spends a package's hours on a booking the venue is holding (PRD US-31). It pays for the
    /// booking whole or not at all: hours buy court-hours one for one, and a booking half paid in
    /// hours and half in money is two answers to what is owed.
    ///
    /// Nothing is written to the till. The money came in when the package was sold, and a receipt
    /// here would count it a second time on a day it did not arrive.
    /// </summary>
    internal static async Task<Results<Ok<VenueBookingResponse>, ProblemHttpResult>> SpendAsync(
        Guid venueId,
        Guid bookingId,
        SpendPackageRequest request,
        CurrentVenue venue,
        AppDbContext database,
        TimeProvider timeProvider,
        ILoggerFactory loggers,
        CancellationToken cancellationToken)
    {
        var membership = venue.Require();
        var now = timeProvider.GetUtcNow();
        var today = PlatformRequirements.BangkokToday(timeProvider);

        var booking = await VenueBookingEndpoints.OneAsync(database, venueId, bookingId)
            .SingleOrDefaultAsync(cancellationToken);

        if (booking is null)
        {
            return ApiProblem.Of(StatusCodes.Status404NotFound, BookingErrorCodes.NotFound);
        }

        var stored = booking.Status;
        var status = BookedSlots.StatusAt(booking, now);

        // The bookings a package may pay for: the ones the venue is going to honour or has played,
        // sold at its own counter. An online booking is paid for the way it was started, and a
        // hold has two ways out that do not include this one (PRD 6.1).
        if (booking.Channel != BookingChannel.Staff || !Takings.StillOwing.Contains(status))
        {
            return ApiProblem.Of(
                StatusCodes.Status409Conflict, PackageErrorCodes.CannotPayForThat);
        }

        await using var transaction =
            await database.Database.BeginTransactionAsync(cancellationToken);

        // Behind the same lock the desk takes before it reads what is owed (PRD US-26): money
        // landing at this moment is either counted here or waits for this to finish.
        await CounterMoneyEndpoints.QueueForTheMoneyAsync(database, bookingId, cancellationToken);

        var taken = await CounterMoneyEndpoints.TakenAsync(database, bookingId, cancellationToken);
        if (taken > 0m || booking.PackageId is not null
            || booking.PaymentState == PaymentState.Received)
        {
            await transaction.RollbackAsync(cancellationToken);
            return ApiProblem.Of(
                StatusCodes.Status409Conflict, PackageErrorCodes.AlreadyPaidFor);
        }

        var package = await database.HourPackages
            .AsNoTracking()
            .SingleOrDefaultAsync(
                one => one.Id == request.PackageId && one.VenueId == venueId, cancellationToken);

        if (package is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return ApiProblem.Of(StatusCodes.Status404NotFound, PackageErrorCodes.NotFound);
        }

        if (package.ExpiresOn < today || package.ExpiredAt is not null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return ApiProblem.Of(StatusCodes.Status409Conflict, PackageErrorCodes.RunOut);
        }

        var hours = booking.Slots.Count;
        var left = await BalanceAsync(database, package.Id, cancellationToken);

        if (left < hours)
        {
            await transaction.RollbackAsync(cancellationToken);
            return ApiProblem.Of(
                StatusCodes.Status409Conflict, PackageErrorCodes.NotEnoughHours);
        }

        var worth = Packages.Worth(package.PriceBaht, package.HoursSold, hours);

        // Conditional on the booking being where it was read: two people spending two packages on
        // one booking at the same moment must not both succeed, or the venue has taken twice for
        // the same hours.
        var paid = await database.Bookings
            .Where(candidate =>
                candidate.Id == bookingId
                && candidate.VenueId == venueId
                && candidate.Status == stored
                && candidate.PackageId == null
                && candidate.PaymentState != PaymentState.Received)
            .ExecuteUpdateAsync(
                set => set
                    .SetProperty(candidate => candidate.PackageId, package.Id)
                    .SetProperty(candidate => candidate.PackageHours, hours)
                    .SetProperty(candidate => candidate.PackageBaht, worth)
                    .SetProperty(candidate => candidate.PaymentState, PaymentState.Received),
                cancellationToken);

        if (paid == 0)
        {
            await transaction.RollbackAsync(cancellationToken);
            return ApiProblem.Of(
                StatusCodes.Status409Conflict, BookingErrorCodes.ChangedMeanwhile);
        }

        database.PackageEntries.Add(new PackageEntry
        {
            PackageId = package.Id,
            Hours = -hours,
            Move = PackageMove.Used,
            BookingId = bookingId,
            At = now,
            ByUserId = membership.UserId,
        });

        // Paying for a booking in full settles the booking as well as its money, the same as the
        // last of the cash arriving does (PRD US-26).
        if (status != stored)
        {
            database.BookingStatusChanges.Add(
                BookingTransitions.Record(bookingId, stored, status, null, now));
        }

        await database.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        AppEvents.For(loggers).LogInformation(
            "package_hours_used {PackageId} {BookingId} {Hours} {Baht}",
            package.Id,
            bookingId,
            hours,
            worth);

        return TypedResults.Ok(
            await VenueBookingEndpoints.OneDrawnAsync(
                database, venueId, bookingId, venue, now, cancellationToken));
    }

    /// <summary>
    /// Hands hours back when a booking a package paid for is let go (PRD US-31, BR-06). The share
    /// is the booking's own cancellation terms, rounded up — the money is not coming back, so the
    /// only thing that can be given is what was spent.
    ///
    /// Called from inside the transaction that ends the booking, so the hours and the ending are
    /// one write: a booking cancelled with its hours left spent is a customer out of pocket.
    /// </summary>
    internal static void GiveHoursBack(
        AppDbContext database,
        Booking booking,
        int refundPercent,
        Guid? byUserId,
        DateTimeOffset at)
    {
        if (booking.PackageId is not { } packageId || booking.PackageHours <= 0)
        {
            return;
        }

        var hours = Packages.HoursBack(booking.PackageHours, refundPercent);
        if (hours <= 0)
        {
            return;
        }

        database.PackageEntries.Add(new PackageEntry
        {
            PackageId = packageId,
            Hours = hours,
            Move = PackageMove.GivenBack,
            BookingId = booking.Id,
            At = at,
            ByUserId = byUserId,
        });
    }

    /// <summary>What a package has left: the sum of its movements, and nothing else.</summary>
    internal static async Task<int> BalanceAsync(
        AppDbContext database,
        Guid packageId,
        CancellationToken cancellationToken) =>
        await database.PackageEntries
            .Where(entry => entry.PackageId == packageId)
            .SumAsync(entry => (int?)entry.Hours, cancellationToken) ?? 0;

    private static async Task<HourPackageResponse> OneAsync(
        AppDbContext database,
        Guid venueId,
        Guid packageId,
        DateOnly today,
        CancellationToken cancellationToken) =>
        (await SoldRowsAsync(
            database.HourPackages.Where(one => one.Id == packageId && one.VenueId == venueId),
            today,
            cancellationToken))
        .Single();

    /// <summary>The packages a query picked out, the way the venue reads them.</summary>
    private static async Task<HourPackageResponse[]> SoldRowsAsync(
        IQueryable<HourPackage> asked,
        DateOnly today,
        CancellationToken cancellationToken)
    {
        var sold = await asked
            .AsNoTracking()
            .OrderByDescending(one => one.SoldAt)
            .Select(one => new
            {
                Package = one,
                TypeName = one.Type!.Name,
                Left = one.Entries.Sum(entry => (int?)entry.Hours) ?? 0,
                Moves = one.Entries
                    .OrderBy(entry => entry.At)
                    .ThenBy(entry => entry.Id)
                    .Select(entry => new PackageMoveResponse(
                        entry.Hours, entry.Move.ToString(), entry.BookingId, entry.At))
                    .ToList(),
            })
            .ToListAsync(cancellationToken);

        return
        [
            .. sold.Select(one => new HourPackageResponse(
                one.Package.Id,
                one.Package.PackageTypeId,
                one.TypeName,
                one.Package.CustomerName,
                one.Package.CustomerPhone,
                one.Package.HoursSold,
                one.Package.PriceBaht,
                Packages.PerHour(one.Package.PriceBaht, one.Package.HoursSold),
                one.Left,
                one.Package.ExpiresOn,
                // Only what somebody can still act on: hours that have run out are not a call to
                // make, and a package spent to nothing is not either.
                one.Left > 0
                    && one.Package.ExpiredAt is null
                    && one.Package.ExpiresOn >= today
                    && one.Package.ExpiresOn <= today.AddDays(Packages.RunningOutWithinDays),
                one.Package.ExpiredAt,
                one.Package.SoldAt,
                [.. one.Moves])),
        ];
    }

    private static PackageTypeResponse Drawn(PackageType offer) =>
        new(
            offer.Id,
            offer.Name,
            offer.Hours,
            offer.PriceBaht,
            Packages.PerHour(offer.PriceBaht, offer.Hours),
            offer.ValidForDays,
            offer.WithdrawnAt);
}
