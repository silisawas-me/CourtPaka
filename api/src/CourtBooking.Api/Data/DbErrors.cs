using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace CourtBooking.Api.Data;

public static class DbErrors
{
    /// <summary>23505 is PostgreSQL's unique_violation; the database is the only place that can be sure.</summary>
    public static bool IsUniqueViolation(DbUpdateException exception) =>
        exception.InnerException is PostgresException { SqlState: "23505" };
}
