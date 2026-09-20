using CourtBooking.Api.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace CourtBooking.Api.Bookings;

/// <summary>
/// Handing a slip over, for the two people allowed to ask for one: the booker who sent it
/// (PRD US-04) and the venue checking it (PRD US-12). Who may ask is the caller's to decide — it
/// says so by handing in a query already narrowed to the slips that reader may see.
/// </summary>
internal static class SlipDownload
{
    /// <summary>
    /// Newest first, so the first row is the one being checked. Slips are added and never
    /// replaced, and two sent in the same second sort by id, which is a v7 and so carries the
    /// order they were made in.
    /// </summary>
    public static IQueryable<PaymentSlip> NewestFirst(this IQueryable<PaymentSlip> slips) =>
        slips.OrderByDescending(slip => slip.UploadedAt).ThenByDescending(slip => slip.Id);

    /// <summary>
    /// The newest of the slips a query selects, as a download.
    ///
    /// Never rendered in the page's own origin. A PDF is a program as much as a document, and this
    /// one came from whoever is holding the booking; handing it to the browser as a download
    /// rather than a view means nothing in it runs next to the reader's session. nosniff is set
    /// here as well as at the proxy, because the API is reachable without one.
    /// </summary>
    public static async Task<Results<FileStreamHttpResult, NotFound, ProblemHttpResult>> NewestAsync(
        IQueryable<PaymentSlip> readable,
        ISlipStore slips,
        HttpResponse response,
        CancellationToken cancellationToken)
    {
        var slip = await readable.AsNoTracking().NewestFirst().FirstOrDefaultAsync(cancellationToken);
        if (slip is null)
        {
            return ApiProblem.Of(StatusCodes.Status404NotFound, SlipErrorCodes.NoSlip);
        }

        var content = await slips.OpenAsync(slip.StoredName, cancellationToken);
        if (content is null)
        {
            return TypedResults.NotFound();
        }

        response.Headers.XContentTypeOptions = "nosniff";
        return TypedResults.File(content, slip.ContentType, fileDownloadName: slip.StoredName);
    }
}
