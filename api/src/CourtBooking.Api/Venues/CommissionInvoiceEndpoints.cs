using System.Security.Claims;
using CourtBooking.Api.Bookings;
using CourtBooking.Api.Data;
using CourtBooking.Api.Http;
using CourtBooking.Api.Identity;
using CourtBooking.Api.Localization;
using CourtBooking.Api.Observability;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace CourtBooking.Api.Venues;

/// <summary>
/// The commission invoice as both sides deal with it (PRD US-21): the venue reads what it owes
/// and says when it has paid, and the platform decides whether it agrees.
///
/// Two groups, because the two sides are not the same door. A venue may see and pay its own
/// invoices and nobody else's; the platform may see everyone's and is the only one who may say
/// an invoice is paid. Sending the evidence is the owner's alone — PRD US-14 puts it in the
/// list of things no permission can be granted for.
/// </summary>
public static class CommissionInvoiceEndpoints
{
    /// <summary>The platform's side: every venue's invoices, and the decision on each.</summary>
    public static void MapAdminInvoiceEndpoints(this IEndpointRouteBuilder routes)
    {
        var invoices = routes.MapGroup("/admin/commission")
            .WithTags("Platform")
            .RequireAuthorization(PlatformAdmins.PolicyName);

        invoices.MapGet("/invoices", AllAsync);
        invoices.MapPost("/invoices/{invoiceId:guid}/paid", PaidAsync);
        invoices.MapPost("/invoices/{invoiceId:guid}/refuse", RefuseAsync);
        invoices.MapGet("/invoices/{invoiceId:guid}/evidence", EvidenceAsync);
    }

    /// <summary>The venue's side: its own invoices, and saying it has paid one.</summary>
    public static void MapVenueInvoiceEndpoints(this RouteGroupBuilder venue)
    {
        // Reading what the venue owes is the venue's own business, so membership is enough —
        // and a suspended venue still owes what it owed (PRD US-20).
        venue.MapGet("/commission", MineAsync).RequireAuthorization(VenuePolicies.Member);

        // Saying the platform has been paid is the owner's alone (PRD US-14).
        venue.MapPost("/commission/{invoiceId:guid}/payment", SubmitAsync)
            .RequireAuthorization(VenuePolicies.OwnerOnly)
            .DisableAntiforgery();
    }

    private static async Task<Ok<CommissionInvoiceResponse[]>> AllAsync(
        string? status,
        AppDbContext database,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var today = PlatformRequirements.BangkokToday(timeProvider);
        var wanted = Enum.GetNames<CommissionInvoiceStatus>().Contains(status, StringComparer.Ordinal)
            ? Enum.Parse<CommissionInvoiceStatus>(status!)
            : (CommissionInvoiceStatus?)null;

        var invoices = await database.CommissionInvoices
            .AsNoTracking()
            .Where(invoice => wanted == null || invoice.Status == wanted)
            .Include(invoice => invoice.Venue)
            .OrderByDescending(invoice => invoice.Month)
            .ThenBy(invoice => invoice.Number)
            .ToListAsync(cancellationToken);

        return TypedResults.Ok(
            invoices.Select(invoice => Drawn(invoice, invoice.Venue?.Name, today, null)).ToArray());
    }

    /// <summary>
    /// This venue's invoices, newest month first, with how to pay the platform. The account is
    /// on every invoice rather than on a page somewhere: a venue paying one is looking at it.
    /// </summary>
    private static async Task<Ok<VenueCommissionResponse>> MineAsync(
        Guid venueId,
        AppDbContext database,
        IOptions<AppOptions> options,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var today = PlatformRequirements.BangkokToday(timeProvider);

        var invoices = await database.CommissionInvoices
            .AsNoTracking()
            .Where(invoice => invoice.VenueId == venueId)
            .Include(invoice => invoice.Lines)
            .OrderByDescending(invoice => invoice.Month)
            .ToListAsync(cancellationToken);

        return TypedResults.Ok(new VenueCommissionResponse(
            // Null where the platform has not said where to send the money, which the page has
            // to be able to say rather than draw an empty line and look broken.
            string.IsNullOrWhiteSpace(options.Value.PlatformPromptPayId)
                ? null
                : new PlatformAccountResponse(
                    options.Value.PlatformPromptPayId,
                    options.Value.PlatformAccountName),
            [.. invoices.Select(invoice => Drawn(invoice, null, today, invoice.Lines))]));
    }

