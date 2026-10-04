using RouterBalancing.Core.Settings;

namespace router_balancing_test.Settings;

public class SettingsValidatorTests
{
    private static SettingsDraft ValidDraft() => new()
    {
        Language = "auto",
        Theme = "system",
        Port = 8317,
        MaxRetry = 3,
        WatchdogIntervalSec = 60,
        ProviderProbeTimeoutSec = 60,
        LogRetentionDays = 90,
        StatsErrorRateThreshold = 10,
    };

    [Fact]
    public void Validate_WhenDraftValid_ReturnsEmpty()
    {
        var errors = SettingsValidator.Validate(ValidDraft());

        Assert.Empty(errors);
    }

    [Fact]
    public void Validate_WhenPortOutOfRange_ReturnsPortError()
    {
        var low = SettingsValidator.Validate(ValidDraft() with { Port = 80 });
        var high = SettingsValidator.Validate(ValidDraft() with { Port = 70000 });

        Assert.Equal("settings.error.port", low[nameof(SettingsDraft.Port)]);
        Assert.Equal("settings.error.port", high[nameof(SettingsDraft.Port)]);
    }

    [Fact]
    public void Validate_WhenLanguageInvalid_ReturnsLanguageError()
    {
        var errors = SettingsValidator.Validate(ValidDraft() with { Language = "fr" });

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
            MaxRetry = 0,
            WatchdogIntervalSec = 5,
            ProviderProbeTimeoutSec = 0,
            LogRetentionDays = 0,
            StatsErrorRateThreshold = 101,
        });

        Assert.Equal(5, errors.Count);
        Assert.Equal("settings.error.maxRetry", errors[nameof(SettingsDraft.MaxRetry)]);
        Assert.Equal("settings.error.watchdog", errors[nameof(SettingsDraft.WatchdogIntervalSec)]);
        Assert.Equal("settings.error.probeTimeout", errors[nameof(SettingsDraft.ProviderProbeTimeoutSec)]);
        Assert.Equal("settings.error.retention", errors[nameof(SettingsDraft.LogRetentionDays)]);
        Assert.Equal("settings.error.threshold", errors[nameof(SettingsDraft.StatsErrorRateThreshold)]);
    }
}
