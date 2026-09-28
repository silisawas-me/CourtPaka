using CourtBooking.Api.Bookings;
using CourtBooking.Api.Identity;
using CourtBooking.Api.Localization;
using CourtBooking.Api.Venues;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace CourtBooking.Api.Data;

/// <summary>
/// Four made-up branches with a fortnight of trade behind them, so the owner app has something to
/// look like on a local stack (docs/plan/owner-overview.md): the branches of the design, evenings
/// fuller than afternoons, walk-ins, standing groups, members on hour packages, and a shop.
/// </summary>
/// <remarks>
/// Only ever runs when App:SeedMockData is on AND the host is Development — the same two locks as
/// <see cref="DevelopmentSeeder"/>. It writes the rows straight to the context, the way the seeder
/// does, because the doors refuse what it needs: an hour that is already over cannot be sold at
/// the counter, and a fortnight of history is nothing but hours that are over.
///
/// Every day is filled once. A day at a branch that has any booking already is left alone, so a
/// restart tops up the days that have come since and never doubles one. The seeded venue (DEV01)
/// is not touched: the verify scripts own it.
/// </remarks>
public static class DevelopmentMockData
{
    /// <summary>The account that owns the four branches (password as the seed's).</summary>
    public const string DemoEmail = "demo@courtpaka.local";

    private const int DaysBack = 13;
    private const int OpensHour = 8;
    private const int ClosesHour = 24;
    private const int PeakFromHour = 17;

    private sealed record Branch(string Code, string Name, string District, int Courts, double Busy);

    private static readonly Branch[] Branches =
    [
        new("ARI01", "อารีย์", "พญาไท", 6, 0.95),
        new("TLO01", "ทองหล่อ", "วัฒนา", 6, 1.0),
        new("BNA01", "บางนา", "บางนา", 12, 0.7),
        new("RM901", "พระราม 9", "ห้วยขวาง", 10, 0.9),
    ];

    private static readonly string[] Names =
    [
        "คุณต้น", "คุณแพร", "คุณบอส", "คุณมิ้นท์", "คุณเจ", "คุณฝน", "คุณโอ๊ต", "คุณแนน", "คุณกอล์ฟ",
        "คุณเบียร์", "คุณนุ่น", "คุณปอนด์", "คุณจูน", "คุณเก่ง", "คุณพลอย", "คุณบีม", "คุณแบงค์", "คุณฟ้า",
        "คุณเอ็ม", "คุณหญิง", "คุณตาล", "คุณนิว", "คุณภูมิ", "คุณข้าว",
    ];

    private static readonly (string Name, DayOfWeek Day, int From, int Until)[] Groups =
    [
        ("ก๊วนวันพุธ", DayOfWeek.Wednesday, 19, 21),
        ("ก๊วนออฟฟิศ", DayOfWeek.Monday, 18, 20),
        ("ก๊วนมือใหม่", DayOfWeek.Saturday, 10, 12),
        ("ก๊วนเช้าวันอาทิตย์", DayOfWeek.Sunday, 8, 10),
    ];

    private static readonly (string Name, decimal Baht, string Unit, bool Counted)[] Goods =
    [
        ("น้ำดื่ม", 15m, "ขวด", true),
        ("เกลือแร่", 25m, "ขวด", true),
        ("ลูกแบด", 80m, "ลูก", true),
        ("กริปพันด้าม", 60m, "ม้วน", true),
        ("เช่าไม้แบด", 50m, "ครั้ง", false),
    ];

    public static async Task SeedAsync(IServiceProvider services, CancellationToken cancellationToken = default)
    {
        using var scope = services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        var time = scope.ServiceProvider.GetRequiredService<TimeProvider>();
        var options = scope.ServiceProvider.GetRequiredService<IOptions<AppOptions>>();

        // An account of its own, so the branches are all it owns and the owner app looks the way
        // the design draws it — owner@ also owns the venues the verify scripts make and leave.
        var owner = await DevelopmentSeeder.EnsureUserAsync(users, DemoEmail);
        var now = time.GetUtcNow();
        var today = PlatformRequirements.BangkokToday(time);

        foreach (var branch in Branches)
        {
            var venue = await EnsureVenueAsync(database, branch, owner.Id, options.Value, today, now, cancellationToken);
            await FillAsync(database, venue, branch, owner.Id, today, now, cancellationToken);
        }
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
            await OwnedByDemoAsync(database, venue.Id, ownerId, now, cancellationToken);
            return venue;
        }

