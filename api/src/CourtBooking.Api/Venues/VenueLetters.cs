using CourtBooking.Api.Identity;

namespace CourtBooking.Api.Venues;

/// <summary>What a venue is told about (PRD US-17). Everything else about a notice follows from this.</summary>
public enum VenueNotice
{
    SlipWaiting,
    SlipSeenBefore,
    SlipStillWaiting,
    RefundOwed,
    PaymentUnanswered,
}

/// <summary>
/// The words of every message a venue gets, in each reader's language (PRD US-17, US-20, US-23).
///
/// Written out in full per language, like the booker's letters. Each ends with the page where the
/// work is done, so the message is a door and not only news. One switch per language with no
/// default arm: a notice added later stops here rather than going out in another one's words.
/// </summary>
public static class VenueLetters
{
    public static string TemplateOf(VenueNotice notice) => notice switch
    {
        VenueNotice.SlipWaiting => "venue.slip_waiting",
        VenueNotice.SlipSeenBefore => "venue.slip_seen_before",
        VenueNotice.SlipStillWaiting => "venue.slip_still_waiting",
        VenueNotice.RefundOwed => "venue.refund_owed",
        VenueNotice.PaymentUnanswered => "venue.payment_unanswered",
        _ => throw new ArgumentOutOfRangeException(nameof(notice), notice, null),
    };

    /// <summary>
    /// The page every notice is dealt with on: the court schedule — the app is its
    /// four sections now, and the slip queue's own page is gone with the rest of "อื่น ๆ".
    /// </summary>
    public static string LinkFor(string baseUrl, Guid venueId) =>
        $"{baseUrl.TrimEnd('/')}/venues/{venueId}/timeline";

    public static (string Subject, string Body) Notice(
        VenueNotice notice, Guid bookingId, string link, string language) =>
        language == SupportedLanguages.English
            ? NoticeInEnglish(notice, bookingId, link)
            : NoticeInThai(notice, bookingId, link);

    private static (string Subject, string Body) NoticeInThai(VenueNotice notice, Guid bookingId, string link) =>
        notice switch
        {
            VenueNotice.SlipWaiting => (
                "มีสลิปรอตรวจ",
                $"ผู้จองส่งสลิปของการจอง {bookingId} แล้ว สลิปอยู่ในคิวตรวจสลิป\n\n{link}"),

            VenueNotice.SlipSeenBefore => (
                "มีสลิปที่เคยเห็นแล้วถูกส่งมาอีก",
                $"สลิปของการจอง {bookingId} เป็นไฟล์เดียวกับสลิปที่สนามเคยได้รับมาก่อน "
                + $"กรุณาตรวจกับยอดโอนจริง\n\n{link}"),

            VenueNotice.SlipStillWaiting => (
                "สลิปยังรอตรวจ และใกล้ถึงเวลาเล่นแล้ว",
                $"การจอง {bookingId} ยังรอตรวจสลิป และชั่วโมงแรกจะเริ่มภายในครึ่งชั่วโมง "
                + $"กรุณาตรวจก่อนผู้เล่นมาถึง\n\n{link}"),

            VenueNotice.RefundOwed => (
                "มีการจองที่ต้องคืนเงิน",
                $"การจอง {bookingId} มียอดที่ต้องคืนให้ผู้จอง สนามโอนคืนเองแล้วบันทึกไว้ในระบบ\n\n{link}"),

            VenueNotice.PaymentUnanswered => (
                "มีการจองที่รอสนามยืนยันการรับเงิน",
                $"การจอง {bookingId} ถูกยกเลิกขณะสลิปยังรอตรวจ จนกว่าสนามจะแจ้งว่าได้รับเงินหรือไม่ "
                + $"จะยังไม่รู้ว่าต้องคืนเท่าไร\n\n{link}"),

            _ => throw new ArgumentOutOfRangeException(nameof(notice), notice, null),
        };

