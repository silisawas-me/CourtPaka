using System.Security.Claims;

namespace CourtBooking.Api.Identity;

/// <summary>
/// Who is making the request. The id lives in one claim, and reading it was spelled out in four
/// places with three different reactions to it being absent.
/// </summary>
public static class CallerId
{
    /// <summary>
    /// The signed-in user. Only for handlers behind authorization: reaching one without the claim
    /// means the pipeline let an unauthenticated request through, which is a bug, not a 401.
    /// </summary>
    public static Guid Of(ClaimsPrincipal principal) =>
        TryOf(principal)
        ?? throw new InvalidOperationException("An authorized request carried no user id claim.");

    /// <summary>The signed-in user, or null where the caller may be anonymous.</summary>
    public static Guid? TryOf(ClaimsPrincipal principal) =>
        Guid.TryParse(principal.FindFirstValue(ClaimTypes.NameIdentifier), out var userId)
            ? userId
            : null;
}
