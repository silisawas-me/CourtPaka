namespace CourtBooking.Api.Observability;

/// <summary>
/// Product events (PRD 8): the things we want to count after a deploy, as opposed to the diagnostics
/// the rest of the logs carry. They all go under one category so a sink can pick them out without
/// knowing which class raised them, and so the category is spelled once rather than at each site.
/// </summary>
public static class AppEvents
{
    public const string Category = "CourtBooking.Events";

    /// <summary>The logger every event is written through. The factory caches it by category.</summary>
    public static ILogger For(ILoggerFactory loggers) => loggers.CreateLogger(Category);
}
