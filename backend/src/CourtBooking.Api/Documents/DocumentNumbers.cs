using System.Data.Common;
using CourtBooking.Api.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace CourtBooking.Api.Documents;

/// <summary>
/// The kinds of document this system puts a number on (PRD 7.4). The three letters are part of
/// the number a customer reads back over the phone, so they are the value, not a display name.
/// </summary>
public enum DocumentKind
{
    /// <summary>ใบกำกับภาษีอย่างย่อ — a venue that is registered for VAT (PRD 7.1).</summary>
    Abb = 1,

    /// <summary>ใบกำกับภาษีเต็มรูป — issued when a booker asks for one (PRD US-07).</summary>
    Tax = 2,

    /// <summary>ใบเสร็จรับเงิน — a venue that is not registered for VAT, and the platform's own.</summary>
    Rec = 3,

    /// <summary>ใบแจ้งหนี้ — the platform's monthly commission invoice (PRD US-21).</summary>
    Inv = 4,
}

/// <summary>
/// One running count of documents: a series code, a kind, and a calendar year (PRD 7.4). The
/// number of the next document is this row and nothing else, which is what lets the count be both
/// unbroken and safe when two are issued at the same instant.
///
/// A Postgres sequence would be the obvious tool and is the wrong one: sequences deliberately do
/// not roll back, so a transaction that fails after taking a number leaves a hole in the count.
/// A row does roll back, because taking a number from it is an ordinary update under an ordinary
/// row lock — the second writer waits, and if the first gives up, the number it took comes back.
/// </summary>
public sealed class DocumentSeries
{
    public Guid Id { get; init; } = Guid.CreateVersion7();

    /// <summary>The venue's code, or <see cref="PlatformSeries"/> for the platform's own.</summary>
    public required string SeriesCode { get; init; }

    public required DocumentKind Kind { get; init; }

    /// <summary>The calendar year, in the Christian era as PRD 7.4 writes it.</summary>
    public required int Year { get; init; }

    /// <summary>How many have been issued in this series. The next one is this plus one.</summary>
    public required int Issued { get; set; }

    public const int SeriesCodeMaxLength = 16;
}

/// <summary>
/// Where a document's number comes from (PRD 7.4, US-16).
///
/// The rule the PRD asks for is stronger than "unique": the count must not skip. That is a
/// promise about the gaps as much as about the numbers, and it is only keepable if the number is
/// taken inside the same transaction as the thing it names — so this takes a transaction it did
/// not open and refuses to work without one.
/// </summary>
public static class DocumentNumbers
{
    /// <summary>The platform's own documents are not any venue's (PRD 7.4).</summary>
    public const string PlatformSeries = "PLT";

    /// <summary>
    /// Six digits, which is what PRD 7.4 writes and what a venue issuing a thousand documents a
    /// day would take three years to exhaust.
    /// </summary>
    public const int Digits = 6;

    /// <summary>
    /// Takes the next number in one series, or throws if the caller has no transaction open.
    ///
    /// The insert that gives a series its first row races with every other first row for the same
    /// series; the unique index is what settles that, and losing the race means reading the row
    /// the winner wrote rather than failing. Once the row exists this is a single statement, and
    /// the row lock it takes is held until the caller commits — which is the whole mechanism: the
    /// next writer waits behind it, and a rollback hands the number back.
    /// </summary>
    public static async Task<string> NextAsync(
        AppDbContext database,
        string seriesCode,
        DocumentKind kind,
        int year,
        CancellationToken cancellationToken)
    {
        if (database.Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException(
                "A document number has to be taken in the same transaction as the document, or a "
                + "failure after taking one leaves a gap in the count (PRD 7.4).");
        }

        // Written as one statement so that the row exists and is locked in the same breath. The
        // conflict arm updates the row it collided with, which both creates the series and takes
        // its first number without a second round trip or a window between the two.
        //
        // Sent as a command rather than through the query pipeline, which puts a SELECT around
        // whatever it is given — and Postgres will not have a data-modifying statement anywhere
        // but the top level. It runs on the caller's own transaction, which is the point: the
        // row lock it takes is released by their commit or their rollback, not by this method.
        var connection = database.Database.GetDbConnection();
        await using var command = connection.CreateCommand();
        command.Transaction = database.Database.CurrentTransaction.GetDbTransaction();
        command.CommandText =
            """
            INSERT INTO "DocumentSeries" ("Id", "SeriesCode", "Kind", "Year", "Issued")
            VALUES (@id, @code, @kind, @year, 1)
            ON CONFLICT ("SeriesCode", "Kind", "Year")
            DO UPDATE SET "Issued" = "DocumentSeries"."Issued" + 1
            RETURNING "Issued"
            """;

        Add(command, "@id", Guid.CreateVersion7());
        Add(command, "@code", seriesCode);
        Add(command, "@kind", (int)kind);
        Add(command, "@year", year);

        var issued = (int)(await command.ExecuteScalarAsync(cancellationToken))!;

        return Format(seriesCode, kind, year, issued);
    }

    private static void Add(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    /// <summary>
    /// The number as it is written and read: `{series}-{kind}-{year}-{six digits}` (PRD 7.4).
    /// Its own method so that the one place that makes a number and the one place that reads one
    /// back cannot drift apart.
    /// </summary>
    public static string Format(string seriesCode, DocumentKind kind, int year, int sequence) =>
        $"{seriesCode}-{kind.ToString().ToUpperInvariant()}-{year:0000}-{sequence.ToString().PadLeft(Digits, '0')}";
}
