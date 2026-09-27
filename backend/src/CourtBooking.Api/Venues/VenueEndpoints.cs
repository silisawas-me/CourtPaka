using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using CourtBooking.Api.Data;
using CourtBooking.Api.Email;
using CourtBooking.Api.Http;
using CourtBooking.Api.Bookings;
using CourtBooking.Api.Identity;
using CourtBooking.Api.Observability;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace CourtBooking.Api.Venues;

public static class VenueEndpoints
{
    /// <summary>An invitation has to outlive a weekend but not a month (PRD US-14).</summary>
    private static readonly TimeSpan InvitationLifetime = TimeSpan.FromDays(7);

    public static RouteGroupBuilder MapVenueEndpoints(this IEndpointRouteBuilder routes)
    {
        var venues = routes.MapGroup("/venues").WithTags("Venues").RequireAuthorization();

        venues.MapPost("/", CreateAsync);
        venues.MapGet("/mine", ListMineAsync);
        venues.MapGet("/agreement", AgreementAsync);
        venues.MapPost("/invitations/accept", AcceptInvitationAsync);

        var venue = venues.MapGroup("/{venueId:guid}");
        venue.MapGet("/", Get).RequireAuthorization(VenuePolicies.Member);
        venue.MapPut("/", UpdateDetailsAsync).RequireAuthorization(VenuePolicies.Settings);

        // The venue's tax identity and where its money lands are the owner's alone (PRD US-14),
        // and stay reachable after a refusal, because putting them right is how a venue answers
        // one (PRD US-10).
        venue.MapGet("/business", BusinessAsync)
            .RequireAuthorization(VenuePolicies.OwnerAnsweringRefusal);
        venue.MapPut("/business", UpdateBusinessAsync)
            .RequireAuthorization(VenuePolicies.OwnerAnsweringRefusal);
        venue.MapPost("/resubmit", ResubmitAsync)
            .RequireAuthorization(VenuePolicies.OwnerAnsweringRefusal);
        venue.MapGet("/attention", WaitingForAsync).RequireAuthorization(VenuePolicies.Member);

        // What this venue owes the platform, and saying it has paid (PRD US-21). Each door
        // declares who may open it: reading is any member's, paying is the owner's alone.
        venue.MapVenueInvoiceEndpoints();
        // How long the counter waits for somebody is a setting like the prices are (PRD US-24).
        venue.MapPut("/grace", SetGraceAsync).RequireAuthorization(VenuePolicies.Settings);
        venue.MapPut("/deposit", SetDepositAsync).RequireAuthorization(VenuePolicies.Settings);
        venue.MapPut("/risk-rule", SetRiskRuleAsync).RequireAuthorization(VenuePolicies.Settings);
        venue.MapPut("/notifications", ChooseNotificationsAsync)
            .RequireAuthorization(VenuePolicies.OwnChoice);
        venue.MapGet("/members", ListMembersAsync).RequireAuthorization(VenuePolicies.Member);
        venue.MapGet("/invitations", ListInvitationsAsync).RequireAuthorization(VenuePolicies.OwnerOnly);
        venue.MapPost("/invitations", InviteAsync).RequireAuthorization(VenuePolicies.OwnerOnly);
        venue.MapPut("/members/{userId:guid}/permissions", ChangePermissionsAsync).RequireAuthorization(VenuePolicies.OwnerOnly);
        venue.MapDelete("/members/{userId:guid}", RemoveMemberAsync).RequireAuthorization(VenuePolicies.OwnerOnly);

        venue.MapCourtEndpoints();
        venue.MapClosureEndpoints();
        venue.MapPricingEndpoints();
        venue.MapVerifySlipEndpoints();
        venue.MapVenueBookingEndpoints();
        venue.MapVenueWaitlistEndpoints();
        // What the day took, and the count at the end of it (PRD US-26).
        venue.MapDayMoneyEndpoints();
        venue.MapVenueDashboardEndpoints();

        return venues;
    }

