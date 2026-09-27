using RouterBalancing.Core.Domain;

namespace RouterBalancing.Core.Combos;

/// <summary>CRUD combo — cycle check chạy lúc save, không để FK Restrict nổ (spec §3).</summary>
public interface IComboService
{
    /// <summary>Tất cả combo, sắp theo Name, đã Include Items.</summary>
    Task<IReadOnlyList<Combo>> ListAsync(CancellationToken ct = default);

    /// <summary>Tạo combo mới từ draft — Position gán 0..n-1 theo thứ tự Items.</summary>
    /// <exception cref="ComboValidationException">Khi draft không hợp lệ.</exception>
    /// <exception cref="ArgumentException">Khi Name rỗng sau trim.</exception>
    Task<Combo> CreateAsync(ComboDraft draft, CancellationToken ct = default);

    /// <summary>Cập nhật — Items thay thế toàn bộ theo draft.</summary>
    /// <exception cref="KeyNotFoundException">Khi không có combo <paramref name="id"/>.</exception>
    /// <exception cref="ComboValidationException">Khi draft không hợp lệ, gồm cycle.</exception>
    /// <exception cref="ArgumentException">Khi Name rỗng sau trim.</exception>
    Task UpdateAsync(long id, ComboDraft draft, CancellationToken ct = default);

    /// <summary>Xóa combo — Items cascade; chặn khi còn combo khác trỏ tới (FK Restrict backstop).</summary>
    /// <exception cref="KeyNotFoundException">Khi không có combo <paramref name="id"/>.</exception>
    /// <exception cref="InvalidOperationException">Khi còn combo tham chiếu.</exception>
    Task DeleteAsync(long id, CancellationToken ct = default);

    /// <summary>Tên combo đang trỏ tới <paramref name="id"/>, sắp theo Name — cho warning của UI trước khi xóa.</summary>
    Task<IReadOnlyList<string>> GetReferencingComboNamesAsync(long id, CancellationToken ct = default);
}
