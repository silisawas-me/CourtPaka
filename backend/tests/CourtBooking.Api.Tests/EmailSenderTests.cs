using CourtBooking.Api.Email;
using CourtBooking.Api.Tests.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CourtBooking.Api.Tests;

/// <summary>Which sender a host ends up with is the part that keeps tokens out of deployed logs.</summary>
[Collection(ApiCollection.Name)]
public sealed class EmailSenderWiringTests(ApiTestFixture fixture)
{
    [Theory]
    [InlineData("Development", typeof(LoggingEmailSender))]
    [InlineData("Testing", typeof(UndeliveredEmailSender))]
    [InlineData("Production", typeof(UndeliveredEmailSender))]
    public void OnlyADevelopmentHostLogsTheMessageItself(string environment, Type expected)
    {
        using var api = new ApiFactory(fixture.ConnectionString)
            .WithWebHostBuilder(builder => builder.UseEnvironment(environment));

        Assert.IsType(expected, api.Services.GetRequiredService<ITransactionalEmailSender>());
    }
}

public sealed class EmailSenderTests
{
    private static readonly EmailMessage Invitation = new(
        "staff@example.com",
        "th",
        "Invitation to Smash Court",
        "Accept the invitation within 7 days: https://courtpaka.test/venue-invitation?token=SECRET-TOKEN");

    [Fact]
    public async Task DeployedSenderKeepsTheTokenAndTheAddressOutOfTheLog()
    {
        var log = new CapturingLogger<UndeliveredEmailSender>();

        await new UndeliveredEmailSender(log).SendAsync(Invitation);

        var written = Assert.Single(log.Entries);
        Assert.Equal(LogLevel.Warning, written.Level);
        Assert.DoesNotContain("SECRET-TOKEN", written.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("staff@example.com", written.Message, StringComparison.Ordinal);
        // Enough to tie a support request to an account: the domain and which message it was.
        Assert.Contains("@example.com", written.Message, StringComparison.Ordinal);
        Assert.Contains("Invitation to Smash Court", written.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DevelopmentSenderLogsTheLinkSoTheLocalLoopCanFollowIt()
    {
        var log = new CapturingLogger<LoggingEmailSender>();

        await new LoggingEmailSender(log).SendAsync(Invitation);

        Assert.Contains("SECRET-TOKEN", Assert.Single(log.Entries).Message, StringComparison.Ordinal);
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));
    }
}