    /// <summary>
    /// What is waiting here for the person asking (PRD US-17). A number beside a door is the
    /// whole of the in-system notice: a counter opens the page it belongs to, not an inbox.
    /// </summary>
    private static async Task<Ok<VenueAttentionResponse>> WaitingForAsync(
        CurrentVenue venue,
        VenueNotifications notifications,
        CancellationToken cancellationToken) =>
        TypedResults.Ok(
            await notifications.WaitingForAsync(venue.Require(), cancellationToken));

    /// <summary>
    /// This member saying whether they want to hear each time a slip arrives here. It is the only
    /// notice that can be turned off, and it is turned off per venue: somebody working two
    /// counters may want to hear from one of them (PRD US-17).
    ///
    /// Behind Member rather than a permission, and allowed even where the venue is frozen: what
    /// somebody wants in their own inbox is theirs and not the venue's, and a venue that has been
    /// suspended is exactly where a member might want the mail to stop.
    /// </summary>
    private static async Task<NoContent> ChooseNotificationsAsync(
        NotificationPreferenceRequest request,
        CurrentVenue venue,
        AppDbContext database,
        CancellationToken cancellationToken)
    {
        var membership = venue.Require();

        await database.VenueMemberships
            .Where(member => member.Id == membership.Id)
            .ExecuteUpdateAsync(
                set => set.SetProperty(
                    member => member.WantsSlipEmails, request.WantsSlipEmails),
                cancellationToken);

        return TypedResults.NoContent();
    }

    /// <summary>
    /// Which agreement the platform is asking venues to accept (PRD US-10, Q8). Asked for before
    /// applying, and sent back with the application, so what was on screen is what is recorded.
    /// </summary>
    private static Ok<VenueAgreementResponse> AgreementAsync(IOptions<AppOptions> options) =>
        TypedResults.Ok(new VenueAgreementResponse(options.Value.VenueAgreementVersion));

