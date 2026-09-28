using CourtBooking.Api.Bookings;
using CourtBooking.Api.Identity;
using CourtBooking.Api.Localization;
using CourtBooking.Api.Venues;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace CourtBooking.Api.Data;

/// <summary>
/// The four branches of the owner app's design (อารีย์ · ทองหล่อ · บางนา · พระราม 9) with the
/// design's own data: its prices (180 / 260 / 320), its members, its shop, the evening it draws
/// at อารีย์ block for block, and the other branches filled to the design's hourly percentages —
/// plus a fortnight of trade behind them so revenue has days to draw (docs/plan/owner-overview.md).
/// </summary>
/// <remarks>
/// Only ever runs when App:SeedMockData is on AND the host is Development — the same two locks as
/// <see cref="DevelopmentSeeder"/>. Rows are written straight to the context, because the doors
/// refuse what a history needs: an hour that is over cannot be sold at the counter.
///
/// Each day at each branch is filled once — a day with a booking already is left alone — so a
/// restart tops up the days that have come since and never doubles one. DEV01 is not touched:
/// the verify scripts own it. Everything belongs to <see cref="DemoEmail"/>, which owns nothing
/// else, so the owner app looks the way the design draws it.
/// </remarks>
public static class DevelopmentMockData
{
    /// <summary>The account that owns the four branches (password as the seed's).</summary>
    public const string DemoEmail = "demo@courtpaka.local";

    private const int DaysBack = 13;
    private const int OpensHour = 8;
    private const int ClosesHour = 23;

    private sealed record Branch(string Code, string Name, string District, int Courts, double[] Evening);

    // The design's hourly use from 14:00 to 22:00, branch by branch (its overview heat map).
    private static readonly Branch[] Branches =
    [
        new("ARI01", "อารีย์", "พญาไท", 6, [.5, .5, .67, .83, 1, 1, 1, .83, .5]),
        new("TLO01", "ทองหล่อ", "วัฒนา", 6, [.33, .5, .5, .67, .83, 1, 1, 1, .67]),
        new("BNA01", "บางนา", "บางนา", 12, [.17, .17, .25, .5, .75, .92, .83, .58, .33]),
        new("RM901", "พระราม 9", "ห้วยขวาง", 10, [.4, .4, .6, .8, 1, 1, .9, .7, .4]),
    ];

    /// <summary>The design's three price tiers: quiet, normal, peak.</summary>
    private static decimal PriceAt(DayOfWeek day, int hour) =>
        day is DayOfWeek.Saturday or DayOfWeek.Sunday
            ? hour >= 18 && hour < 21 ? 320m : hour >= 10 ? 260m : 180m
            : hour >= 17 ? 260m : 180m;

    // The people who book through the app, by the name the counter calls out.
    private static readonly string[] AppPeople =
    [
        "คุณต้น", "คุณแพร", "คุณบีม", "คุณจิ๊บ", "คุณเมย์ +3", "คุณโอ๊ต", "คุณนุ่น", "คุณฝน", "คุณมิ้นท์",
        "คุณเจ", "คุณแนน", "คุณกอล์ฟ", "คุณเบียร์", "คุณปอนด์", "คุณพลอย", "คุณฟ้า",
    ];

    private static readonly string[] WalkInNames =
    [
        "Walk-in", "คุณกิ๊ฟ", "คุณบอส", "คุณเอ็ม", "คุณภูมิ", "คุณข้าว", "คุณตาล", "คุณนิว", "คุณจูน",
    ];

    // The design's members: name, phone, plan, hours used, hours bought, days to expiry.
    private static readonly (string Name, string Phone, int Plan, int Used, int Hours, int Days)[] Members =
    [
        ("คุณเมย์ ศรีสุข", "081-234-5521", 0, 6, 8, 31),
        ("ชมรม KU", "089-777-1200", 2, 38, 48, 76),
        ("คุณโอ๊ต ธนา", "062-118-9034", 1, 15, 16, 3),
        ("คุณแพร วงศ์ใหญ่", "085-990-2211", 0, 3, 8, 51),
        ("ทีม Office", "02-555-0199", 3, 20, 32, 2),
        ("คุณบีม กิตติ", "091-443-7788", 1, 11, 16, 69),
        ("คุณจิ๊บ นภา", "080-321-6655", 0, 8, 8, 43),
    ];

