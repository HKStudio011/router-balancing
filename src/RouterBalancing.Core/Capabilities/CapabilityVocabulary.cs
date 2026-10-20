namespace RouterBalancing.Core.Capabilities;

/// <summary>Nguồn của một dòng capability fact — quyết định quyền ghi đè (C-E/C-N, spec §5).</summary>
public enum CapabilitySource
{
    /// <summary>Danh mục chính thức của provider (opencode catalog, ollama tags, trang NVIDIA, OpenRouter).</summary>
    Catalog = 0,

    /// <summary>Probe endpoint <c>/v1/models/{id}</c> — không phải full-list, không được sinh dòng KnownAbsent (C-P).</summary>
    Endpoint = 1,

    /// <summary>Người dùng đặt tay — catalog/endpoint không bao giờ đè lên (C-N).</summary>
    User = 2,
}

/// <summary>Capability boolean của model — chỉ tools/reasoning; vision/audio/video là modality (C-H).</summary>
public enum CapabilityKind
{
    Tools = 0,
    Reasoning = 1,
}

/// <summary>Loại modality của model; chiều input/output phân biệt ở dòng fact chứa nó (C-H/C-O).</summary>
public enum ModalityKind
{
    Text = 0,
    Image = 1,
    Audio = 2,
    Video = 3,

    /// <summary>
    /// Input dạng tệp (PDF/DOCX…) — OpenRouter list thật dùng token "file" ở
    /// architecture.input_modalities (193/470 model, probe 2026-10-20); schema opencode
    /// capabilities.input là array of string không enum → "file" hợp lệ (R-T5-5).
    /// </summary>
    File = 4,
}

/// <summary>Chiều của modality so với request chat.</summary>
public enum ModalityDirection
{
    Input = 0,
    Output = 1,
}

/// <summary>
/// Từ vựng capability: map enum ↔ giá trị TEXT chữ thường trong DB (spec §6).
/// Token là storage contract (đúng DDL "TEXT in ('tools','reasoning')", …) — đổi token
/// nào cũng phải kèm migration dời dữ liệu, không đổi âm thầm.
/// </summary>
public static class CapabilityNames
{
    public static string ToDb(this CapabilityKind value) => value switch
    {
        CapabilityKind.Tools => "tools",
        CapabilityKind.Reasoning => "reasoning",
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, "Unknown capability kind."),
    };

    public static string ToDb(this ModalityKind value) => value switch
    {
        ModalityKind.Text => "text",
        ModalityKind.Image => "image",
        ModalityKind.Audio => "audio",
        ModalityKind.Video => "video",
        ModalityKind.File => "file",
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, "Unknown modality kind."),
    };

    public static string ToDb(this ModalityDirection value) => value switch
    {
        ModalityDirection.Input => "input",
        ModalityDirection.Output => "output",
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, "Unknown modality direction."),
    };

    /// <summary>Đọc token TEXT → enum; false nếu token không thuộc từ vựng (so khớp tuyệt đối, kể cả khác hoa thường).</summary>
    public static bool TryFromDb(string value, out CapabilityKind kind)
    {
        switch (value)
        {
            case "tools":
                kind = CapabilityKind.Tools;
                return true;
            case "reasoning":
                kind = CapabilityKind.Reasoning;
                return true;
            default:
                kind = default;
                return false;
        }
    }

    /// <inheritdoc cref="TryFromDb(string, out CapabilityKind)"/>
    public static bool TryFromDb(string value, out ModalityKind modality)
    {
        switch (value)
        {
            case "text":
                modality = ModalityKind.Text;
                return true;
            case "image":
                modality = ModalityKind.Image;
                return true;
            case "audio":
                modality = ModalityKind.Audio;
                return true;
            case "video":
                modality = ModalityKind.Video;
                return true;
            case "file":
                modality = ModalityKind.File;
                return true;
            default:
                modality = default;
                return false;
        }
    }

    /// <inheritdoc cref="TryFromDb(string, out CapabilityKind)"/>
    public static bool TryFromDb(string value, out ModalityDirection direction)
    {
        switch (value)
        {
            case "input":
                direction = ModalityDirection.Input;
                return true;
            case "output":
                direction = ModalityDirection.Output;
                return true;
            default:
                direction = default;
                return false;
        }
    }

    /// <summary>
    /// Đọc token TEXT → enum (ngược chiều <c>ToDb</c>). Ném <see cref="FormatException"/>
    /// cho giá trị lạ — giá trị hỏng trong DB phải nổ to, không âm thầm thành giá trị đầu enum.
    /// </summary>
    public static T FromDb<T>(string value) where T : struct, Enum
    {
        if (typeof(T) == typeof(CapabilityKind) && TryFromDb(value, out CapabilityKind kind))
            return (T)(object)kind;
        if (typeof(T) == typeof(ModalityKind) && TryFromDb(value, out ModalityKind modality))
            return (T)(object)modality;
        if (typeof(T) == typeof(ModalityDirection) && TryFromDb(value, out ModalityDirection direction))
            return (T)(object)direction;
        throw new FormatException($"Unknown {typeof(T).Name} capability token: '{value}'.");
    }
}
