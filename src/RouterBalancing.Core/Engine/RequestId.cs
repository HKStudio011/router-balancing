namespace RouterBalancing.Core.Engine;

/// <summary>Sinh id ngắn cho request (spec §3.6).</summary>
public static class RequestId
{
    private const string Alphabet = "abcdefghijklmnopqrstuvwxyz0123456789";

    /// <summary>8 ký tự base36 (a-z, 0-9) ngẫu nhiên — endpoint tự retry nếu va chạm.</summary>
    public static string New()
    {
        Span<char> chars = stackalloc char[8];
        for (var i = 0; i < chars.Length; i++)
            chars[i] = Alphabet[Random.Shared.Next(Alphabet.Length)];
        return new string(chars);
    }
}