    private static readonly (string Name, int Hours, decimal Baht, int Days)[] Plans =
    [
        ("รายเดือน 8 ชม.", 8, 1_400m, 30),
        ("รายเดือน 16 ชม.", 16, 2_600m, 30),
        ("ชมรม / ทีม 48 ชม.", 48, 7_200m, 90),
        ("ชมรม / ทีม 32 ชม.", 32, 5_000m, 60),
    ];

    private static readonly (string Name, decimal Baht, string Unit)[] Goods =
    [
        ("ลูกแบด (หลอด 12 ลูก)", 850m, "หลอด"),
        ("ลูกแบด (ลูกละ)", 75m, "ลูก"),
        ("น้ำดื่ม", 15m, "ขวด"),
        ("เกลือแร่", 25m, "ขวด"),
    ];

    /// <summary>A booking of the design, as it stands on a court.</summary>
    private enum Kind { App, Walk, Group, Member }

    private sealed record Planned(int Court, int From, int Until, string Name, Kind Kind, bool Waits, bool Unpaid);

    // The evening the design draws at อารีย์, block for block (half hours rounded to the hour).
    private static readonly Planned[] AriToday =
    [
        new(1, 14, 16, "คุณต้น", Kind.App, false, false),
        new(1, 16, 18, "ก๊วนเย็นวันพุธ", Kind.Group, false, false),
        new(1, 18, 20, "คุณแพร", Kind.App, false, false),
        new(1, 20, 22, "ทีม Office", Kind.Member, true, false),
        new(2, 15, 17, "Walk-in", Kind.Walk, false, false),
        new(2, 18, 19, "คุณบีม", Kind.App, false, false),
        new(2, 19, 21, "คุณจิ๊บ", Kind.App, true, false),
        new(3, 17, 19, "ก๊วนมือใหม่ 7/8", Kind.Group, false, false),
        new(3, 19, 21, "คุณเมย์ +3", Kind.App, true, true),
        new(4, 18, 21, "ชมรม KU", Kind.Member, false, false),
        new(4, 21, 23, "คุณโอ๊ต", Kind.App, true, false),
        new(5, 14, 15, "คุณนุ่น", Kind.App, false, false),
        new(5, 19, 20, "คุณกิ๊ฟ", Kind.Walk, true, true),
        new(6, 18, 22, "ก๊วนขาประจำ 12/12", Kind.Group, false, false),
    ];

    public static async Task SeedAsync(IServiceProvider services, CancellationToken cancellationToken = default)
    {
        using var scope = services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        var time = scope.ServiceProvider.GetRequiredService<TimeProvider>();
        var options = scope.ServiceProvider.GetRequiredService<IOptions<AppOptions>>();
        var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>()
            .CreateLogger(typeof(DevelopmentMockData));

        var owner = await DevelopmentSeeder.EnsureUserAsync(users, DemoEmail);
        var bookers = await EnsureBookersAsync(users, cancellationToken);
        var now = time.GetUtcNow();
        var today = PlatformRequirements.BangkokToday(time);

        foreach (var branch in Branches)
        {
            // Made-up data must never be why a local API does not start: a branch that fails is
            // logged and skipped, and the next start tries it again.
            try
            {
                var venue = await EnsureVenueAsync(
                    database, branch, owner.Id, options.Value, today, now, cancellationToken);
                await new Filler(database, venue, branch, owner.Id, bookers, today, now)
                    .RunAsync(cancellationToken);
            }
            catch (Exception failure) when (failure is not OperationCanceledException)
            {
                logger.LogWarning(failure, "Mock data for {Branch} was not written", branch.Code);
                database.ChangeTracker.Clear();
            }
        }
    }

