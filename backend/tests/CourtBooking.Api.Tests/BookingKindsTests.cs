using CourtBooking.Api.Bookings;

namespace CourtBooking.Api.Tests;

/// <summary>The four kinds the floor colours a block by, read from what the booking records.</summary>
public sealed class BookingKindsTests
{
    [Fact]
    public void A_booking_made_in_the_app_is_an_app_booking() =>
        Assert.Equal(BookingKinds.App, BookingKinds.Of(Made(BookingChannel.Online)));

    [Fact]
    public void A_booking_sold_at_the_counter_is_a_walk_in() =>
        Assert.Equal(BookingKinds.WalkIn, BookingKinds.Of(Made(BookingChannel.Staff)));

    [Fact]
    public void Hours_paid_from_a_package_read_as_the_package() =>
        Assert.Equal(BookingKinds.Package, BookingKinds.Of(Made(BookingChannel.Staff, package: Guid.NewGuid())));

    /// <summary>The standing arrangement is what the counter sees, however the week was paid.</summary>
    [Fact]
    public void A_groups_week_stays_the_groups_even_when_a_package_paid_for_it() =>
        Assert.Equal(
            BookingKinds.Series,
            BookingKinds.Of(Made(BookingChannel.Staff, series: Guid.NewGuid(), package: Guid.NewGuid())));

    private static Booking Made(BookingChannel channel, Guid? series = null, Guid? package = null) =>
        new()
        {
            VenueId = Guid.NewGuid(),
            Channel = channel,
            CreatedAt = DateTimeOffset.UtcNow,
            HoldExpiresAt = DateTimeOffset.UtcNow,
            TotalBaht = 200m,
            DepositBaht = 200m,
            DepositReason = DepositReason.VenueTerms,
            CancellationPolicyId = Guid.NewGuid(),
            SeriesId = series,
            PackageId = package,
        };
}
