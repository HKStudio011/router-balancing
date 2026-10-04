namespace RouterBalancing.Core.Providers;

/// <summary>
/// Validate draft account ở service boundary. Throw <see cref="ArgumentException"/> với message
/// kỹ thuật (không phải i18n) — UI chặn trước bằng CanSave + input min/max, toast dùng key chung.
/// </summary>
public static class ProviderAccountValidator
{
    /// <summary>Throw <see cref="ArgumentException"/> nếu draft vi phạm rule nào đó — chặn sớm ở service boundary.</summary>
    /// <param name="draft">Bản nháp form cần validate.</param>
    /// <param name="requireApiKey">Create bắt buộc key (trừ khi draft.NoKey); update rỗng = giữ key cũ.</param>
    /// <exception cref="ArgumentException">Khi draft vi phạm rule nào đó.</exception>
    public static void ValidateAndThrow(ProviderAccountDraft draft, bool requireApiKey)
    {
        if (string.IsNullOrWhiteSpace(draft.Name))
        {
            throw new ArgumentException("Name is required.");
        }
        if (draft.Name.Trim().Length > 100)
        {
            throw new ArgumentException("Name must be 100 characters or fewer.");
        }
        if (requireApiKey && !draft.NoKey && string.IsNullOrWhiteSpace(draft.ApiKey))
        {
            throw new ArgumentException("API key is required.");
        }
        if (draft.Weight is < 0 or > 10_000)
        {
            throw new ArgumentException("Weight must be between 0 and 10000.");
        }
        if (draft.DailyTokenLimit is <= 0)
        {
            throw new ArgumentException("Daily token limit must be greater than 0.");
        }
        if (draft.DailyRequestLimit is <= 0)
        {
            throw new ArgumentException("Daily request limit must be greater than 0.");
        }
        if (draft.ModelPatterns.Length > 50)
        {
            throw new ArgumentException("At most 50 model patterns.");
        }
        if (draft.ModelPatterns.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException("Model pattern must not be empty.");
        }
    }
}
