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
}
