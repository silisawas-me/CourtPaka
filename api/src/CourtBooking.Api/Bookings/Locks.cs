using CourtBooking.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace CourtBooking.Api.Bookings;

/// <summary>
/// The queue everybody writing against the same thing stands in.
///
/// Reading a balance and then writing from it is only safe if the readers take turns, and a
/// conditional update cannot make them: taking part of what is owed changes no status, so there is
/// nothing for a <c>WHERE</c> to catch. Two tills — or one double click — both read the same total,
/// both find room for the whole of it, and both write.
///
/// The lock is held for the transaction, so every caller has to be inside one, and the lock buys
/// nothing unless the read that follows is inside it too.
///
/// One home for it so that the key is derived one way. Two callers reaching for the same booking
/// through two different derivations take two different locks and queue behind nobody.
/// </summary>
internal static class Locks
{
    /// <summary>Queues behind anyone else writing against this one thing.</summary>
    public static Task OnAsync(
        AppDbContext database,
        Guid id,
        CancellationToken cancellationToken) =>
        OnAsync(database, [id], cancellationToken);

    /// <summary>
    /// Queues behind anyone else writing against any of these, in an order everyone agrees on.
    /// Without the fixed order two callers holding the same pair take them the other way round
    /// and Postgres breaks the tie by killing one of them.
    /// </summary>
    public static async Task OnAsync(
        AppDbContext database,
        IEnumerable<Guid> ids,
        CancellationToken cancellationToken)
    {
        foreach (var key in ids.Select(KeyOf).Distinct().Order())
        {
            await database.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT pg_advisory_xact_lock({key})", cancellationToken);
        }
    }

    /// <summary>
    /// One number per thing, the same for everyone asking for it. Two different things sharing a
    /// number only means they queue behind each other, which costs a moment and nothing else.
    /// </summary>
    private static long KeyOf(Guid id)
    {
        Span<byte> bytes = stackalloc byte[16];
        id.TryWriteBytes(bytes);
        return BitConverter.ToInt64(bytes[..8]) ^ BitConverter.ToInt64(bytes[8..]);
    }
}
