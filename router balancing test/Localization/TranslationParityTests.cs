using RouterBalancing.Core.Localization;

namespace router_balancing_test.Localization;

public class TranslationParityTests
{
    [Fact]
    public void EnglishAndVietnamese_HaveIdenticalKeySets()
    {
        var missingInVi = Translations.English.Keys
            .Except(Translations.Vietnamese.Keys).OrderBy(k => k).ToList();
        var missingInEn = Translations.Vietnamese.Keys
            .Except(Translations.English.Keys).OrderBy(k => k).ToList();

        Assert.True(missingInVi.Count == 0 && missingInEn.Count == 0,
            $"Keys missing in VI: [{string.Join(", ", missingInVi)}]; " +
            $"Keys missing in EN: [{string.Join(", ", missingInEn)}]");
    }
}
