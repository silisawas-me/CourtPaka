using System.Globalization;
using System.Text;
using CourtBooking.Api.Identity;
using CourtBooking.Api.Localization;

namespace CourtBooking.Api.Bookings;

/// <summary>One court-hour as a booker reads it: which court, and when it starts.</summary>
public sealed record LetterSlot(string Court, DateTimeOffset StartsAt);

/// <summary>
/// Everything a message to a booker can say (PRD US-06). Filled from the booking as it stands
/// when the message goes out; which fields matter depends on <see cref="Kind"/>.
/// </summary>
public sealed record BookerLetter(
    BookerNoticeKind Kind,
    string VenueName,
    IReadOnlyList<LetterSlot> Slots,
    decimal TotalBaht,
    string Link,
    DateTimeOffset HoldExpiresAt,
    string? Reason = null,
    CancellationReason? Cause = null,
    bool CancelledByBooker = false,
    decimal RefundDueBaht = 0,
    bool PaymentUnconfirmed = false,
    decimal RefundAmountBaht = 0,
    RefundMethod? RefundMethod = null,
    DateOnly? RefundedOn = null,
    decimal StillOwedBaht = 0);

/// <summary>
/// The words of every message a booker gets, in the language they chose (PRD US-06, US-23).
///
/// Written out in full for each language rather than assembled from fragments: Thai and English
/// do not put the same things in the same order, and a sentence built from parts reads like one.
/// Each language is one switch with no default arm, so a kind added later stops here instead of
/// going out in another kind's words.
///
/// Dates and hours are the venue's (Bangkok), because that is where the court is. Thai dates carry
/// the Buddhist year, the way the screen shows them.
/// </summary>
public static class BookerLetters
{
    private static readonly CultureInfo Thai = CultureInfo.GetCultureInfo("th-TH");
    private static readonly CultureInfo English = CultureInfo.GetCultureInfo("en-GB");

    public static (string Subject, string Body) Write(BookerLetter letter, string language) =>
        language == SupportedLanguages.English ? InEnglish(letter) : InThai(letter);

    private static (string Subject, string Body) InThai(BookerLetter letter)
    {
        var when = Hours(letter.Slots, Thai);
        var venue = letter.VenueName;

        return letter.Kind switch
        {
            BookerNoticeKind.Held => (
                $"รอชำระเงิน: {venue}",
                Lines(
                    $"เราจองคอร์ทที่ {venue} ไว้ให้แล้ว",
                    when,
                    $"ยอดชำระ {Baht(letter.TotalBaht, Thai)} บาท",
                    $"กรุณาโอนเงินและส่งสลิปภายใน {Clock(letter.HoldExpiresAt)} น. "
                    + "ถ้าเลยเวลานี้ ช่วงเวลาที่จองจะถูกปล่อยให้คนอื่นจอง",
                    letter.Link)),

            BookerNoticeKind.Confirmed => (
                $"ยืนยันการจองแล้ว: {venue}",
                Lines(
                    $"{venue} ยืนยันการชำระเงินแล้ว การจองของคุณเรียบร้อย",
                    when,
                    $"ยอดที่ชำระ {Baht(letter.TotalBaht, Thai)} บาท",
                    letter.Link)),

            BookerNoticeKind.Rejected => (
                $"สลิปไม่ผ่านการตรวจ: {venue}",
                Lines(
                    $"{venue} ตรวจสลิปแล้วไม่สามารถยืนยันการชำระเงินได้ และการจองนี้ถูกยกเลิก",
                    when,
                    $"เหตุผลจากสนาม: {letter.Reason}",
                    RefundInThai(letter),
                    "หากมีข้อสงสัยกรุณาติดต่อสนามโดยตรง",
                    letter.Link)),

            BookerNoticeKind.Expired => (
                $"หมดเวลาชำระเงิน: {venue}",
                Lines(
                    $"การจองที่ {venue} หมดเวลาชำระเงินก่อนได้รับสลิป ช่วงเวลานี้จึงถูกปล่อยแล้ว",
                    when,
                    "ถ้ายังต้องการเล่น จองใหม่ได้ถ้าช่วงเวลายังว่างอยู่",
                    letter.Link)),

            BookerNoticeKind.Cancelled => (
                $"การจองถูกยกเลิก: {venue}",
                Lines(
                    letter.CancelledByBooker
                        ? $"คุณยกเลิกการจองที่ {venue} แล้ว"
                        : $"{venue} ยกเลิกการจองของคุณ",
                    when,
                    CauseInThai(letter),
                    RefundInThai(letter),
                    letter.Link)),

            BookerNoticeKind.RefundRecorded => (
                $"สนามบันทึกการคืนเงินแล้ว: {venue}",
                Lines(
                    $"{venue} บันทึกว่าได้คืนเงิน {Baht(letter.RefundAmountBaht, Thai)} บาท "
                    + $"{MethodInThai(letter.RefundMethod)} "
                    + $"เมื่อวันที่ {letter.RefundedOn?.ToString("d MMMM yyyy", Thai)}",
                    when,
                    letter.StillOwedBaht > 0
                        ? $"ยอดที่ยังค้างคืน {Baht(letter.StillOwedBaht, Thai)} บาท"
                        : "คืนเงินครบแล้ว",
                    "ถ้ายังไม่ได้รับเงิน กรุณาติดต่อสนามโดยตรง",
                    letter.Link)),

            BookerNoticeKind.AboutToPlay => (
                $"อีกไม่ถึง 2 ชั่วโมงได้เวลาเล่น: {venue}",
                Lines(
                    $"เตือนความจำ: คุณมีคอร์ทที่ {venue}",
                    when,
                    letter.Link)),

            _ => throw new ArgumentOutOfRangeException(nameof(letter), letter.Kind, null),
        };
    }

