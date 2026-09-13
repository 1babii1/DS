namespace Shared;

/// <summary>
/// Страница результатов вместе с метаданными. Без Total клиент не может показать
/// пагинацию и не знает, когда остановиться: раньше endpoints принимали page/size,
/// но возвращали голый массив.
/// </summary>
public record PagedResponse<T>(IReadOnlyList<T> Items, int Page, int Size, int Total)
{
    /// <summary>Верхняя граница размера страницы, чтобы один запрос не выгребал таблицу целиком.</summary>
    public const int MaxSize = 200;

    public const int DefaultSize = 20;

    public bool HasNext => (long)Page * Size < Total;

    public static PagedResponse<T> Empty(int page, int size) => new([], page, size, 0);

    /// <summary>Приводит вход клиента к допустимому диапазону: страницы с 1, размер в пределах MaxSize.</summary>
    public static (int Page, int Size) Normalize(int? page, int? size) =>
        (Math.Max(page ?? 1, 1), Math.Clamp(size ?? DefaultSize, 1, MaxSize));
}