    /// <summary>
    /// App bookers, named. Their addresses are not confirmed, so the booker mail never writes to
    /// them — these are people who do not exist.
    /// </summary>
    private static async Task<Dictionary<string, Guid>> EnsureBookersAsync(
        UserManager<AppUser> users,
        CancellationToken cancellationToken)
    {
        var found = new Dictionary<string, Guid>();
        for (var index = 0; index < AppPeople.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var email = $"booker{index + 1:00}@mock.badpaka.test";
            var user = await users.FindByEmailAsync(email);
            if (user is null)
            {
                user = new AppUser
                {
                    UserName = email,
                    Email = email,
                    EmailConfirmed = false,
                    DisplayName = AppPeople[index],
                    Language = SupportedLanguages.Thai,
                };
                var made = await users.CreateAsync(user);
                if (!made.Succeeded)
                {
                    throw new InvalidOperationException(
                        $"Could not make {email}: {string.Join(", ", made.Errors.Select(error => error.Description))}");
                }
            }

            found[AppPeople[index]] = user.Id;
        }

        return found;
    }

    private static async Task<Venue> EnsureVenueAsync(
        AppDbContext database,
        Branch branch,
        Guid ownerId,
        AppOptions options,
        DateOnly today,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var venue = await database.Venues.SingleOrDefaultAsync(v => v.Code == branch.Code, cancellationToken);
        if (venue is not null)
        {
            return venue;
        }

        var opened = today.AddDays(-DaysBack - 60);
        venue = new Venue
        {
            Code = branch.Code,
            Name = branch.Name,
            AddressLine = $"badPaka {branch.Name}",
            District = branch.District,
            Province = "กรุงเทพมหานคร",
            Status = VenueStatus.Approved,
            Business = new VenueBusiness
            {
                PromptPayId = "0812345678",
                PromptPayAccountName = "บริษัท แบดปะก้า จำกัด",
                IsVatRegistered = true,
                LegalName = "บริษัท แบดปะก้า จำกัด",
                TaxId = "0105561000000",
                TaxBranch = VenueBusiness.HeadOfficeBranch,
                BillingAddress = $"badPaka {branch.Name} เขต{branch.District} กรุงเทพมหานคร",
            },
            AgreementVersion = options.VenueAgreementVersion,
            AgreementAcceptedAt = now,
            AgreementAcceptedByUserId = ownerId,
            CreatedAt = now,
        };
        database.Venues.Add(venue);
        database.VenueMemberships.Add(new VenueMembership
        {
            VenueId = venue.Id,
            UserId = ownerId,
            Role = VenueRole.Owner,
            Permissions = VenuePermissions.None,
            CreatedAt = now,
        });

        for (var index = 0; index < branch.Courts; index++)
        {
            var (court, status) = Court.Open(venue.Id, $"คอร์ท {index + 1}", index, ownerId, opened, now);
            database.Courts.Add(court);
            database.CourtStatusChanges.Add(status);
        }

        var week = Enum.GetValues<DayOfWeek>();
        database.OpeningHoursSchedules.Add(OpeningHoursSchedule.Create(
            venue.Id, opened, week.Select(day => new WeekdayHours(day, OpensHour, ClosesHour)), ownerId, now));

        // One band per run of hours at one price, as the pricing page saves them.
        var bands = new List<BandHours>();
        foreach (var day in week)
        {
            var from = OpensHour;
            for (var hour = OpensHour + 1; hour <= ClosesHour; hour++)
            {
                if (hour == ClosesHour || PriceAt(day, hour) != PriceAt(day, from))
                {
                    bands.Add(new BandHours(day, from, hour, PriceAt(day, from)));
                    from = hour;
                }
            }
        }

        database.PriceLists.Add(PriceList.Create(venue.Id, bands, ownerId, now));
        database.CancellationPolicies.Add(CancellationPolicy.Create(
            venue.Id, CancellationPolicy.Default, ownerId, now));

        await database.SaveChangesAsync(cancellationToken);
        return venue;
    }