    private static (string Subject, string Body) NoticeInEnglish(VenueNotice notice, Guid bookingId, string link) =>
        notice switch
        {
            VenueNotice.SlipWaiting => (
                "A slip is waiting to be checked",
                $"A booker has sent a slip for booking {bookingId}. It is in the slip queue.\n\n{link}"),

            VenueNotice.SlipSeenBefore => (
                "A slip you have seen before has arrived again",
                $"The slip sent for booking {bookingId} has the same bytes as one this venue has "
                + $"been sent before. Check it against the transfer.\n\n{link}"),

            VenueNotice.SlipStillWaiting => (
                "A slip is still waiting, and the court is about to be played",
                $"Booking {bookingId} is still waiting for its slip to be checked, and its first "
                + $"hour starts within the half hour. Check it before the player arrives.\n\n{link}"),

            VenueNotice.RefundOwed => (
                "A booking is owed money back",
                $"Booking {bookingId} is owed money back. The venue sends it and records that "
                + $"it did.\n\n{link}"),

            VenueNotice.PaymentUnanswered => (
                "A booking needs an answer about its payment",
                $"Booking {bookingId} was given up while its slip was still being checked. Until "
                + $"the venue says whether the money arrived, nobody knows what is owed.\n\n{link}"),

            _ => throw new ArgumentOutOfRangeException(nameof(notice), notice, null),
        };

    public const string StandingTemplate = "venue.standing";

    /// <summary>
    /// A venue told where it stands with the platform (PRD US-20). The reason is theirs to read,
    /// so it goes in whole.
    /// </summary>
    public static (string Subject, string Body) Standing(
        string venueName, VenueStatus from, VenueStatus to, string? reason, string language)
    {
        venueName = string.Join(' ', venueName.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries));
        return language == SupportedLanguages.English
            ? StandingInEnglish(venueName, from, to, reason)
            : StandingInThai(venueName, from, to, reason);
    }

    private static (string Subject, string Body) StandingInThai(
        string venueName, VenueStatus from, VenueStatus to, string? reason) =>
        (from, to) switch
        {
            (VenueStatus.Suspended, VenueStatus.Approved) => (
                $"{venueName} เปิดรับการจองได้อีกครั้ง",
                "ยกเลิกการระงับสนามนี้แล้ว ผู้จองค้นหาและจองสนามได้ตั้งแต่ตอนนี้"),

            (_, VenueStatus.Approved) => (
                $"{venueName} ได้รับการอนุมัติแล้ว",
                "สนามนี้อยู่บน badPaka แล้ว ผู้จองค้นหาและจองได้ "
                + "กรุณาตรวจคอร์ต เวลาเปิด-ปิด และราคา ก่อนการจองแรกจะเข้ามา"),

            (_, VenueStatus.Rejected) => (
                $"{venueName} ยังไม่ได้รับการอนุมัติ",
                $"สนามนี้ยังไม่ได้รับการอนุมัติ เหตุผล: {reason}\n\n"
                + "แก้ไขข้อมูลแล้วส่งให้ตรวจใหม่ได้"),

            (_, VenueStatus.Suspended) => (
                $"{venueName} ถูกระงับ",
                $"สนามนี้ถูกระงับ เหตุผล: {reason}\n\n"
                + "ผู้จองจะค้นหาสนามไม่เจอและจองใหม่ไม่ได้ การจองที่ยืนยันแล้วยังอยู่ "
                + "สนามต้องให้บริการหรือยกเลิกพร้อมเหตุผล และยังตรวจสลิปที่ส่งมาแล้วได้"),

            _ => (
                $"สถานะของ {venueName} เปลี่ยนเป็น {to}",
                $"สถานะของสนามเปลี่ยนจาก {from} เป็น {to}"),
        };

    private static (string Subject, string Body) StandingInEnglish(
        string venueName, VenueStatus from, VenueStatus to, string? reason) =>
        (from, to) switch
        {
            (VenueStatus.Suspended, VenueStatus.Approved) => (
                $"{venueName} is open for bookings again",
                "The suspension on this venue has been lifted. Bookers can find it and book it "
                + "again from now."),

            (_, VenueStatus.Approved) => (
                $"{venueName} has been approved",
                "This venue is now on the platform. Bookers can find it and book it. Check the "
                + "courts, opening hours and prices before the first booking arrives."),

            (_, VenueStatus.Rejected) => (
                $"{venueName} was not approved",
                $"This venue was not approved. The reason given: {reason}\n\n"
                + "The details can be corrected and the venue submitted again."),

            (_, VenueStatus.Suspended) => (
                $"{venueName} has been suspended",
                $"This venue has been suspended. The reason given: {reason}\n\n"
                + "Bookers can no longer find it and no new booking can be made. Bookings "
                + "already confirmed still stand: they have to be honoured, or cancelled with a "
                + "reason. Slips already sent can still be checked."),

            _ => (
                $"{venueName} has moved to {to}",
                $"This venue moved from {from} to {to}."),
        };
}
