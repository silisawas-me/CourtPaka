namespace CourtBooking.Api.Identity;

/// <summary>
/// The account's own messages — verifying an address, an address already taken, a lockout, and
/// an invitation to a venue — in the recipient's language (PRD US-01, US-14, US-23).
///
/// Each is written out in full per language, like the booker's letters (Bookings/BookerLetters):
/// a sentence assembled from parts reads like one. The link is on a line of its own, so a mail
/// client makes it pressable and nothing around it is mistaken for part of it.
/// </summary>
public static class AccountLetters
{
    public const string VerifyTemplate = "auth.verify";
    public const string AccountExistsTemplate = "auth.account_exists";
    public const string LockedTemplate = "auth.locked";
    public const string InvitationTemplate = "venue.invitation";

    public static (string Subject, string Body) Verify(string language, string link) =>
        Thai(language)
            ? ("badPaka: ยืนยันอีเมลของคุณ",
                $"กดลิงก์นี้เพื่อยืนยันอีเมลและสมัครให้เสร็จ\n\n{link}\n\n"
                + "ถ้าคุณไม่ได้สมัคร badPaka ไม่ต้องทำอะไร")
            : ("badPaka: verify your email",
                $"Confirm your address to finish signing up:\n\n{link}\n\n"
                + "If you did not sign up for badPaka, you can ignore this.");

    public static (string Subject, string Body) AccountExists(string language) =>
        Thai(language)
            ? ("badPaka: มีบัญชีที่ใช้อีเมลนี้อยู่แล้ว",
                "มีคนพยายามสมัครด้วยอีเมลนี้ ถ้าเป็นคุณ เข้าสู่ระบบด้วยบัญชีเดิมได้เลย "
                + "ถ้าไม่ใช่คุณ ไม่ต้องทำอะไร บัญชีของคุณยังปลอดภัย")
            : ("badPaka: account already exists",
                "Someone tried to register with this address. If it was you, sign in with the "
                + "account you already have. If it was not, you do not need to do anything.");

    public static (string Subject, string Body) Locked(string language) =>
        Thai(language)
            ? ("badPaka: บัญชีของคุณถูกล็อกชั่วคราว",
                "มีการใส่รหัสผ่านผิดหลายครั้ง บัญชีจึงถูกล็อกไว้ 15 นาที "
                + "ถ้าไม่ใช่คุณ ควรเปลี่ยนรหัสผ่านเมื่อปลดล็อกแล้ว")
            : ("badPaka: your account is temporarily locked",
                "Too many failed sign-in attempts locked your account for 15 minutes. "
                + "If this was not you, change your password once it unlocks.");

    public static (string Subject, string Body) Invitation(string language, string venueName, string link) =>
        InvitationIn(language, OneLine(venueName), link);

    private static (string Subject, string Body) InvitationIn(string language, string venueName, string link) =>
        Thai(language)
            ? ($"badPaka: คุณได้รับคำเชิญเข้าร่วม {venueName}",
                $"{venueName} เชิญคุณเข้าร่วมเป็นทีมงานของสนาม กดลิงก์นี้เพื่อตอบรับภายใน 7 วัน\n\n"
                + $"{link}\n\nต้องเข้าสู่ระบบด้วยอีเมลนี้ก่อนตอบรับ")
            : ($"badPaka: you were invited to {venueName}",
                $"{venueName} has invited you to join its staff. Accept within 7 days:\n\n"
                + $"{link}\n\nSign in with this address before accepting.");

    private static bool Thai(string language) => language != SupportedLanguages.English;

    /// <summary>The venue writes its own name, and a subject is one header line.</summary>
    private static string OneLine(string text) =>
        string.Join(' ', text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries));
}