    private static (string Subject, string Body) InEnglish(BookerLetter letter)
    {
        var when = Hours(letter.Slots, English);
        var venue = letter.VenueName;

        return letter.Kind switch
        {
            BookerNoticeKind.Held => (
                $"Waiting for payment: {venue}",
                Lines(
                    $"We are holding these hours for you at {venue}.",
                    when,
                    $"Amount to pay: THB {Baht(letter.TotalBaht, English)}",
                    $"Please transfer and send the slip by {Clock(letter.HoldExpiresAt)}. "
                    + "After that the hours are released for somebody else to book.",
                    letter.Link)),

            BookerNoticeKind.Confirmed => (
                $"Booking confirmed: {venue}",
                Lines(
                    $"{venue} has confirmed your payment. Your booking is all set.",
                    when,
                    $"Paid: THB {Baht(letter.TotalBaht, English)}",
                    letter.Link)),

            BookerNoticeKind.Rejected => (
                $"Your slip was not accepted: {venue}",
                Lines(
                    $"{venue} checked your slip and could not confirm the payment, so this "
                    + "booking has been cancelled.",
                    when,
                    $"The venue's reason: {letter.Reason}",
                    RefundInEnglish(letter),
                    "If anything is unclear, please contact the venue directly.",
                    letter.Link)),

            BookerNoticeKind.Expired => (
                $"Payment time ran out: {venue}",
                Lines(
                    $"Your booking at {venue} ran out of time before a slip arrived, so the hours "
                    + "have been released.",
                    when,
                    "If you still want to play, you can book again while the hours are free.",
                    letter.Link)),

            BookerNoticeKind.Cancelled => (
                $"Booking cancelled: {venue}",
                Lines(
                    letter.CancelledByBooker
                        ? $"You cancelled your booking at {venue}."
                        : $"{venue} cancelled your booking.",
                    when,
                    CauseInEnglish(letter),
                    RefundInEnglish(letter),
                    letter.Link)),

            BookerNoticeKind.RefundRecorded => (
                $"The venue recorded a refund: {venue}",
                Lines(
                    $"{venue} recorded refunding THB {Baht(letter.RefundAmountBaht, English)} "
                    + $"{MethodInEnglish(letter.RefundMethod)} "
                    + $"on {letter.RefundedOn?.ToString("d MMMM yyyy", English)}.",
                    when,
                    letter.StillOwedBaht > 0
                        ? $"Still to be refunded: THB {Baht(letter.StillOwedBaht, English)}"
                        : "Everything owed has now been refunded.",
                    "If the money has not reached you, please contact the venue directly.",
                    letter.Link)),

            BookerNoticeKind.AboutToPlay => (
                $"Playing in under 2 hours: {venue}",
                Lines(
                    $"A reminder that you have a court at {venue}.",
                    when,
                    letter.Link)),

            _ => throw new ArgumentOutOfRangeException(nameof(letter), letter.Kind, null),
        };
    }

    /// <summary>
    /// Why the venue cancelled, from the closed set, and in the venue's own words if it wrote
    /// any. A booker who cancelled needs no reason read back to them.
    /// </summary>
    private static string? CauseInThai(BookerLetter letter)
    {
        if (letter.CancelledByBooker)
        {
            return null;
        }

        var cause = letter.Cause switch
        {
            CancellationReason.CustomerRequest => "ยกเลิกตามคำขอของคุณ",
            CancellationReason.VenueInitiated => "สนามไม่สามารถให้บริการตามที่จองได้",
            CancellationReason.PaymentNotReceived => "สนามไม่ได้รับเงินค่าจอง",
            _ => null,
        };

        return Joined("เหตุผล: ", cause, letter.Reason);
    }

