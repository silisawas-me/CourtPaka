using CourtBooking.Api.Bookings;
using CourtBooking.Api.Identity;
using CourtBooking.Api.Venues;

namespace CourtBooking.Api.Tests;

/// <summary>
/// Every email in both languages (PRD US-23): the account's own, the venue's notices, and the
/// platform's word on a venue's standing. The switches have no default arm, so a kind added
/// without words throws here rather than in production.
/// </summary>
public sealed class EmailLanguageTests
{
    private const string Link = "https://courtpaka.test/somewhere?token=abc";

    public static TheoryData<string> Languages => [SupportedLanguages.Thai, SupportedLanguages.English];

    [Theory]
    [MemberData(nameof(Languages))]
    public void The_account_letters_carry_their_link_in_the_reader_s_language(string language)
    {
        foreach (var (subject, body) in new[]
                 {
                     AccountLetters.Verify(language, Link),
                     AccountLetters.Invitation(language, "Test Court", Link),
                     AccountLetters.OwnerInvitation(language, Link),
                 })
        {
            Assert.Contains(Link, body);
            Assert.Equal(language == SupportedLanguages.Thai, IsThai(subject + body));
        }

        Assert.Equal(language == SupportedLanguages.Thai, IsThai(AccountLetters.Locked(language).Body));
        Assert.Equal(language == SupportedLanguages.Thai, IsThai(AccountLetters.AccountExists(language).Body));
    }

    /// <summary>
    /// Every letter a booker can get, in both languages. The switches have no default arm, so a
    /// kind added without words throws — and it throws here rather than at somebody's inbox.
    /// </summary>
    [Theory]
    [MemberData(nameof(Languages))]
    public void Every_booker_letter_has_words_in_both_languages(string language)
    {
        foreach (var kind in Enum.GetValues<BookerNoticeKind>())
        {
            var letter = new BookerLetter(
                kind,
                "CourtPaka Test",
                [new LetterSlot("Court 1", DateTimeOffset.UtcNow.AddDays(1))],
                400m,
                "https://courtpaka.test/bookings",
                DateTimeOffset.UtcNow.AddMinutes(15),
                Reason: "a reason",
                Cause: CancellationReason.VenueInitiated,
                RefundDueBaht: 400m,
                RefundAmountBaht: 400m,
                RefundMethod: RefundMethod.Transfer,
                RefundedOn: DateOnly.FromDateTime(DateTime.UtcNow));

            var (subject, body) = BookerLetters.Write(letter, language);

            Assert.NotEmpty(subject);
            Assert.Contains(letter.Link, body);
            Assert.Equal(language == SupportedLanguages.Thai, IsThai(subject));
        }
    }

    [Theory]
    [MemberData(nameof(Languages))]
    public void Every_venue_notice_has_words_and_the_page_to_act_on(string language)
    {
        foreach (var notice in Enum.GetValues<VenueNotice>())
        {
            var link = VenueLetters.LinkFor("https://courtpaka.test", Guid.Empty);
            var (subject, body) = VenueLetters.Notice(notice, Guid.NewGuid(), link, language);

            Assert.Contains(link, body);
            Assert.Equal(language == SupportedLanguages.Thai, IsThai(subject));
            Assert.NotEmpty(VenueLetters.TemplateOf(notice));
        }
    }

    [Theory]
    [MemberData(nameof(Languages))]
    public void A_venue_is_told_its_standing_with_the_reason_whole(string language)
    {
        var (subject, body) = VenueLetters.Standing(
            "สนาม\nทดสอบ", VenueStatus.Approved, VenueStatus.Suspended, "เหตุผลที่เขียนให้สนามอ่าน", language);

        Assert.Contains("เหตุผลที่เขียนให้สนามอ่าน", body);
        Assert.DoesNotContain('\n', subject);
    }

    /// <summary>A subject is one header line, whatever the venue called itself.</summary>
    [Fact]
    public void An_invitation_subject_is_one_line()
    {
        var (subject, _) = AccountLetters.Invitation(SupportedLanguages.Thai, "สนาม\r\nBcc: x@example.test", Link);

        Assert.DoesNotContain('\n', subject);
        Assert.DoesNotContain('\r', subject);
    }

    private static bool IsThai(string text) => text.Any(character => character is >= '฀' and <= '๿');
}