        var opened = today.AddDays(-DaysBack - 30);
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

        database.OpeningHoursSchedules.Add(OpeningHoursSchedule.Create(
            venue.Id,
            opened,
            Enum.GetValues<DayOfWeek>().Select(day => new WeekdayHours(day, OpensHour, ClosesHour)),
            ownerId,
            now));
        database.PriceLists.Add(PriceList.Create(
            venue.Id,
            Enum.GetValues<DayOfWeek>().SelectMany(day => new[]
            {
                new BandHours(day, OpensHour, PeakFromHour, 220m),
                new BandHours(day, PeakFromHour, ClosesHour, 320m),
            }),
            ownerId,
            now));
        database.CancellationPolicies.Add(CancellationPolicy.Create(
            venue.Id, CancellationPolicy.Default, ownerId, now));

        await database.SaveChangesAsync(cancellationToken);
        return venue;
    }

    /// <summary>
    /// Branches written before there was a demo account belonged to owner@: they move to the demo
    /// account, so owner@ is left with the venues the scripts drive.
    /// </summary>
    private static async Task OwnedByDemoAsync(
        AppDbContext database,
        Guid venueId,
        Guid demoId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var seats = await database.VenueMemberships
            .Where(member => member.VenueId == venueId)
            .ToListAsync(cancellationToken);
        if (seats.Any(member => member.UserId == demoId))
        {
            return;
        }

        database.VenueMemberships.RemoveRange(seats);
        database.VenueMemberships.Add(new VenueMembership
        {
            VenueId = venueId,
            UserId = demoId,
            Role = VenueRole.Owner,
            Permissions = VenuePermissions.None,
            CreatedAt = now,
        });
        await database.SaveChangesAsync(cancellationToken);
    }

    private static async Task FillAsync(
        AppDbContext database,
        Venue venue,
        Branch branch,
        Guid ownerId,
        DateOnly today,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var courts = await database.Courts
            .Where(court => court.VenueId == venue.Id)
            .OrderBy(court => court.Position)
            .ToListAsync(cancellationToken);
        var policyId = await database.CancellationPolicies
            .Where(policy => policy.VenueId == venue.Id)
            .OrderByDescending(policy => policy.CreatedAt)
            .Select(policy => policy.Id)
            .FirstAsync(cancellationToken);

        var first = today.AddDays(-DaysBack);
        var since = PlatformRequirements.BangkokHour(first, 0);
        var written = await database.BookingSlots
            .Where(slot => slot.Court!.VenueId == venue.Id && slot.StartsAt >= since)
            .Select(slot => new { slot.StartsAt, slot.Booking!.SeriesId })
            .ToListAsync(cancellationToken);

        // A day is filled when it has a walk-in. A group's week is its own question, because the
        // caretaker writes those ahead of time (US-30) and a day it wrote one for is not filled.
        var filled = written
            .Where(one => one.SeriesId is null)
            .Select(one => DayOf(one.StartsAt))
            .ToHashSet();
        var weeks = written
            .Where(one => one.SeriesId is not null)
            .Select(one => (one.SeriesId!.Value, DayOf(one.StartsAt)))
            .ToHashSet();

        var series = await EnsureGroupsAsync(database, venue, courts, ownerId, first, now, cancellationToken);
        var packages = await EnsurePackagesAsync(database, venue, ownerId, first, now, cancellationToken);
        var goods = await EnsureShopAsync(database, venue, ownerId, first, now, cancellationToken);

        for (var date = first; date <= today; date = date.AddDays(1))
        {
            var random = new Random(HashCode.Combine(branch.Code, date.DayNumber));
            var taken = new HashSet<(Guid, int)>();

            if (date == today && branch.Code == "BNA01" && !filled.Contains(date))
            {
                // One court at one branch shut until five, so the overview has something to warn
                // about, and nothing booked on it meanwhile, which the closure door would refuse
                // (US-11).
                var shut = courts[1];
                database.CourtClosures.Add(new CourtClosure
                {
                    CourtId = shut.Id,
                    StartsAt = PlatformRequirements.BangkokHour(today, 0),
                    EndsAt = PlatformRequirements.BangkokHour(today, 17),
                    Reason = "ซ่อมพื้น",
                    CreatedByUserId = ownerId,
                    CreatedAt = now,
                });
                for (var hour = OpensHour; hour < 17; hour++)
                {
                    taken.Add((shut.Id, hour));
                }
            }

            foreach (var group in series.Where(one => one.Day == date.DayOfWeek))
            {
                var hours = Enumerable.Range(group.FromHour, group.Hours).ToArray();
                if (weeks.Contains((group.Id, date)) || hours.Any(hour => taken.Contains((group.CourtId, hour))))
                {
                    continue;
                }

                var starts = PlatformRequirements.BangkokHour(date, group.FromHour);
                var booking = Booking.ForSeries(
                    group, policyId, Priced(group.CourtId, date, hours), Earlier(starts.AddDays(-7), now));
                if (PlatformRequirements.BangkokHour(date, group.UntilHour) <= now)
                {
                    booking.PaymentState = PaymentState.Received;
                    Receipt(database, venue.Id, booking, PaymentMethod.Cash, date, starts, ownerId);
                }

                Arrive(database, booking, now, random, ownerId);
                database.Bookings.Add(booking);
                foreach (var hour in hours)
                {
                    taken.Add((group.CourtId, hour));
                }
            }

            if (filled.Contains(date))
            {
                await database.SaveChangesAsync(cancellationToken);
                continue;
            }

            for (var hour = OpensHour; hour < ClosesHour; hour++)
            {
                foreach (var court in courts)
                {
                    if (taken.Contains((court.Id, hour)) || random.NextDouble() > Busy(hour, date, branch.Busy))
                    {
                        continue;
                    }

                    var length = hour + 1 < ClosesHour && !taken.Contains((court.Id, hour + 1))
                        && random.NextDouble() < 0.45 ? 2 : 1;
                    var hours = Enumerable.Range(hour, length).ToArray();
                    var name = Names[random.Next(Names.Length)];
                    var phone = $"08{random.Next(10_000_000, 99_999_999)}";
                    var made = Earlier(PlatformRequirements.BangkokHour(date, hour).AddMinutes(-random.Next(20, 600)), now);
                    var slots = Priced(court.Id, date, hours);

                    var package = packages.FirstOrDefault(one => one.Left >= length);
                    Booking booking;
                    if (package is not null && random.NextDouble() < 0.18)
                    {
                        booking = Booking.OnHours(
                            venue.Id, package.Package.CustomerName, package.Package.CustomerPhone,
                            package.Package, length, policyId, slots, ownerId, made);
                        package.Left -= length;
                        database.PackageEntries.Add(new PackageEntry
                        {
                            PackageId = package.Package.Id,
                            Hours = -length,
                            Move = PackageMove.Used,
                            BookingId = booking.Id,
                            At = made,
                            ByUserId = ownerId,
                        });
                    }
                    else
                    {
                        var paid = random.NextDouble() < 0.55 ? CounterPayment.Transfer : CounterPayment.Cash;
                        booking = Booking.AtCounter(
                            venue.Id, name, phone, paid, policyId, slots, ownerId, made);
                        Receipt(
                            database, venue.Id, booking,
                            paid == CounterPayment.Cash ? PaymentMethod.Cash : PaymentMethod.PromptPay,
                            date, made, ownerId);
                    }

                    Arrive(database, booking, now, random, ownerId);
                    database.Bookings.Add(booking);
                    foreach (var one in hours)
                    {
                        taken.Add((court.Id, one));
                    }

                    if (random.NextDouble() < 0.35 && booking.Slots.Min(slot => slot.StartsAt) < now)
                    {
                        Sell(database, venue.Id, booking, goods, random, ownerId, now);
                    }
                }
            }

            await database.SaveChangesAsync(cancellationToken);
        }

        SlotPrice[] Priced(Guid courtId, DateOnly date, int[] hours) =>
        [
            .. hours.Select(hour => new SlotPrice(
                courtId,
                PlatformRequirements.BangkokHour(date, hour),
                hour >= PeakFromHour ? 320m : 220m)),
        ];
    }

    /// <summary>How likely a court is taken at an hour: quiet mornings, a lunch bump, full evenings.</summary>
    private static double Busy(int hour, DateOnly date, double branch)
    {
        var shape = hour switch
        {
            < 10 => 0.2,
            < 12 => 0.3,
            < 14 => 0.25,
            < 17 => 0.4,
            < 18 => 0.65,
            < 22 => 0.9,
            _ => 0.45,
        };
        var weekend = date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday ? 1.2 : 1.0;
        return Math.Min(0.97, shape * weekend * branch);
    }

    /// <summary>
    /// Whoever's game has begun has been checked in — except, on the hour playing now, a couple
    /// who have not turned up yet, so the desk has somebody to wait for.
    /// </summary>
    private static void Arrive(AppDbContext database, Booking booking, DateTimeOffset now, Random random, Guid ownerId)
    {
        var starts = booking.Slots.Min(slot => slot.StartsAt);
        if (starts > now || (starts > now.AddMinutes(-60) && random.NextDouble() < 0.3))
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

    private static DateOnly DayOf(DateTimeOffset instant) =>
        DateOnly.FromDateTime(instant.AddHours(7).UtcDateTime);

    private static DateTimeOffset Earlier(DateTimeOffset one, DateTimeOffset other) => one < other ? one : other;

    private static void Receipt(
        AppDbContext database,
        Guid venueId,
        Booking booking,
        PaymentMethod method,
        DateOnly date,
        DateTimeOffset at,
        Guid ownerId) =>
        database.PaymentReceipts.Add(new PaymentReceipt
        {
            BookingId = booking.Id,
            VenueId = venueId,
            CountsOn = date,
            AmountBaht = booking.TotalBaht,
            Method = method,
            ReceivedAt = at,
            ReceivedByUserId = ownerId,
        });

    private static void Sell(
        AppDbContext database, Guid venueId, Booking booking, ShopItem[] goods, Random random, Guid ownerId, DateTimeOffset now)
    {
        var sale = new ShopSale
        {
            VenueId = venueId,
            BookingId = booking.Id,
            SoldAt = Earlier(booking.Slots.Min(slot => slot.StartsAt).AddMinutes(random.Next(0, 50)), now),
            SoldByUserId = ownerId,
        };
        var lines = goods
            .OrderBy(_ => random.Next())
            .Take(random.Next(1, 3))
            .Select(item => new ShopSaleLine
            {
                SaleId = sale.Id,
                ItemId = item.Id,
                Name = item.Name,
                Quantity = random.Next(1, 4),
                EachBaht = item.PriceBaht,
            })
            .ToList();
        sale.Selling(lines);
        database.ShopSales.Add(sale);

        foreach (var line in lines.Where(line => goods.First(item => item.Id == line.ItemId).Counted))
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
            VenueId = venueId,
            CountsOn = DayOf(sale.SoldAt),
            AmountBaht = sale.TotalBaht,
            Method = random.NextDouble() < 0.6 ? PaymentMethod.Cash : PaymentMethod.PromptPay,
            ReceivedAt = sale.SoldAt,
            ReceivedByUserId = ownerId,
        });
    }

    private static async Task<List<BookingSeries>> EnsureGroupsAsync(
        AppDbContext database,
        Venue venue,
        List<Court> courts,
        Guid ownerId,
        DateOnly first,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var existing = await database.BookingSeries
            .Where(one => one.VenueId == venue.Id)
            .ToListAsync(cancellationToken);
        if (existing.Count > 0)
        {
            return existing;
        }

        var made = Groups.Select((group, index) => new BookingSeries
        {
            VenueId = venue.Id,
            CourtId = courts[courts.Count - 1 - index % courts.Count].Id,
            Day = group.Day,
            FromHour = group.From,
            UntilHour = group.Until,
            CustomerName = group.Name,
            CustomerPhone = $"081{index}00{index}000",
            StartsOn = first,
            CreatedAt = now,
            CreatedByUserId = ownerId,
        }).ToList();

        database.BookingSeries.AddRange(made);
        await database.SaveChangesAsync(cancellationToken);
        return made;
    }

    private sealed class Balance(HourPackage package, int left)
    {
        public HourPackage Package { get; } = package;

        public int Left { get; set; } = left;
    }

    private static async Task<List<Balance>> EnsurePackagesAsync(
        AppDbContext database,
        Venue venue,
        Guid ownerId,
        DateOnly first,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (!await database.PackageTypes.AnyAsync(type => type.VenueId == venue.Id, cancellationToken))
        {
            var ten = new PackageType
            {
                VenueId = venue.Id, Name = "10 ชั่วโมง", Hours = 10, PriceBaht = 2_000m, ValidForDays = 60,
                CreatedAt = now, CreatedByUserId = ownerId,
            };
            var twenty = new PackageType
            {
                VenueId = venue.Id, Name = "20 ชั่วโมง", Hours = 20, PriceBaht = 3_800m, ValidForDays = 90,
                CreatedAt = now, CreatedByUserId = ownerId,
            };
            database.PackageTypes.AddRange(ten, twenty);

            string[] members = ["คุณวิน", "คุณมายด์", "คุณโจ้", "คุณเฟิร์น", "คุณปาล์ม", "คุณอ้อม"];
            for (var index = 0; index < members.Length; index++)
            {
                var type = index % 2 == 0 ? ten : twenty;
                // Sold across the last two months, so some are nearly used up or nearly out of date.
                var soldOn = first.AddDays(-7 * index);
                var soldAt = PlatformRequirements.BangkokHour(soldOn, 18);
                var package = new HourPackage
                {
                    VenueId = venue.Id,
                    PackageTypeId = type.Id,
                    CustomerName = members[index],
                    CustomerPhone = $"0891{index}2345{index}",
                    HoursSold = type.Hours,
                    PriceBaht = type.PriceBaht,
                    ExpiresOn = soldOn.AddDays(type.ValidForDays),
                    SoldAt = soldAt,
                    SoldByUserId = ownerId,
                };
                database.HourPackages.Add(package);
                database.PackageEntries.Add(new PackageEntry
                {
                    PackageId = package.Id, Hours = type.Hours, Move = PackageMove.Sold, At = soldAt,
                    ByUserId = ownerId,
                });
                database.PaymentReceipts.Add(new PaymentReceipt
                {
                    PackageId = package.Id,
                    VenueId = venue.Id,
                    CountsOn = soldOn,
                    AmountBaht = type.PriceBaht,
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
        return [.. held.Select(one => new Balance(one.Package, one.Left))];
    }

    private static async Task<ShopItem[]> EnsureShopAsync(
        AppDbContext database,
        Venue venue,
        Guid ownerId,
        DateOnly first,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var items = await database.ShopItems
            .Where(item => item.VenueId == venue.Id && item.WithdrawnAt == null)
            .ToArrayAsync(cancellationToken);
        if (items.Length > 0)
        {
            return items;
        }

        items =
        [
            .. Goods.Select(good => new ShopItem
            {
                VenueId = venue.Id,
                Name = good.Name,
                PriceBaht = good.Baht,
                Unit = good.Unit,
                Counted = good.Counted,
                CreatedAt = now,
                CreatedByUserId = ownerId,
            }),
        ];
        database.ShopItems.AddRange(items);

        // Stock bought in before the fortnight began: one bill, one line on the shelf per thing.
        var boughtAt = PlatformRequirements.BangkokHour(first.AddDays(-1), 10);
        foreach (var item in items.Where(item => item.Counted))
        {
            var spend = new Spend
            {
                VenueId = venue.Id,
                Kind = SpendKind.Stock,
                AmountBaht = item.PriceBaht * 0.5m * 600,
                PaidOn = first.AddDays(-1),
                PaidBy = PaymentMethod.PromptPay,
                Note = item.Name,
                RecordedAt = boughtAt,
                RecordedByUserId = ownerId,
            };
            database.Spends.Add(spend);
            database.StockEntries.Add(new StockEntry
            {
                ItemId = item.Id, Quantity = 600, Move = StockMove.BoughtIn, SpendId = spend.Id, At = boughtAt,
                ByUserId = ownerId,
            });
        }

        await database.SaveChangesAsync(cancellationToken);
        return items;
    }
}
