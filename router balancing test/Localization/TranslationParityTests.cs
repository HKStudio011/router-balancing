using System.Globalization;
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

    [Fact]
    public void DatasetStats_BothLanguages_FormatAllPayloadCounters()
    {
        object[] sampleValues = [1, 2, 3, 4, 5, 6, 7, 8, 12, 34, 56, 78.9];

        foreach (var language in new[] { "en", "vi" })
        {
            var formattedStats = string.Format(
                CultureInfo.InvariantCulture,
                Translations.For(language)["settings.status.datasetStats"],
                sampleValues);

            // Arguments {8}..{11} are 12, 34, 56 and 78.9 respectively.
            Assert.Contains("12", formattedStats);   // large records
            Assert.Contains("34", formattedStats);   // payload manifests
            Assert.Contains("56", formattedStats);   // externalized payload files
            Assert.Contains("78.9", formattedStats); // externalized MiB
        }
    }
}
