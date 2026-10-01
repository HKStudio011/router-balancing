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
    public void Mask_LegacyKeyOver8Chars_ShowsFirst4AndLast4()
        => Assert.Equal("abcd…mnop", ClientKeyHasher.Mask("abcdefghijklmnop"));

    [Fact]
    public void Mask_ShortKey_ShowsWholeAfterEllipsis()
        => Assert.Equal("…short", ClientKeyHasher.Mask("short"));
}