    private static async Task<Results<Created<VenueResponse>, ProblemHttpResult>> CreateAsync(
        CreateVenueRequest request,
        ClaimsPrincipal principal,
        AppDbContext database,
        IOptions<AppOptions> options,
        TimeProvider timeProvider,
        ILoggerFactory loggers,
        CancellationToken cancellationToken)
    {
        var invalid = VenueValidation.ValidateCode(request.Code)
                      ?? VenueValidation.ValidateName(request.Name)
                      ?? VenueValidation.ValidateAddress(
                          request.AddressLine, request.District, request.Province)
                      ?? VenueValidation.ValidateBusiness(request.Business);
        if (invalid is not null)
        {
            return ApiProblem.Of(StatusCodes.Status400BadRequest, invalid);
        }

        // What was agreed to has to be the thing that was shown. A version that moved on while
        // the form was open is refused rather than recorded as the new one (PRD US-10, Q8).
        if (!string.Equals(
                request.AgreementVersion, options.Value.VenueAgreementVersion, StringComparison.Ordinal))
        {
            return ApiProblem.Of(
                StatusCodes.Status409Conflict, VenueErrorCodes.AgreementOutOfDate);
        }

        var now = timeProvider.GetUtcNow();
        var venue = new Venue
        {
            Code = request.Code.Trim().ToUpperInvariant(),
            Name = request.Name.Trim(),
            AddressLine = request.AddressLine.Trim(),
            District = request.District.Trim(),
            Province = request.Province.Trim(),
            Business = Written(request.Business),
            AgreementVersion = options.Value.VenueAgreementVersion,
            AgreementAcceptedAt = now,
            AgreementAcceptedByUserId = CallerId.Of(principal),
            CreatedAt = now,
        };

        database.Venues.Add(venue);
        // The terms a venue starts with are a row like any other, so a booking made on day one has
        // a policy to point at rather than a default resolved somewhere else (PRD S-11, BR-05).
        database.CancellationPolicies.Add(CancellationPolicy.Create(
            venue.Id, CancellationPolicy.Default, CallerId.Of(principal), now));
        // The person who applies runs the venue, so they start as its owner (PRD US-10).
        var membership = new VenueMembership
        {
            VenueId = venue.Id,
            UserId = CallerId.Of(principal),
            Role = VenueRole.Owner,
            Permissions = VenuePermissions.None, // Owners derive their permissions from the role.
            CreatedAt = now,
        };

        database.VenueMemberships.Add(membership);

        database.MembershipChanges.Add(new MembershipChange
        {
            VenueId = venue.Id,
            UserId = membership.UserId,
            Kind = MembershipChangeKind.Joined,
            Role = VenueRole.Owner,
            PermissionsAfter = membership.Permissions,
            ChangedByUserId = membership.UserId,
            ChangedAt = now,
        });

        // The first row of the venue's standing: it applied. Nobody decided this, the venue asked,
        // so there is no admin to name (VenueStatusChange.ChangedByUserId, PRD US-10, US-20).
        database.VenueStatusChanges.Add(new VenueStatusChange
        {
            VenueId = venue.Id,
            From = null,
            To = VenueStatus.Pending,
            ChangedAt = now,
            ChangedByUserId = null,
        });

        // The row, not the cookie: an account suspended or forgotten a moment ago must not come
        // out of this owning a venue nobody can reach (PRD US-22, S-15).
        await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
        if (await AccountGate.RefusalAsync(database, CallerId.Of(principal), cancellationToken) is { } closed)
        {
            return ApiProblem.Of(StatusCodes.Status403Forbidden, closed);
        }

        // Everything the platform and its bookers say to a venue is an email, so a LINE account
        // that shared no address cannot be the one running it (PRD US-01, US-17, US-20).
        var ownerAddress = await database.Users
            .Where(user => user.Id == CallerId.Of(principal))
            .Select(user => user.Email)
            .SingleOrDefaultAsync(cancellationToken);
        if (string.IsNullOrEmpty(ownerAddress))
        {
            return ApiProblem.Of(StatusCodes.Status403Forbidden, VenueErrorCodes.OwnerNeedsEmail);
        }

        try
        {
            await database.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (DbErrors.IsUniqueViolation(exception))
        {
            return ApiProblem.Of(StatusCodes.Status409Conflict, VenueErrorCodes.CodeAlreadyUsed);
        }

        await transaction.CommitAsync(cancellationToken);

        AppEvents.For(loggers).LogInformation(
            "venue_applied {VenueId} {Resubmitted}", venue.Id, false);

        return TypedResults.Created($"/api/venues/{venue.Id}", ToResponse(venue, membership));
    }

    private static async Task<Ok<VenueResponse[]>> ListMineAsync(
        ClaimsPrincipal principal,
        AppDbContext database,
        CancellationToken cancellationToken)
    {
        var userId = CallerId.Of(principal);
        var memberships = await database.VenueMemberships
            .AsNoTracking()
            .Where(member => member.UserId == userId)
            .Include(member => member.Venue)
            .OrderBy(member => member.Venue!.Name)
            .ToListAsync(cancellationToken);

        return TypedResults.Ok(memberships
            .Select(member => ToResponse(member.Venue!, member))
            .ToArray());
    }

    private static Ok<VenueResponse> Get(CurrentVenue currentVenue)
    {
        // Authorization already loaded the venue and the caller's membership for this request.
        var membership = currentVenue.Require();
        return TypedResults.Ok(ToResponse(membership.Venue!, membership));
    }

    private static async Task<Results<NoContent, NotFound, ProblemHttpResult>> UpdateDetailsAsync(
        Guid venueId,
        UpdateVenueRequest request,
        AppDbContext database,
        CancellationToken cancellationToken)
    {
        var invalid = VenueValidation.ValidateName(request.Name)
                      ?? VenueValidation.ValidateAddress(
                          request.AddressLine, request.District, request.Province);
        if (invalid is not null)
        {
            return ApiProblem.Of(StatusCodes.Status400BadRequest, invalid);
        }

        var updated = await database.Venues
            .Where(venue => venue.Id == venueId)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(venue => venue.Name, request.Name.Trim())
                    .SetProperty(venue => venue.AddressLine, request.AddressLine.Trim())
                    .SetProperty(venue => venue.District, request.District.Trim())
                    .SetProperty(venue => venue.Province, request.Province.Trim()),
                cancellationToken);

        return updated == 1 ? TypedResults.NoContent() : TypedResults.NotFound();
    }

