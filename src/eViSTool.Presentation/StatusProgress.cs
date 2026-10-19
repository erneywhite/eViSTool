namespace eViSTool.Presentation;

/// <summary>
/// Ход долгого дела в строку состояния, который после конца дела молчит. <see cref="Progress{T}"/> доставляет отчёты
/// через очередь окна, и последний («Спрашиваю модбазу: 16/16») приходил уже после итога («Проверено в …») и затирал
/// его. <see cref="Close"/> зовут на потоке окна сразу после await, до итога: всё, что ещё в очереди, отбрасывается.
/// </summary>
/// <param name="show">Показать текст (на потоке окна).</param>
/// <param name="still">Ещё уместно ли показывать (например, профиль не сменили); null — всегда.</param>
public sealed class StatusProgress(Action<string> show, Func<bool>? still = null) : IProgress<string>
{
    private volatile bool _closed;

    public void Report(string value)
    {
        if (_closed) return;
        Ui.Post(() =>
        {
            if (!_closed && (still?.Invoke() ?? true)) show(value);
        });
    }

    /// <summary>Дело кончилось: отчёты, которые ещё не дошли, не показывать.</summary>
    public void Close() => _closed = true;
}