    /// <summary>One branch's days, written one day at a time.</summary>
    private sealed class Filler(
        AppDbContext database,
        Venue venue,
        Branch branch,
        Guid ownerId,
        Dictionary<string, Guid> bookers,
        DateOnly today,
        DateTimeOffset now)
    {
        private List<Court> courts = [];
        private Guid policyId;
        private ShopItem[] goods = [];
        private List<Balance> packages = [];
        private readonly Dictionary<string, BookingSeries> groups = [];

        public async Task RunAsync(CancellationToken cancellationToken)
        {
            courts = await database.Courts
                .Where(court => court.VenueId == venue.Id)
                .OrderBy(court => court.Position)
                .ToListAsync(cancellationToken);
            policyId = await database.CancellationPolicies
                .Where(policy => policy.VenueId == venue.Id)
                .OrderByDescending(policy => policy.CreatedAt)
                .Select(policy => policy.Id)
                .FirstAsync(cancellationToken);

            goods = await EnsureShopAsync(cancellationToken);
            packages = await EnsurePackagesAsync(cancellationToken);

            var first = today.AddDays(-DaysBack);
            var since = PlatformRequirements.BangkokHour(first, 0);
            var filled = (await database.BookingSlots
                    .Where(slot => slot.Court!.VenueId == venue.Id
                        && slot.StartsAt >= since
                        && slot.Booking!.SeriesId == null)
                    .Select(slot => slot.StartsAt)
                    .ToListAsync(cancellationToken))
                .Select(DayOf)
                .ToHashSet();

            for (var date = first; date <= today; date = date.AddDays(1))
            {
                if (filled.Contains(date))
                {
                    continue;
                }

                var random = new Random(HashCode.Combine(branch.Code, date.DayNumber));
                var taken = new HashSet<(int Court, int Hour)>();

                if (date == today && branch.Code == "ARI01")
                {
                    await DesignedEveningAsync(date, random, taken, cancellationToken);
                }
                else if (date == today)
                {
                    await GroupsTodayAsync(date, random, taken, cancellationToken);
                }

                FillDay(date, random, taken, date == today);
                await database.SaveChangesAsync(cancellationToken);
            }
        }

        /// <summary>The design's อารีย์ evening, block for block, and its shut court.</summary>
        private async Task DesignedEveningAsync(
            DateOnly date,
            Random random,
            HashSet<(int, int)> taken,
            CancellationToken cancellationToken)
        {
            database.CourtClosures.Add(new CourtClosure
            {
                CourtId = courts[5].Id,
                StartsAt = PlatformRequirements.BangkokHour(date, 14),
                EndsAt = PlatformRequirements.BangkokHour(date, 17),
                Reason = "ปิดซ่อมพื้น",
                CreatedByUserId = ownerId,
                CreatedAt = now,
            });
            for (var hour = 14; hour < 17; hour++)
            {
                taken.Add((6, hour));
            }

            foreach (var one in AriToday)
            {
                await WriteAsync(one, date, random, cancellationToken);
                for (var hour = one.From; hour < one.Until; hour++)
                {
                    taken.Add((one.Court, hour));
                }
            }
        }

        /// <summary>Two standing groups tonight at the other branches.</summary>
        private async Task GroupsTodayAsync(
            DateOnly date,
            Random random,
            HashSet<(int, int)> taken,
            CancellationToken cancellationToken)
        {
            Planned[] tonight =
            [
                new(courts.Count, 18, 20, "ก๊วนเย็น" + branch.Name, Kind.Group, false, false),
                new(courts.Count - 1, 19, 22, "ก๊วนขาประจำ", Kind.Group, false, false),
            ];
            foreach (var one in tonight)
            {
                await WriteAsync(one, date, random, cancellationToken);
                for (var hour = one.From; hour < one.Until; hour++)
                {
                    taken.Add((one.Court, hour));
                }
            }
        }

        /// <summary>
        /// The rest of a day: from 14:00 to 22:00 as many courts as the design's percentage says,
        /// quieter either side of it; a fortnight ago a little quieter than now.
        /// </summary>
        private void FillDay(DateOnly date, Random random, HashSet<(int Court, int Hour)> taken, bool isToday)
        {
            var ageing = 1.0 - (today.DayNumber - date.DayNumber) * 0.008;
            for (var hour = OpensHour; hour < ClosesHour; hour++)
            {
                var share = hour is >= 14 and <= 22
                    ? branch.Evening[hour - 14]
                    : hour < 10 ? 0.2 : hour < 12 ? 0.3 : 0.35;
                if (!isToday)
                {
                    share = Math.Min(1, share * ageing * (0.9 + random.NextDouble() * 0.2));
                }

                var wanted = (int)Math.Round(share * courts.Count);
                var busy = Enumerable.Range(1, courts.Count).Count(court => taken.Contains((court, hour)));
                var free = Enumerable.Range(1, courts.Count)
                    .Where(court => !taken.Contains((court, hour)))
                    .OrderBy(_ => random.Next())
                    .ToList();

                foreach (var court in free.Take(Math.Max(0, wanted - busy)))
                {
                    var until = hour + 1 < ClosesHour && !taken.Contains((court, hour + 1)) && random.NextDouble() < 0.5
                        ? hour + 2
                        : hour + 1;
                    var roll = random.NextDouble();
                    var kind = roll < 0.35 ? Kind.App : roll < 0.8 ? Kind.Walk : Kind.Member;
                    var name = kind switch
                    {
                        Kind.App => AppPeople[random.Next(AppPeople.Length)],
                        Kind.Walk => WalkInNames[random.Next(WalkInNames.Length)],
                        _ => "",
                    };
                    Write(new Planned(court, hour, until, name, kind, false, false), date, random);
                    for (var one = hour; one < until; one++)
                    {
                        taken.Add((court, one));
                    }
                }
            }
        }

        private async Task WriteAsync(Planned one, DateOnly date, Random random, CancellationToken cancellationToken)
        {
            if (one.Kind == Kind.Group)
            {
                await GroupBookingAsync(one, date, random, cancellationToken);
            }
            else
            {
                Write(one, date, random);
            }
        }

        /// <summary>One booking of any kind but a group's, written with its money and its arrival.</summary>
        private void Write(Planned one, DateOnly date, Random random)
        {
            var hours = Enumerable.Range(one.From, one.Until - one.From).ToArray();
            var slots = Priced(one.Court, date, hours);
            var starts = PlatformRequirements.BangkokHour(date, one.From);
            var made = Earlier(starts.AddMinutes(-random.Next(30, 60 * 24)), now);
            Booking booking;

            switch (one.Kind)
            {
                case Kind.App:
                {
                    var bookerId = bookers.GetValueOrDefault(one.Name, bookers.Values.First());
                    booking = Booking.Hold(
                        venue.Id, bookerId, policyId, slots, 100, DepositReason.VenueTerms, made);
                    var paidAt = made.AddMinutes(4);
                    booking.StatusChanges.Add(BookingTransitions.Record(
                        booking.Id, BookingStatus.Held, BookingStatus.PendingVerification, bookerId, paidAt));
                    booking.StatusChanges.Add(BookingTransitions.Record(
                        booking.Id, BookingStatus.PendingVerification, BookingStatus.Confirmed, ownerId, paidAt.AddMinutes(6)));
                    booking.Status = BookingStatus.Confirmed;
                    if (one.Unpaid)
                    {
                        // Pays at the venue (the design's "จ่ายที่สนาม"): nothing in yet.
                        booking.PaymentState = PaymentState.NotReceived;
                    }
                    else
                    {
                        booking.PaymentState = PaymentState.Received;
                        Receipt(booking, PaymentMethod.PromptPay, DayOf(paidAt), paidAt, null);
                    }

                    break;
                }

                case Kind.Member:
                {
                    // A member the design names plays on their own package; anybody else is one
                    // of the members, but only within the few hours the fortnight may use, so each
                    // ends up with the hours used the design's members table shows.
                    var package = packages.FirstOrDefault(p => p.Package.CustomerName == one.Name && p.Left >= hours.Length)
                        ?? packages
                            .Where(p => p.Spare >= hours.Length && p.Left >= hours.Length)
                            .OrderBy(_ => random.Next())
                            .FirstOrDefault();
                    if (package is not null && package.Package.CustomerName != one.Name)
                    {
                        package.Spare -= hours.Length;
                    }
                    if (package is null)
                    {
                        Write(one with { Kind = Kind.Walk, Name = WalkInNames[random.Next(WalkInNames.Length)] }, date, random);
                        return;
                    }

                    booking = Booking.OnHours(
                        venue.Id, package.Package.CustomerName, package.Package.CustomerPhone,
                        package.Package, hours.Length, policyId, slots, ownerId, made);
                    package.Left -= hours.Length;
                    database.PackageEntries.Add(new PackageEntry
                    {
                        PackageId = package.Package.Id,
                        Hours = -hours.Length,
                        Move = PackageMove.Used,
                        BookingId = booking.Id,
                        At = made,
                        ByUserId = ownerId,
                    });
                    break;
                }

                default:
                {
                    var paid = random.NextDouble() < 0.6 ? CounterPayment.Transfer : CounterPayment.Cash;
                    booking = Booking.AtCounter(
                        venue.Id, one.Name, $"08{random.Next(10_000_000, 99_999_999)}", paid, policyId, slots, ownerId, made);
                    if (one.Unpaid)
                    {
                        booking.PaymentState = PaymentState.NotReceived;
                    }
                    else
                    {
                        Receipt(
                            booking,
                            paid == CounterPayment.Cash ? PaymentMethod.Cash : PaymentMethod.PromptPay,
                            DayOf(made), made, ownerId);
                    }

                    break;
                }
            }

            Arrive(booking, starts, one.Waits, random);
            database.Bookings.Add(booking);
            MaybeSell(booking, starts, random);
        }

        private async Task GroupBookingAsync(Planned one, DateOnly date, Random random, CancellationToken cancellationToken)
        {
            var court = courts[one.Court - 1];
            if (!groups.TryGetValue(one.Name, out var series))
            {
                series = await database.BookingSeries.FirstOrDefaultAsync(
                    s => s.VenueId == venue.Id && s.CustomerName == one.Name, cancellationToken);
                if (series is null)
                {
                    series = new BookingSeries
                    {
                        VenueId = venue.Id,
                        CourtId = court.Id,
                        Day = date.DayOfWeek,
                        FromHour = one.From,
                        UntilHour = one.Until,
                        CustomerName = one.Name,
                        CustomerPhone = $"0812{random.Next(100_000, 999_999)}",
                        StartsOn = date.AddDays(-28),
                        CreatedAt = now.AddDays(-28),
                        CreatedByUserId = ownerId,
                    };
                    database.BookingSeries.Add(series);
                }

                groups[one.Name] = series;
            }

            var hours = Enumerable.Range(one.From, one.Until - one.From).ToArray();
            var starts = PlatformRequirements.BangkokHour(date, one.From);
            var booking = Booking.ForSeries(series, policyId, Priced(one.Court, date, hours), Earlier(starts.AddDays(-7), now));
            if (starts <= now)
            {
                // Paid at the desk when they arrived.
                booking.PaymentState = PaymentState.Received;
                Receipt(booking, PaymentMethod.Cash, date, starts, ownerId);
            }

            Arrive(booking, starts, one.Waits, random);
            database.Bookings.Add(booking);
            MaybeSell(booking, starts, random);
        }

        /// <summary>Whoever's game has begun has been taken in — unless the design has them still due.</summary>
        private void Arrive(Booking booking, DateTimeOffset starts, bool waits, Random random)
        {
            if (starts > now || (waits && starts > now.AddHours(-3)))
            {
                return;
            }

            var at = starts.AddMinutes(-random.Next(0, 15));
            database.BookingArrivalChanges.Add(new BookingArrivalChange
            {
                BookingId = booking.Id,
                From = booking.Arrival,
                To = BookingArrival.Arrived,
                ChangedAt = at,
                ChangedByUserId = ownerId,
            });
            booking.Arrival = BookingArrival.Arrived;
            booking.ArrivedAt = at;
        }

        private void Receipt(Booking booking, PaymentMethod method, DateOnly day, DateTimeOffset at, Guid? by) =>
            database.PaymentReceipts.Add(new PaymentReceipt
            {
                BookingId = booking.Id,
                VenueId = venue.Id,
                CountsOn = day,
                AmountBaht = booking.TotalBaht,
                Method = method,
                ReceivedAt = at,
                ReceivedByUserId = by,
            });

        /// <summary>Shuttles and drinks onto about a third of the games that have begun.</summary>
        private void MaybeSell(Booking booking, DateTimeOffset starts, Random random)
        {
            if (starts > now || random.NextDouble() > 0.35)
            {
                return;
            }

            var sale = new ShopSale
            {
                VenueId = venue.Id,
                BookingId = booking.Id,
                SoldAt = Earlier(starts.AddMinutes(random.Next(0, 40)), now),
                SoldByUserId = ownerId,
            };
            // Tubes sell best, as the design's top items have it.
            var lines = new List<ShopSaleLine>();
            if (random.NextDouble() < 0.35)
            {
                lines.Add(Line(sale, goods[0], 1));
            }

            lines.Add(Line(sale, goods[random.Next(1, goods.Length)], random.Next(1, 5)));
            sale.Selling(lines);
            database.ShopSales.Add(sale);

            foreach (var line in lines)
            {
                database.StockEntries.Add(new StockEntry
                {
                    ItemId = line.ItemId,
                    Quantity = -line.Quantity,
                    Move = StockMove.Sold,
                    SaleId = sale.Id,
                    At = sale.SoldAt,
                    ByUserId = ownerId,
                });
            }

            database.PaymentReceipts.Add(new PaymentReceipt
            {
                SaleId = sale.Id,
                VenueId = venue.Id,
                CountsOn = DayOf(sale.SoldAt),
                AmountBaht = sale.TotalBaht,
                Method = random.NextDouble() < 0.6 ? PaymentMethod.PromptPay : PaymentMethod.Cash,
                ReceivedAt = sale.SoldAt,
                ReceivedByUserId = ownerId,
            });
        }

        private static ShopSaleLine Line(ShopSale sale, ShopItem item, int quantity) => new()
        {
            SaleId = sale.Id,
            ItemId = item.Id,
            Name = item.Name,
            Quantity = quantity,
            EachBaht = item.PriceBaht,
        };

        private SlotPrice[] Priced(int court, DateOnly date, int[] hours) =>
        [
            .. hours.Select(hour => new SlotPrice(
                courts[court - 1].Id,
                PlatformRequirements.BangkokHour(date, hour),
                PriceAt(date.DayOfWeek, hour))),
        ];

        private async Task<ShopItem[]> EnsureShopAsync(CancellationToken cancellationToken)
        {
            var items = await database.ShopItems
                .Where(item => item.VenueId == venue.Id && item.WithdrawnAt == null)
                .OrderBy(item => item.CreatedAt)
                .ToArrayAsync(cancellationToken);
            if (items.Length > 0)
            {
                return items;
            }

            var boughtAt = PlatformRequirements.BangkokHour(today.AddDays(-DaysBack - 1), 10);
            items =
            [
                .. Goods.Select((good, index) => new ShopItem
                {
                    VenueId = venue.Id,
                    Name = good.Name,
                    PriceBaht = good.Baht,
                    Unit = good.Unit,
                    Counted = true,
                    TellMeAt = 10,
                    CreatedAt = now.AddSeconds(index),
                    CreatedByUserId = ownerId,
                }),
            ];
            database.ShopItems.AddRange(items);

            // Stock bought in before the fortnight began: one bill each, one line on the shelf.
            foreach (var item in items)
            {
                var spend = new Spend
                {
                    VenueId = venue.Id,
                    Kind = SpendKind.Stock,
                    AmountBaht = item.PriceBaht * 0.55m * 500,
                    PaidOn = DayOf(boughtAt),
                    PaidBy = PaymentMethod.PromptPay,
                    Note = item.Name,
                    RecordedAt = boughtAt,
                    RecordedByUserId = ownerId,
                };
                database.Spends.Add(spend);
                database.StockEntries.Add(new StockEntry
                {
                    ItemId = item.Id, Quantity = 500, Move = StockMove.BoughtIn, SpendId = spend.Id,
                    At = boughtAt, ByUserId = ownerId,
                });
            }

            await database.SaveChangesAsync(cancellationToken);
            return items;
        }

        /// <summary>The design's members, each with the hours they have used already.</summary>
        private async Task<List<Balance>> EnsurePackagesAsync(CancellationToken cancellationToken)
        {
            if (!await database.PackageTypes.AnyAsync(type => type.VenueId == venue.Id, cancellationToken))
            {
                var types = Plans.Select(plan => new PackageType
                {
                    VenueId = venue.Id, Name = plan.Name, Hours = plan.Hours, PriceBaht = plan.Baht,
                    ValidForDays = plan.Days, CreatedAt = now, CreatedByUserId = ownerId,
                }).ToArray();
                database.PackageTypes.AddRange(types);

                foreach (var member in Members)
                {
                    var type = types[member.Plan];
                    var expires = today.AddDays(member.Days);
                    var soldOn = expires.AddDays(-type.ValidForDays);
                    var soldAt = PlatformRequirements.BangkokHour(soldOn, 19);
                    var package = new HourPackage
                    {
                        VenueId = venue.Id,
                        PackageTypeId = type.Id,
                        CustomerName = member.Name,
                        CustomerPhone = member.Phone,
                        HoursSold = member.Hours,
                        PriceBaht = type.PriceBaht * member.Hours / type.Hours,
                        ExpiresOn = expires,
                        SoldAt = soldAt,
                        SoldByUserId = ownerId,
                    };
                    database.HourPackages.Add(package);
                    database.PackageEntries.Add(new PackageEntry
                    {
                        PackageId = package.Id, Hours = member.Hours, Move = PackageMove.Sold, At = soldAt,
                        ByUserId = ownerId,
                    });
                    // What they had used before the fortnight this data draws: all of the design's
                    // number but the hours tonight's evening at อารีย์ and the fortnight will use.
                    var tonight = branch.Code == "ARI01"
                        ? AriToday.Where(one => one.Name == member.Name).Sum(one => one.Until - one.From)
                        : 0;
                    var usedBefore = Math.Max(0, member.Used - tonight - SpareHours);
                    if (usedBefore > 0)
                    {
                        database.PackageEntries.Add(new PackageEntry
                        {
                            PackageId = package.Id, Hours = -usedBefore, Move = PackageMove.Used,
                            At = soldAt.AddDays(3), ByUserId = ownerId,
                        });
                    }

                    database.PaymentReceipts.Add(new PaymentReceipt
                    {
                        PackageId = package.Id,
                        VenueId = venue.Id,
                        CountsOn = soldOn,
                        AmountBaht = package.PriceBaht,
                        Method = PaymentMethod.PromptPay,
                        ReceivedAt = soldAt,
                        ReceivedByUserId = ownerId,
                    });
                }

                await database.SaveChangesAsync(cancellationToken);
            }

            var held = await database.HourPackages
                .Where(package => package.VenueId == venue.Id && package.ExpiredAt == null)
                .Select(package => new { Package = package, Left = package.Entries.Sum(entry => entry.Hours) })
                .ToListAsync(cancellationToken);
            return [.. held.Select(one => new Balance(one.Package, one.Left, SpareHours))];
        }
    }

    /// <summary>How many hours of each member's package the fortnight's other games may use.</summary>
    private const int SpareHours = 2;

    private sealed class Balance(HourPackage package, int left, int spare)
    {
        public HourPackage Package { get; } = package;

        public int Left { get; set; } = left;

        /// <summary>What anybody but the member the design names may still take from it.</summary>
        public int Spare { get; set; } = spare;
    }

    private static DateOnly DayOf(DateTimeOffset instant) =>
        DateOnly.FromDateTime(instant.AddHours(7).UtcDateTime);

    private static DateTimeOffset Earlier(DateTimeOffset one, DateTimeOffset other) => one < other ? one : other;
}