    private static string? CauseInEnglish(BookerLetter letter)
    {
        if (letter.CancelledByBooker)
        {
            return null;
        }

        var cause = letter.Cause switch
        {
            CancellationReason.CustomerRequest => "Cancelled at your request.",
            CancellationReason.VenueInitiated => "The venue could not provide the booking.",
            CancellationReason.PaymentNotReceived => "The venue did not receive the payment.",
            _ => null,
        };

        return Joined("Reason: ", cause, letter.Reason);
    }

    /// <summary>
    /// What comes back, as the booking says now — the same number the booker's history shows
    /// (PRD 6.2). A slip the venue never got round to checking leaves the amount unknown
    /// until it says whether the money arrived, and the booker is told exactly that rather than
    /// a number that may be wrong (PRD 6.2).
    /// </summary>
    private static string RefundInThai(BookerLetter letter) =>
        letter.PaymentUnconfirmed
            ? "สนามยังไม่ได้ยืนยันว่าได้รับเงินหรือไม่ เมื่อสนามยืนยันแล้ว "
              + "ถ้ามียอดที่ต้องคืน สนามจะคืนให้และระบบจะแจ้งคุณอีกครั้ง"
            : letter.RefundDueBaht > 0
                ? $"ยอดที่สนามต้องคืนให้คุณ {Baht(letter.RefundDueBaht, Thai)} บาท "
                  + "สนามจะโอนคืนเองและระบบจะแจ้งคุณเมื่อบันทึกการคืนเงิน"
                : "การจองนี้ไม่มียอดที่ต้องคืน";

    private static string RefundInEnglish(BookerLetter letter) =>
        letter.PaymentUnconfirmed
            ? "The venue has not yet confirmed whether your payment arrived. Once it does, "
              + "anything owed is refunded by the venue and you will be told again."
            : letter.RefundDueBaht > 0
                ? $"The venue owes you THB {Baht(letter.RefundDueBaht, English)}. The venue "
                  + "sends it itself, and you will be told when it records the refund."
                : "Nothing is owed back on this booking.";

    private static string MethodInThai(RefundMethod? method) => method switch
    {
        RefundMethod.Cash => "เป็นเงินสด",
        _ => "ด้วยการโอน",
    };

    private static string MethodInEnglish(RefundMethod? method) => method switch
    {
        RefundMethod.Cash => "in cash",
        _ => "by transfer",
    };

    /// <summary>The hours, one per line, under the day they are on.</summary>
    private static string Hours(IReadOnlyList<LetterSlot> slots, CultureInfo culture)
    {
        var text = new StringBuilder();

        foreach (var day in slots
                     .Select(slot => (slot.Court, At: Local(slot.StartsAt)))
                     .OrderBy(slot => slot.At)
                     .GroupBy(slot => DateOnly.FromDateTime(slot.At)))
        {
            if (text.Length > 0)
            {
                text.Append('\n');
            }

            // "วันอังคารที่ 22 กันยายน 2569" in Thai, "Tuesday 22 September 2026" in English.
            text.Append(day.Key.ToString(
                culture.Name == Thai.Name ? "dddd'ที่' d MMMM yyyy" : "dddd d MMMM yyyy", culture));

            foreach (var (court, at) in day)
            {
                text.Append($"\n  {court} · {at:HH:00}–{at.AddHours(1):HH:00}");
            }
        }

        return text.ToString();
    }

    private static DateTime Local(DateTimeOffset at) =>
        TimeZoneInfo.ConvertTime(at, PlatformRequirements.BangkokTimeZone).DateTime;

    private static string Clock(DateTimeOffset at) => Local(at).ToString("HH:mm", CultureInfo.InvariantCulture);

    private static string Baht(decimal amount, CultureInfo culture) =>
        amount.ToString("#,0.##", culture);

    private static string? Joined(string label, string? first, string? second)
    {
        var parts = new[] { first, second }.Where(part => !string.IsNullOrWhiteSpace(part)).ToList();
        return parts.Count == 0 ? null : label + string.Join(" ", parts);
    }

    /// <summary>Paragraphs, skipping the ones that have nothing to say.</summary>
    private static string Lines(params string?[] paragraphs) =>
        string.Join("\n\n", paragraphs.Where(paragraph => !string.IsNullOrWhiteSpace(paragraph)));
}
