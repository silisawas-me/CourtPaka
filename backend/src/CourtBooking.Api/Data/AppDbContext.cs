using Microsoft.EntityFrameworkCore;

namespace CourtBooking.Api.Data;

public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options);
