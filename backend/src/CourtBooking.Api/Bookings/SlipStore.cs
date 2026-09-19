using System.Security.Cryptography;

namespace CourtBooking.Api.Bookings;

/// <summary>
/// Where the slip images live. Behind an interface because the bytes will move to object storage
/// before this is in front of real venues (PRD 9.1), and nothing above here should have to care.
/// </summary>
public interface ISlipStore
{
    /// <summary>
    /// Writes the bytes and answers what was written: the name to ask for them back by, how many
    /// bytes there were, and their SHA-256. The name is the store's, never the booker's.
    /// </summary>
    Task<StoredSlip> SaveAsync(Stream content, string contentType, CancellationToken cancellationToken);

    /// <summary>Opens a slip for reading, or null when the store no longer holds it.</summary>
    Task<Stream?> OpenAsync(string storedName, CancellationToken cancellationToken);

    /// <summary>
    /// Forgets a slip. Needed when a write is rolled back, and by the erasure a booker may ask for
    /// (PDPA, PRD 8) — a row that cascades away must not leave its bytes behind.
    /// </summary>
    Task DeleteAsync(string storedName, CancellationToken cancellationToken);
}

public readonly record struct StoredSlip(string Name, long ByteSize, string Sha256);

/// <summary>
/// Slips on the server's own disk, under a directory given by configuration. The whole directory
/// is one flat namespace of generated names: nothing a booker sends is ever part of a path, so
/// there is nothing to traverse with.
/// </summary>
public sealed class LocalSlipStore(string root) : ISlipStore
{
    public async Task<StoredSlip> SaveAsync(
        Stream content,
        string contentType,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(root);

        var name = $"{Guid.CreateVersion7():N}{SlipValidation.ExtensionOf(contentType)}";
        var path = Path.Combine(root, name);

        // Hashed while it is written, so the bytes are read once and the hash is of what landed.
        using var hash = SHA256.Create();
        await using var file = File.Create(path);
        await using var hashing = new CryptoStream(file, hash, CryptoStreamMode.Write);

        await content.CopyToAsync(hashing, cancellationToken);
        await hashing.FlushFinalBlockAsync(cancellationToken);

        return new StoredSlip(name, file.Length, Convert.ToHexStringLower(hash.Hash!));
    }

    public Task DeleteAsync(string storedName, CancellationToken cancellationToken)
    {
        if (Resolve(storedName) is { } path)
        {
            File.Delete(path);
        }

        return Task.CompletedTask;
    }

    public Task<Stream?> OpenAsync(string storedName, CancellationToken cancellationToken)
    {
        var path = Resolve(storedName);
        return Task.FromResult<Stream?>(
            path is not null && File.Exists(path) ? File.OpenRead(path) : null);
    }

    /// <summary>
    /// The file a stored name points at, or null if the name could point anywhere else. The name
    /// came from SaveAsync, but it arrives here from the database, so it is checked rather than
    /// trusted: a name with a separator in it would leave the directory.
    /// </summary>
    private string? Resolve(string storedName) =>
        storedName.Length == 0
        || storedName.AsSpan().ContainsAny('/', '\\', ':')
        || storedName.Contains("..")
            ? null
            : Path.Combine(root, storedName);
}
