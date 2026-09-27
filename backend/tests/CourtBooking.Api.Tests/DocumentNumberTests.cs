using CourtBooking.Api.Data;
using CourtBooking.Api.Documents;
using CourtBooking.Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CourtBooking.Api.Tests;

/// <summary>
/// Document numbers, which PRD 7.4 asks two things of: that they never repeat, and that they
/// never skip. The second is the hard one, and it is the one US-16's acceptance criterion names
/// — "เลขไม่ซ้ำและไม่ข้ามแม้ออกพร้อมกันหลายฉบับ".
/// </summary>
public sealed class DocumentNumberTests(ApiTestFixture api) : IClassFixture<ApiTestFixture>
{
    [Fact]
    public void A_number_is_written_the_way_PRD_7_4_writes_it()
    {
        Assert.Equal(
            "SBC-ABB-2027-000123",
            DocumentNumbers.Format("SBC", DocumentKind.Abb, 2027, 123));
    }

    [Fact]
    public async Task The_first_of_a_series_is_one_and_the_next_is_two()
    {
        var series = Series();

        Assert.Equal($"{series}-REC-2027-000001", await TakeAsync(series, DocumentKind.Rec, 2027));
        Assert.Equal($"{series}-REC-2027-000002", await TakeAsync(series, DocumentKind.Rec, 2027));
    }

    /// <summary>
    /// Each series counts for itself: a kind, a code and a year are three different counts, and a
    /// new year starts again at one (PRD 7.4).
    /// </summary>
    [Fact]
    public async Task Every_series_counts_on_its_own()
    {
        var series = Series();
        var other = Series();

        await TakeAsync(series, DocumentKind.Abb, 2027);

        Assert.Equal($"{series}-TAX-2027-000001", await TakeAsync(series, DocumentKind.Tax, 2027));
        Assert.Equal($"{series}-ABB-2028-000001", await TakeAsync(series, DocumentKind.Abb, 2028));
        Assert.Equal($"{other}-ABB-2027-000001", await TakeAsync(other, DocumentKind.Abb, 2027));
        Assert.Equal($"{series}-ABB-2027-000002", await TakeAsync(series, DocumentKind.Abb, 2027));
    }

    /// <summary>
    /// The acceptance criterion itself. Twenty at once, each in its own connection and its own
    /// transaction, and the answer has to be one to twenty with nothing missing and nothing
    /// twice. A Postgres sequence passes the first half of this and fails the second, which is
    /// why the count is a row.
    /// </summary>
    [Fact]
    public async Task Twenty_issued_at_once_are_one_to_twenty()
    {
        var series = Series();

        var numbers = await Task.WhenAll(
            Enumerable.Range(0, 20).Select(_ => TakeAsync(series, DocumentKind.Abb, 2027)));

        Assert.Equal(
            Enumerable.Range(1, 20).Select(one => $"{series}-ABB-2027-{one:000000}"),
            numbers.Order());
    }

    /// <summary>
    /// A document that was never written leaves no hole where its number would have been. This
    /// is the whole reason the count is a row under a row lock rather than a sequence: a
    /// sequence would have moved on, and the next document would be numbered two past the last
    /// one that exists.
    /// </summary>
    [Fact]
    public async Task A_number_taken_by_something_that_failed_comes_back()
    {
        var series = Series();
        Assert.Equal($"{series}-INV-2027-000001", await TakeAsync(series, DocumentKind.Inv, 2027));

        using (var scope = api.CreateScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await using var transaction = await database.Database.BeginTransactionAsync();

            Assert.Equal(
                $"{series}-INV-2027-000002",
                await DocumentNumbers.NextAsync(
                    database, series, DocumentKind.Inv, 2027, CancellationToken.None));

            await transaction.RollbackAsync();
        }

        Assert.Equal($"{series}-INV-2027-000002", await TakeAsync(series, DocumentKind.Inv, 2027));
    }

    /// <summary>
    /// Taken outside a transaction, the promise cannot be kept — so it refuses rather than
    /// quietly handing back a number that might be orphaned.
    /// </summary>
    [Fact]
    public async Task A_number_cannot_be_taken_without_a_transaction_to_lose_it_with()
    {
        using var scope = api.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            DocumentNumbers.NextAsync(
                database, Series(), DocumentKind.Rec, 2027, CancellationToken.None));
    }

    /// <summary>
    /// One number, taken and kept, the way a caller issuing a document does it. Its own scope
    /// and its own transaction, so the concurrent test above is really concurrent.
    /// </summary>
    private async Task<string> TakeAsync(string seriesCode, DocumentKind kind, int year)
    {
        using var scope = api.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await using var transaction = await database.Database.BeginTransactionAsync();

        var number = await DocumentNumbers.NextAsync(
            database, seriesCode, kind, year, CancellationToken.None);

        await transaction.CommitAsync();
        return number;
    }

    /// <summary>
    /// A series code this test alone uses. The database is shared across the class, and a count
    /// that another test had already moved would make the numbers here depend on the order they
    /// happened to run in.
    /// </summary>
    private static string Series() => $"T{Guid.NewGuid().ToString("N")[..8].ToUpperInvariant()}";
}
