namespace RouterBalancing.Core.Combos;

/// <summary>Lý do draft combo không hợp lệ — UI map sang key i18n combos.error.{camelCase}.</summary>
public enum ComboValidationError
{
    DuplicateName,
    EmptyItems,
    CycleDetected,
    InvalidItemTarget,
    TargetNotFound,
    NameTooLong,
}

/// <summary>Exception validation combo — giữ Code để UI chọn đúng thông điệp i18n.</summary>
public sealed class ComboValidationException : Exception
{
    /// <summary>Lý do cụ thể.</summary>
    public ComboValidationError Code { get; }

    public ComboValidationException(ComboValidationError code, string message)
        : base(message)
    {
        Code = code;
    }
}
