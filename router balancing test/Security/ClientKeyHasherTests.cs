using RouterBalancing.Core.Security;

namespace router_balancing_test.Security;

public class ClientKeyHasherTests
{
    [Fact]
    public void Hash_KnownVector_ReturnsSha256HexLower()
        => Assert.Equal(
            "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad",
            ClientKeyHasher.Hash("abc"));

    [Fact]
    public void Hash_SameInput_ReturnsSameHex()
        => Assert.Equal(ClientKeyHasher.Hash("key-1"), ClientKeyHasher.Hash("key-1"));

    [Fact]
    public void GeneratePlaintext_HasPrefixAnd43Base64UrlChars()
    {
        var key = ClientKeyHasher.GeneratePlaintext();
        Assert.Matches("^sk-rb-[A-Za-z0-9_-]{43}$", key);
    }

    [Fact]
    public void GeneratePlaintext_TwoCalls_Differ()
        => Assert.NotEqual(ClientKeyHasher.GeneratePlaintext(), ClientKeyHasher.GeneratePlaintext());

    [Fact]
    public void Mask_GeneratedKey_ShowsPrefixAndLast4()
    {
        var key = ClientKeyHasher.GeneratePlaintext();
        Assert.Equal($"sk-rb-…{key[^4..]}", ClientKeyHasher.Mask(key));
    }

    [Fact]
    public void Mask_LegacyKey_ShowsOnlyLast4()
        => Assert.Equal("…mnop", ClientKeyHasher.Mask("abcdefghijklmnop"));

    [Fact]
    public void Mask_ShortLegacyKey_HidesEverythingButLast4()
    {
        // 5 ký tự → vẫn chỉ 4 cuối; ≤4 ký tự → mask cố định, không lộ ký tự secret nào (spec §2)
        Assert.Equal("…hort", ClientKeyHasher.Mask("short"));
        Assert.Equal("…", ClientKeyHasher.Mask("abcd"));
        Assert.Equal("…", ClientKeyHasher.Mask("a"));
    }
}