    private static async Task<Results<Ok<VenueBusinessResponse>, NotFound>> BusinessAsync(
        Guid venueId,
        AppDbContext database,
        CancellationToken cancellationToken)
    {
        var business = await database.Venues
            .AsNoTracking()
            .Where(venue => venue.Id == venueId)
            .Select(venue => venue.Business)
            .SingleOrDefaultAsync(cancellationToken);

        return business is null ? TypedResults.NotFound() : TypedResults.Ok(Drawn(business));
    }

    /// <summary>
    /// Correcting where the money goes and who the venue is for tax (PRD US-10, US-14). The
    /// platform is not told: a venue that was turned away says so itself, by asking again.
    /// </summary>
    private static async Task<Results<Ok<VenueBusinessResponse>, NotFound, ProblemHttpResult>>
        UpdateBusinessAsync(
            Guid venueId,
            VenueBusinessRequest request,
            AppDbContext database,
            CancellationToken cancellationToken)
    {
        if (VenueValidation.ValidateBusiness(request) is { } invalid)
        {
            return ApiProblem.Of(StatusCodes.Status400BadRequest, invalid);
        }

        var venue = await database.Venues
            .SingleOrDefaultAsync(one => one.Id == venueId, cancellationToken);

        if (venue is null)
        {
            return TypedResults.NotFound();
        }

        venue.Business = Written(request);
        await database.SaveChangesAsync(cancellationToken);

        return TypedResults.Ok(Drawn(venue.Business));
    }

    /// <summary>
    /// A venue the platform turned away, asking to be looked at again (PRD US-10). Conditional on
    /// still being refused, so two presses do not walk an approved venue back to waiting.
    /// </summary>
    private static async Task<Results<Ok<VenueResponse>, ProblemHttpResult>> ResubmitAsync(
        Guid venueId,
        CurrentVenue currentVenue,
        AppDbContext database,
        TimeProvider timeProvider,
        ILoggerFactory loggers,
        CancellationToken cancellationToken)
    {
        // The move and its record together, like every other change of standing: a venue that
        // asked again without a row saying so would read, later, as never having been refused.
        await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);

        var moved = await database.Venues
            .Where(venue => venue.Id == venueId && venue.Status == VenueStatus.Rejected)
            .ExecuteUpdateAsync(
                set => set.SetProperty(venue => venue.Status, VenueStatus.Pending),
                cancellationToken);

        if (moved == 0)
        {
            return ApiProblem.Of(
                StatusCodes.Status409Conflict, VenueErrorCodes.NotRefused);
        }

        database.VenueStatusChanges.Add(new VenueStatusChange
        {
            VenueId = venueId,
            From = VenueStatus.Rejected,
            To = VenueStatus.Pending,
            ChangedAt = timeProvider.GetUtcNow(),
            ChangedByUserId = null,
        });
        await database.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        AppEvents.For(loggers).LogInformation(
            "venue_applied {VenueId} {Resubmitted}", venueId, true);

        var membership = currentVenue.Require();
        var venue = await database.Venues
            .AsNoTracking()
            .SingleAsync(one => one.Id == venueId, cancellationToken);