    /// <summary>
    /// The venue says it has transferred, and shows something for it (PRD US-21). The evidence is
    /// checked the way a booker's slip is — by its first bytes, never by what the uploader called
    /// it — because it is stored and handed back to somebody later.
    /// </summary>
    private static async Task<Results<Ok<CommissionInvoiceResponse>, ProblemHttpResult>> SubmitAsync(
        Guid venueId,
        Guid invoiceId,
        IFormFile? file,
        AppDbContext database,
        ISlipStore files,
        TimeProvider timeProvider,
        ILoggerFactory loggers,
        CancellationToken cancellationToken)
    {
        if (file is null || file.Length == 0)
        {
            return ApiProblem.Of(StatusCodes.Status400BadRequest, SlipErrorCodes.NoFile);
        }

        if (file.Length > PaymentSlip.MaxBytes)
        {
            return ApiProblem.Of(StatusCodes.Status400BadRequest, SlipErrorCodes.TooLarge);
        }

        var invoice = await database.CommissionInvoices
            .SingleOrDefaultAsync(
                one => one.Id == invoiceId && one.VenueId == venueId, cancellationToken);

        if (invoice is null)
        {
            return ApiProblem.Of(StatusCodes.Status404NotFound, VenueErrorCodes.InvoiceNotFound);
        }

        // An invoice the platform has already agreed is paid is not one to send evidence for.
        if (invoice.Status == CommissionInvoiceStatus.Paid)
        {
            return ApiProblem.Of(StatusCodes.Status409Conflict, VenueErrorCodes.InvoiceAlreadyPaid);
        }

        await using var content = file.OpenReadStream();
        var start = new byte[SlipValidation.SniffBytes];
        var read = await content.ReadAtLeastAsync(
            start, start.Length, throwOnEndOfStream: false, cancellationToken);

        if (SlipValidation.ContentTypeOf(start.AsSpan(0, read)) is not { } contentType)
        {
            return ApiProblem.Of(StatusCodes.Status400BadRequest, SlipErrorCodes.UnsupportedFile);
        }

        content.Position = 0;
        var stored = await files.SaveAsync(content, contentType, cancellationToken);

        // The one before it is let go of: a venue correcting itself should not leave the platform
        // choosing between two pictures, and only the last one is what it is standing on.
        var replaced = invoice.EvidenceKey;

        invoice.EvidenceKey = stored.Name;
        invoice.SubmittedAt = timeProvider.GetUtcNow();
        invoice.Status = CommissionInvoiceStatus.PaymentSubmitted;

        // A refusal answered is a refusal spent; leaving it would show the venue a reason for
        // something it has since done again.
        invoice.RefusedReason = null;

        await database.SaveChangesAsync(cancellationToken);

        if (replaced is not null)
        {
            await files.DeleteAsync(replaced, CancellationToken.None);
        }

        AppEvents.For(loggers).LogInformation(
            "commission_payment_submitted {VenueId} {Number}", venueId, invoice.Number);

        return TypedResults.Ok(
            Drawn(invoice, null, PlatformRequirements.BangkokToday(timeProvider), null));
    }

    /// <summary>The platform has seen the money (PRD US-21).</summary>
    private static Task<Results<Ok<CommissionInvoiceResponse>, ProblemHttpResult>> PaidAsync(
        Guid invoiceId,
        ClaimsPrincipal principal,
        AppDbContext database,
        TimeProvider timeProvider,
        ILoggerFactory loggers,
        CancellationToken cancellationToken) =>
        DecideAsync(
            invoiceId, CommissionInvoiceStatus.Paid, null,
            principal, database, timeProvider, loggers, cancellationToken);

    /// <summary>
    /// The platform does not agree it has been paid, and says why (PRD US-21). The invoice goes
    /// back to being issued, so the venue can answer it again.
    /// </summary>
    private static async Task<Results<Ok<CommissionInvoiceResponse>, ProblemHttpResult>> RefuseAsync(
        Guid invoiceId,
        InvoiceRefusalRequest request,
        ClaimsPrincipal principal,
        AppDbContext database,
        TimeProvider timeProvider,
        ILoggerFactory loggers,
        CancellationToken cancellationToken)
    {
        var reason = request.Reason?.Trim();
        if (string.IsNullOrEmpty(reason) || reason.Length > CommissionInvoice.ReasonMaxLength)
        {
            return ApiProblem.Of(StatusCodes.Status400BadRequest, VenueErrorCodes.ReasonRequired);
        }

        return await DecideAsync(
            invoiceId, CommissionInvoiceStatus.Issued, reason,
            principal, database, timeProvider, loggers, cancellationToken);
    }

