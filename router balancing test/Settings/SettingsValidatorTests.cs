using RouterBalancing.Core.Settings;
using RouterBalancing.Core.Tools;

namespace router_balancing_test.Settings;

public class SettingsValidatorTests
{
    private static SettingsDraft ValidDraft() => new()
    {
        Language = "auto",
        Theme = "system",
        Port = 8317,
        ProviderProbeTimeoutSec = 60,
        LogRetentionDays = 90,
        StatsErrorRateThreshold = 10,
        TransientMaxRetries = 5,
        TransientBackoffBaseMs = 1000,
        UpstreamIdleTimeoutSec = 300,
        OpenCodeConfigPath = OpenCodeConfigPaths.DefaultPath,
    };

    [Fact]
    public void Validate_WhenDraftValid_ReturnsEmpty()
    {
        var errors = SettingsValidator.Validate(ValidDraft());

        Assert.Empty(errors);
    }

    [Fact]
    public void Validate_WhenOpenCodeConfigPathEmpty_ReturnsError()
    {
        var draft = ValidDraft() with { OpenCodeConfigPath = "" };

        var errors = SettingsValidator.Validate(draft);

        Assert.Equal("settings.error.openCodeConfigPath", errors[nameof(SettingsDraft.OpenCodeConfigPath)]);
    }

    [Fact]
    public void Validate_WhenOpenCodeConfigPathValid_ReturnsNoError()
    {
        var errors = SettingsValidator.Validate(ValidDraft());

        Assert.False(errors.ContainsKey(nameof(SettingsDraft.OpenCodeConfigPath)));
    }

    [Fact]
    public void Validate_WhenPortOutOfRange_ReturnsPortError()
    {
        var low = SettingsValidator.Validate(ValidDraft() with { Port = 80 });
        var high = SettingsValidator.Validate(ValidDraft() with { Port = 70000 });

        Assert.Equal("settings.error.port", low[nameof(SettingsDraft.Port)]);
        Assert.Equal("settings.error.port", high[nameof(SettingsDraft.Port)]);
    }

    [Theory]
    [InlineData("fr")]
    [InlineData("ja")]
    [InlineData("zh-Hans")]
    public void Validate_WhenLanguageIsIsoCode_ReturnsNoLanguageError(string language)
    {
        var errors = SettingsValidator.Validate(ValidDraft() with { Language = language });

        Assert.False(errors.ContainsKey(nameof(SettingsDraft.Language)));
    }

    [Theory]
    [InlineData("French")]
    [InlineData("JA")]
    [InlineData("j")]
    public void Validate_WhenLanguageNotIsoCode_ReturnsLanguageError(string language)
    {
        var errors = SettingsValidator.Validate(ValidDraft() with { Language = language });

        Assert.Equal("settings.error.language", errors[nameof(SettingsDraft.Language)]);
    }

    [Fact]
    public void Validate_WhenThemeInvalid_ReturnsThemeError()
    {
        var errors = SettingsValidator.Validate(ValidDraft() with { Theme = "blue" });

        Assert.Equal("settings.error.theme", errors[nameof(SettingsDraft.Theme)]);
    }

    [Fact]
    public void Validate_WhenEngineValuesOutOfRange_ReturnsEachFieldError()
    {
        var errors = SettingsValidator.Validate(ValidDraft() with
        {
            ProviderProbeTimeoutSec = 0,
            LogRetentionDays = 0,
            StatsErrorRateThreshold = 101,
        });

        Assert.Equal(3, errors.Count);
        Assert.Equal("settings.error.probeTimeout", errors[nameof(SettingsDraft.ProviderProbeTimeoutSec)]);
        Assert.Equal("settings.error.retention", errors[nameof(SettingsDraft.LogRetentionDays)]);
        Assert.Equal("settings.error.threshold", errors[nameof(SettingsDraft.StatsErrorRateThreshold)]);
    }

    [Fact]
    public void Validate_WhenTransientValuesAtRangeEdges_ReturnsEmpty()
    {
        var errors = SettingsValidator.Validate(ValidDraft() with
        {
            TransientMaxRetries = 0,          // 0 hợp lệ = tắt
            TransientBackoffBaseMs = 250,     // min hợp lệ
        });
        Assert.Empty(errors);
    }

    [Fact]
    public void Validate_WhenTransientValuesOutOfRange_ReturnsEachFieldError()
    {
        var low = SettingsValidator.Validate(ValidDraft() with { TransientMaxRetries = -1, TransientBackoffBaseMs = 249 });
        var high = SettingsValidator.Validate(ValidDraft() with { TransientMaxRetries = 11, TransientBackoffBaseMs = 4001 });

        Assert.Equal("settings.error.transientRetries", low[nameof(SettingsDraft.TransientMaxRetries)]);
        Assert.Equal("settings.error.transientBackoffBase", low[nameof(SettingsDraft.TransientBackoffBaseMs)]);
        Assert.Equal("settings.error.transientRetries", high[nameof(SettingsDraft.TransientMaxRetries)]);
        Assert.Equal("settings.error.transientBackoffBase", high[nameof(SettingsDraft.TransientBackoffBaseMs)]);
    }

    [Fact]
    public void Validate_WhenUpstreamIdleTimeoutAtRangeEdges_ReturnsEmpty()
    {
        // 0 hợp lệ = tắt watchdog; 3600 = max hợp lệ
        var errors = SettingsValidator.Validate(ValidDraft() with
        {
            UpstreamIdleTimeoutSec = 0,
        });
        Assert.Empty(errors);

        errors = SettingsValidator.Validate(ValidDraft() with { UpstreamIdleTimeoutSec = 3600 });
        Assert.Empty(errors);
    }

    [Fact]
    public void Validate_WhenUpstreamIdleTimeoutOutOfRange_ReturnsError()
    {
        var low = SettingsValidator.Validate(ValidDraft() with { UpstreamIdleTimeoutSec = -1 });
        var high = SettingsValidator.Validate(ValidDraft() with { UpstreamIdleTimeoutSec = 3601 });

        Assert.Equal("settings.error.upstreamIdleTimeout", low[nameof(SettingsDraft.UpstreamIdleTimeoutSec)]);
        Assert.Equal("settings.error.upstreamIdleTimeout", high[nameof(SettingsDraft.UpstreamIdleTimeoutSec)]);
    }
}