        return TypedResults.Ok(ToResponse(venue, membership));
    }

    private static VenueBusiness Written(VenueBusinessRequest request) =>
        new()
        {
            PromptPayId = request.PromptPayId.Trim(),
            PromptPayAccountName = request.PromptPayAccountName.Trim(),
            IsVatRegistered = request.IsVatRegistered,
            LegalName = request.LegalName.Trim(),
            TaxId = request.TaxId.Trim(),
            TaxBranch = request.TaxBranch.Trim(),
            BillingAddress = request.BillingAddress.Trim(),
            Latitude = request.Latitude,
            Longitude = request.Longitude,
        };

    private static VenueBusinessResponse Drawn(VenueBusiness business) =>
        new(
            business.PromptPayId,
            business.PromptPayAccountName,
            business.IsVatRegistered,
            business.LegalName,
            business.TaxId,
            business.TaxBranch,
            business.BillingAddress,
            business.Latitude,
            business.Longitude);

    private static async Task<Ok<VenueMemberResponse[]>> ListMembersAsync(
        Guid venueId,
        AppDbContext database,
        CancellationToken cancellationToken)
    {
        var members = await database.VenueMemberships
            .AsNoTracking()
            .Where(member => member.VenueId == venueId)
            .Include(member => member.User)
            .OrderBy(member => member.CreatedAt)
            .ToListAsync(cancellationToken);

        return TypedResults.Ok(members.Select(ToResponse).ToArray());
    }

    private static async Task<Ok<VenueInvitationResponse[]>> ListInvitationsAsync(
        Guid venueId,
        AppDbContext database,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var invitations = await database.VenueInvitations
            .AsNoTracking()
            .Where(invitation =>
                invitation.VenueId == venueId && invitation.AcceptedAt == null && invitation.ExpiresAt > now)
            .OrderBy(invitation => invitation.CreatedAt)
            .ToListAsync(cancellationToken);

        return TypedResults.Ok(invitations
            .Select(invitation => new VenueInvitationResponse(
                invitation.Id, invitation.Email, VenuePermissionSet.Describe(invitation.Permissions), invitation.ExpiresAt))
            .ToArray());
    }

    /// <summary>
    /// Invites an address, which may not have an account yet: the link in the email is what grants
    /// access, once the person signs in and accepts it (PRD US-14).
    /// </summary>
    private static async Task<Results<Created<VenueInvitationResponse>, ProblemHttpResult>> InviteAsync(
        Guid venueId,
        InviteMemberRequest request,
        AppDbContext database,
        CurrentVenue currentVenue,
        UserManager<AppUser> userManager,
        ITransactionalEmailSender emailSender,
        IOptions<AppOptions> options,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        VenuePermissions permissions;
        if (request.Permissions is null)
        {
            permissions = VenuePermissions.StaffDefault;
        }
        else if (!VenuePermissionSet.TryParse(request.Permissions, out permissions))
        {
            return ApiProblem.Of(StatusCodes.Status400BadRequest, VenueErrorCodes.InvalidPermissions);
        }

        var invalidEmail = VenueValidation.ValidateEmail(request.Email);
        if (invalidEmail is not null)
        {
            return ApiProblem.Of(StatusCodes.Status400BadRequest, invalidEmail);
        }

        var email = request.Email.Trim();
        var normalizedEmail = Normalize(email);
        var existing = await userManager.FindByEmailAsync(email);
        if (existing is not null
            && await database.VenueMemberships.AnyAsync(
                member => member.VenueId == venueId && member.UserId == existing.Id, cancellationToken))
        {
            return ApiProblem.Of(StatusCodes.Status409Conflict, VenueErrorCodes.AlreadyMember);
        }

        var now = timeProvider.GetUtcNow();

        // One live invitation per address: re-inviting replaces the old link instead of leaving
        // several valid tokens the owner cannot revoke. Matching is on the normalized address, so
        // a difference in capitalisation cannot leave an older, more permissive link alive.
        await database.VenueInvitations
            .Where(item =>
                item.VenueId == venueId && item.NormalizedEmail == normalizedEmail && item.AcceptedAt == null)
            .ExecuteDeleteAsync(cancellationToken);

        var token = GenerateToken();
        var invitation = new VenueInvitation
        {
            VenueId = venueId,
            Email = email,
            NormalizedEmail = normalizedEmail,
            Permissions = permissions,
            TokenHash = HashToken(token),
            ExpiresAt = now + InvitationLifetime,
            CreatedAt = now,
        };

        database.VenueInvitations.Add(invitation);
        await database.SaveChangesAsync(cancellationToken);

        var link = QueryHelpers.AddQueryString(
            $"{options.Value.BaseUrl.TrimEnd('/')}/venue-invitation",
            new Dictionary<string, string?> { ["invitationId"] = invitation.Id.ToString(), ["token"] = token });

        // In the invited person's language if they already have an account, Thai otherwise.
        var language = existing?.Language ?? SupportedLanguages.Default;
        var (subject, body) = AccountLetters.Invitation(language, currentVenue.Require().Venue!.Name, link);
        await emailSender.SendAsync(
            new EmailMessage(email, language, subject, body, AccountLetters.InvitationTemplate),
            cancellationToken);

        return TypedResults.Created(
            $"/api/venues/{venueId}/invitations",
            new VenueInvitationResponse(
                invitation.Id, email, VenuePermissionSet.Describe(permissions), invitation.ExpiresAt));
    }

    private static async Task<Results<Ok<VenueResponse>, ProblemHttpResult>> AcceptInvitationAsync(
        AcceptInvitationRequest request,
        ClaimsPrincipal principal,
        AppDbContext database,
        UserManager<AppUser> userManager,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var invitation = await database.VenueInvitations
            .Include(item => item.Venue)
            .SingleOrDefaultAsync(item => item.Id == request.InvitationId, cancellationToken);

        if (invitation is null
            || invitation.AcceptedAt is not null
            || invitation.ExpiresAt <= now
            || !CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(invitation.TokenHash), Encoding.UTF8.GetBytes(HashToken(request.Token))))
        {
            return ApiProblem.Of(StatusCodes.Status400BadRequest, VenueErrorCodes.InvitationInvalid);
        }

        var user = await userManager.GetUserAsync(principal);
        if (user is null)
        {
            return ApiProblem.Of(StatusCodes.Status400BadRequest, VenueErrorCodes.InvitationInvalid);
        }

        // The invitation names an address; only that person may take it.
        if (Normalize(user.Email) != invitation.NormalizedEmail)
        {
            return ApiProblem.Of(StatusCodes.Status403Forbidden, VenueErrorCodes.InvitationForAnotherAddress);
        }

        // This endpoint has no venue in its route, so it never passes the venue policy: the freeze
        // has to be checked here too, or a suspended venue could still take on new members.
        if (VenueStatusRules.IsFrozen(invitation.Venue?.Status))
        {
            return ApiProblem.Of(StatusCodes.Status403Forbidden, VenueErrorCodes.NotApproved);
        }

        if (await database.VenueMemberships.AnyAsync(
                member => member.VenueId == invitation.VenueId && member.UserId == user.Id, cancellationToken))
        {
            // Nothing left to accept, so the invitation stops being pending.
            invitation.AcceptedAt = now;
            await database.SaveChangesAsync(cancellationToken);
            return ApiProblem.Of(StatusCodes.Status409Conflict, VenueErrorCodes.AlreadyMember);
        }

        var membership = new VenueMembership
        {
            VenueId = invitation.VenueId,
            UserId = user.Id,
            Role = VenueRole.Staff,
            Permissions = invitation.Permissions,
            CreatedAt = now,
        };

        database.VenueMemberships.Add(membership);
        invitation.AcceptedAt = now;
        database.MembershipChanges.Add(new MembershipChange
        {
            VenueId = invitation.VenueId,
            UserId = user.Id,
            Kind = MembershipChangeKind.Joined,
            Role = VenueRole.Staff,
            PermissionsAfter = invitation.Permissions,
            ChangedByUserId = user.Id,
            ChangedAt = now,
        });

        // Queued behind a deletion in progress, so a seat is never given to a forgotten account.
        await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
        if (await AccountGate.RefusalAsync(database, user.Id, cancellationToken) is { } closed)
        {
            return ApiProblem.Of(StatusCodes.Status403Forbidden, closed);
        }

        try
        {
            await database.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (DbErrors.IsUniqueViolation(exception))
        {
            // Two accepts raced (a double click, a retry); the membership already exists.
            return ApiProblem.Of(StatusCodes.Status409Conflict, VenueErrorCodes.AlreadyMember);
        }

        await transaction.CommitAsync(cancellationToken);

        return TypedResults.Ok(ToResponse(invitation.Venue!, membership));
    }

    /// <summary>
    /// How long this venue waits after the hour starts before nobody having come is what it is
    /// (PRD US-24). A court by a main road and a court in a mall do not wait the same.
    /// </summary>
    private static async Task<Results<NoContent, ProblemHttpResult>> SetGraceAsync(
        Guid venueId,
        GraceRequest request,
        AppDbContext database,
        CancellationToken cancellationToken)
    {
        if (request.Minutes < 0 || request.Minutes > VenueDecisions.MaxGraceMinutes)
        {
            return ApiProblem.Of(StatusCodes.Status400BadRequest, VenueErrorCodes.InvalidGrace);
        }

        await database.Venues
            .Where(venue => venue.Id == venueId)
            .ExecuteUpdateAsync(
                set => set.SetProperty(venue => venue.GraceMinutes, request.Minutes),
                cancellationToken);

        return TypedResults.NoContent();
    }

    /// <summary>
    /// How much of a booking's price has to arrive before this venue holds the hours (PRD US-28).
    /// Asking for less than the whole makes the rest something the desk collects, which is a
    /// venue's own call: a court that can chase a no-show over the counter carries less risk than
    /// one that cannot.
    /// </summary>
    private static async Task<Results<NoContent, ProblemHttpResult>> SetDepositAsync(
        Guid venueId,
        DepositRequest request,
        AppDbContext database,
        CancellationToken cancellationToken)
    {
        if (!Deposit.IsAShare(request.Percent))
        {
            return ApiProblem.Of(StatusCodes.Status400BadRequest, VenueErrorCodes.InvalidDeposit);
        }

        await database.Venues
            .Where(venue => venue.Id == venueId)
            .ExecuteUpdateAsync(
                set => set.SetProperty(venue => venue.DepositPercent, request.Percent),
                cancellationToken);

        return TypedResults.NoContent();
    }

    /// <summary>
    /// When this venue asks somebody for more than its usual share (PRD US-28). The whole rule can
    /// be turned off: a venue that would rather not keep a count of who let it down is entitled to
    /// not keep one.
    /// </summary>
    private static async Task<Results<NoContent, ProblemHttpResult>> SetRiskRuleAsync(
        Guid venueId,
        RiskRuleRequest request,
        AppDbContext database,
        CancellationToken cancellationToken)
    {
        if (!DepositRisk.AreThresholds(request.LookbackDays, request.HalfAt, request.FullAt))
        {
            return ApiProblem.Of(StatusCodes.Status400BadRequest, VenueErrorCodes.InvalidRiskRule);
        }

        if (!VenueRiskRule.IsAWindow(request.PeakFromHour, request.PeakUntilHour))
        {
            return ApiProblem.Of(StatusCodes.Status400BadRequest, VenueErrorCodes.InvalidPeakHours);
        }

        await database.Venues
            .Where(venue => venue.Id == venueId)
            .ExecuteUpdateAsync(
                set => set
                    .SetProperty(venue => venue.Risk.On, request.On)
                    .SetProperty(venue => venue.Risk.LookbackDays, request.LookbackDays)
                    .SetProperty(venue => venue.Risk.HalfAt, request.HalfAt)
                    .SetProperty(venue => venue.Risk.FullAt, request.FullAt)
                    .SetProperty(venue => venue.Risk.PeakFromHour, request.PeakFromHour)
                    .SetProperty(venue => venue.Risk.PeakUntilHour, request.PeakUntilHour),
                cancellationToken);

        return TypedResults.NoContent();
    }

    private static async Task<Results<NoContent, ProblemHttpResult, NotFound>> ChangePermissionsAsync(
        Guid venueId,
        Guid userId,
        ChangePermissionsRequest request,
        CurrentVenue currentVenue,
        AppDbContext database,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        if (!VenuePermissionSet.TryParse(request.Permissions, out var permissions))
        {
            return ApiProblem.Of(StatusCodes.Status400BadRequest, VenueErrorCodes.InvalidPermissions);
        }

        if (request.RefundLimitBaht is { } asked && !RefundLimits.IsALimit(asked))
        {
            return ApiProblem.Of(StatusCodes.Status400BadRequest, VenueErrorCodes.InvalidRefundLimit);
        }

        var membership = await database.VenueMemberships
            .SingleOrDefaultAsync(member => member.VenueId == venueId && member.UserId == userId, cancellationToken);

        if (membership is null)
        {
            return TypedResults.NotFound();
        }

        // The owner always holds every permission; changing that would lock the venue out of itself.
        if (membership.Role == VenueRole.Owner)
        {
            return ApiProblem.Of(StatusCodes.Status409Conflict, VenueErrorCodes.OwnerCannotBeChanged);
        }

        // Left out means left alone: a caller that only meant to change permissions must not
        // silently set somebody's limit back to nothing (PRD US-18).
        var limit = request.RefundLimitBaht ?? membership.RefundLimitBaht;

        // Written down with the change, in the same save, only when something changed (PRD 8).
        // A limit is as much a permission as a flag is — it decides what somebody may do with
        // the venue's money — so it goes in the same history and by the same rule.
        if (membership.Permissions != permissions || membership.RefundLimitBaht != limit)
        {
            database.MembershipChanges.Add(new MembershipChange
            {
                VenueId = venueId,
                UserId = userId,
                Kind = MembershipChangeKind.PermissionsChanged,
                Role = membership.Role,
                PermissionsBefore = membership.Permissions,
                PermissionsAfter = permissions,
                RefundLimitBefore = membership.RefundLimitBaht,
                RefundLimitAfter = limit,
                ChangedByUserId = currentVenue.Require().UserId,
                ChangedAt = timeProvider.GetUtcNow(),
            });
            membership.Permissions = permissions;
            membership.RefundLimitBaht = limit;
        }

        await database.SaveChangesAsync(cancellationToken);
        return TypedResults.NoContent();
    }

    private static async Task<Results<NoContent, ProblemHttpResult, NotFound>> RemoveMemberAsync(
        Guid venueId,
        Guid userId,
        CurrentVenue currentVenue,
        AppDbContext database,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var membership = await database.VenueMemberships
            .SingleOrDefaultAsync(member => member.VenueId == venueId && member.UserId == userId, cancellationToken);

        if (membership is null)
        {
            return TypedResults.NotFound();
        }

        if (membership.Role == VenueRole.Owner)
        {
            return ApiProblem.Of(StatusCodes.Status409Conflict, VenueErrorCodes.OwnerCannotBeChanged);
        }

        database.VenueMemberships.Remove(membership);
        database.MembershipChanges.Add(new MembershipChange
        {
            VenueId = venueId,
            UserId = userId,
            Kind = MembershipChangeKind.Removed,
            Role = membership.Role,
            PermissionsBefore = membership.Permissions,
            ChangedByUserId = currentVenue.Require().UserId,
            ChangedAt = timeProvider.GetUtcNow(),
        });
        await database.SaveChangesAsync(cancellationToken);
        return TypedResults.NoContent();
    }

    private static string Normalize(string? email) => email?.Trim().ToUpperInvariant() ?? string.Empty;

    private static string GenerateToken() => WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));

    private static string HashToken(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    /// <summary>
    /// A venue as one of its members sees it: the place, plus what this member may do here and
    /// what they want to hear about. Built from the membership so that nothing about the reader
    /// has to be restated by the caller.
    /// </summary>
    private static VenueResponse ToResponse(Venue venue, VenueMembership membership) =>
        new(
            venue.Id,
            venue.Code,
            venue.Name,
            venue.AddressLine,
            venue.District,
            venue.Province,
            venue.Status.ToString(),
            membership.Role.ToString(),
            VenuePermissionSet.Describe(
                membership.Role == VenueRole.Owner
                    ? VenuePermissions.All
                    : membership.Permissions),
            membership.WantsSlipEmails,
            venue.DepositPercent,
            new RiskRuleResponse(
                venue.Risk.On,
                venue.Risk.LookbackDays,
                venue.Risk.HalfAt,
                venue.Risk.FullAt,
                venue.Risk.PeakFromHour,
                venue.Risk.PeakUntilHour),
            membership.RefundCeiling);

    private static VenueMemberResponse ToResponse(VenueMembership membership) =>
        new(
            membership.UserId,
            membership.User?.Email ?? string.Empty,
            membership.Role.ToString(),
            VenuePermissionSet.Describe(
                membership.Role == VenueRole.Owner ? VenuePermissions.All : membership.Permissions),
            membership.RefundCeiling);

}
