using System.Globalization;
using System.Text;

namespace CourtBooking.Api.Venues;

/// <summary>
/// The string a Thai bank app reads out of a PromptPay QR (PRD US-04).
///
/// It is EMVCo's tag-length-value format with the Thai scheme's own fields inside it: every
/// element is a two-digit tag, a two-digit length, and that many characters. The whole thing ends
/// with a CRC over everything before it, including the CRC's own tag and length — which is the
/// part that is easy to get wrong and impossible to notice, because a QR with a bad checksum
/// scans perfectly well and is simply refused by the bank.
///
/// Built here rather than in the browser because it is a rule about money: the amount in the QR
/// has to be the amount the booking says, and the account has to be the one the venue registered
/// (PRD US-10). The page only turns the string into squares.
/// </summary>
public static class PromptPay
{
    private const string PayloadFormat = "00";
    private const string PointOfInitiation = "01";
    private const string MerchantAccount = "29";
    private const string CurrencyTag = "53";
    private const string AmountTag = "54";
    private const string CountryTag = "58";
    private const string CrcTag = "63";

    /// <summary>The Thai scheme's identifier, which names PromptPay inside the merchant field.</summary>
    private const string ThaiQrAid = "A000000677010111";

    /// <summary>Thai baht, and Thailand, in the numbers the standard uses.</summary>
    private const string ThaiBaht = "764";

    private const string Thailand = "TH";

    /// <summary>
    /// "Use this once": the payload carries an amount, so it is not a poster on a wall.
    /// </summary>
    private const string DynamicQr = "12";

    /// <summary>How a proxy is named inside the merchant field, by what kind of thing it is.</summary>
    private enum Proxy
    {
        Phone,
        NationalId,
        EWallet,
    }

    /// <summary>
    /// The payload for one payment, or null when the account is not something PromptPay accepts.
    /// A venue with an unusable account is a venue that cannot be paid, and saying so is better
    /// than drawing a QR that every bank app refuses.
    /// </summary>
    public static string? For(string? promptPayId, decimal amountBaht)
    {
        if (amountBaht <= 0)
        {
            return null;
        }

        // Not `is not var (...)`: a var pattern matches anything, including null, so that spelling
        // is a guard that never fires. This mistake has been made in this repo before.
        if (Read(promptPayId) is not { } account)
        {
            return null;
        }

        var merchant = Element("00", ThaiQrAid)
                       + Element(TagFor(account.Proxy), account.Value);

        var payload = new StringBuilder()
            .Append(Element(PayloadFormat, "01"))
            .Append(Element(PointOfInitiation, DynamicQr))
            .Append(Element(MerchantAccount, merchant))
            .Append(Element(CurrencyTag, ThaiBaht))
            .Append(Element(AmountTag, amountBaht.ToString("0.00", CultureInfo.InvariantCulture)))
            .Append(Element(CountryTag, Thailand))
            .ToString();

        // The checksum covers its own tag and length as well, so they go on before it is taken.
        var withCrcHeader = payload + CrcTag + "04";
        return withCrcHeader + Crc16(withCrcHeader);
    }

    /// <summary>
    /// What kind of account this is, by its shape. A Thai mobile number is ten digits starting
    /// with a zero, a tax or citizen number is thirteen, and an e-wallet identifier is fifteen.
    /// Anything else is not something a bank app will accept.
    /// </summary>
    private static (Proxy Proxy, string Value)? Read(string? promptPayId)
    {
        var digits = new string((promptPayId ?? string.Empty).Where(char.IsAsciiDigit).ToArray());

        return digits.Length switch
        {
            10 when digits[0] == '0' =>
                // Written as a country code and the number without its leading zero, padded to
                // thirteen — which is how every bank app expects to read a phone number back.
                (Proxy.Phone, ("0000" + "66" + digits[1..])[^13..]),
            13 => (Proxy.NationalId, digits),
            15 => (Proxy.EWallet, digits),
            _ => null,
        };
    }

    private static string TagFor(Proxy proxy) => proxy switch
    {
        Proxy.Phone => "01",
        Proxy.NationalId => "02",
        _ => "03",
    };

    /// <summary>One element: its tag, how long its value is, and the value.</summary>
    private static string Element(string tag, string value) =>
        $"{tag}{value.Length:00}{value}";

    /// <summary>
    /// CRC-16/CCITT-FALSE over the ASCII of everything so far, as four uppercase hex digits.
    /// </summary>
    private static string Crc16(string payload)
    {
        ushort crc = 0xFFFF;

        foreach (var character in Encoding.ASCII.GetBytes(payload))
        {
            crc ^= (ushort)(character << 8);

            for (var bit = 0; bit < 8; bit++)
            {
                crc = (crc & 0x8000) != 0
                    ? (ushort)((crc << 1) ^ 0x1021)
                    : (ushort)(crc << 1);
            }
        }

        return crc.ToString("X4", CultureInfo.InvariantCulture);
    }
}