    private static async Task<Results<Ok<CommissionInvoiceResponse>, ProblemHttpResult>> DecideAsync(
        Guid invoiceId,
        CommissionInvoiceStatus decided,
        string? reason,
        ClaimsPrincipal principal,
        AppDbContext database,
        TimeProvider timeProvider,
        ILoggerFactory loggers,
        CancellationToken cancellationToken)
    {
        var invoice = await database.CommissionInvoices
            .Include(one => one.Venue)
            .SingleOrDefaultAsync(one => one.Id == invoiceId, cancellationToken);

        if (invoice is null)
        {
            return ApiProblem.Of(StatusCodes.Status404NotFound, VenueErrorCodes.InvoiceNotFound);
        }

        // Both decisions are answers to a claim of payment, so there has to be one to answer.
        if (invoice.Status != CommissionInvoiceStatus.PaymentSubmitted)
        {
            return ApiProblem.Of(
                StatusCodes.Status409Conflict, VenueErrorCodes.InvoiceNotAwaitingDecision);
        }

        var now = timeProvider.GetUtcNow();

        invoice.Status = decided;
        invoice.RefusedReason = reason;

        if (decided == CommissionInvoiceStatus.Paid)
        {
            invoice.PaidAt = now;
            invoice.PaidByUserId = CallerId.Of(principal);
        }

        await database.SaveChangesAsync(cancellationToken);

        AppEvents.For(loggers).LogInformation(
            decided == CommissionInvoiceStatus.Paid
                ? "commission_paid {VenueId} {Number} {AmountBaht}"
                : "commission_payment_refused {VenueId} {Number} {AmountBaht}",
            invoice.VenueId, invoice.Number, invoice.AmountBaht);

        return TypedResults.Ok(
            Drawn(invoice, invoice.Venue?.Name, PlatformRequirements.BangkokToday(timeProvider), null));
    }

    /// <summary>
    /// What the venue sent as evidence, for the platform to look at before deciding. Only the
    /// platform: it is a picture of somebody's bank account, and the venue already knows what it
    /// sent (PDPA, PRD 8).
    /// </summary>
    private static async Task<Results<FileStreamHttpResult, NotFound>> EvidenceAsync(
        Guid invoiceId,
        AppDbContext database,
        ISlipStore files,
        CancellationToken cancellationToken)
    {
        var key = await database.CommissionInvoices
            .Where(invoice => invoice.Id == invoiceId)
            .Select(invoice => invoice.EvidenceKey)
            .SingleOrDefaultAsync(cancellationToken);

        if (key is null
            || await files.OpenAsync(key, cancellationToken) is not { } content)
        {
            return TypedResults.NotFound();
        }

        return TypedResults.File(content, SlipValidation.ContentTypeOfName(key));
    }

    private static CommissionInvoiceResponse Drawn(
        CommissionInvoice invoice,
        string? venueName,
        DateOnly today,
        List<CommissionInvoiceLine>? lines) =>
        new(
            invoice.Id,
            invoice.VenueId,
            venueName,
            invoice.Number,
            invoice.Month,
            invoice.AmountBaht,
            invoice.Status.ToString(),
            // Shown beside the status rather than instead of it (PRD US-21): an invoice that is
            // late is still waiting to be paid, and the platform needs to see both at once.
            InvoiceStanding.IsOverdue(invoice.Status, invoice.DueOn, today),
            invoice.IssuedAt,
            invoice.DueOn,
            invoice.SubmittedAt,
            invoice.EvidenceKey is not null,
            invoice.PaidAt,
            invoice.RefusedReason,
            lines is null
                ? null
                : [
                    .. lines
                        .OrderBy(line => line.ServedOn)
                        .Select(line => new CommissionInvoiceLineResponse(
                            line.ServedOn, line.KeptBaht, line.Percent, line.AmountBaht)),
                ]);
}

/// <summary>
/// Whether an invoice is late (PRD US-21). Its own rule because it is not a status: an invoice
/// that is late is still waiting to be paid, and both have to be said at once.
/// </summary>
public static class InvoiceStanding
{
    public static bool IsOverdue(CommissionInvoiceStatus status, DateOnly dueOn, DateOnly today) =>
        status != CommissionInvoiceStatus.Paid && today > dueOn;
}
