using Microsoft.AspNetCore.Http.HttpResults;

namespace CourtBooking.Api.Http;

/// <summary>
/// Every API error carries a stable <c>code</c> that the frontend translates (PRD US-23);
/// messages in the payload are for developers, never shown to users.
/// </summary>
public static class ApiProblem
{
    public const string CodeProperty = "code";

    public static ProblemHttpResult Of(int statusCode, string code) =>
        TypedResults.Problem(
            statusCode: statusCode,
            extensions: new Dictionary<string, object?> { [CodeProperty] = code });

    /// <summary>
    /// A refusal that has to hand something back for the screen to show — the bookings standing
    /// in the way of a court closing (PRD US-11), where naming the rule is not enough to act on.
    /// The extra data is named, never prose: the code still decides what the user is told.
    /// </summary>
    public static ProblemHttpResult Of(int statusCode, string code, string name, object? value) =>
        TypedResults.Problem(
            statusCode: statusCode,
            extensions: new Dictionary<string, object?>
            {
                [CodeProperty] = code,
                [name] = value,
            });
}
