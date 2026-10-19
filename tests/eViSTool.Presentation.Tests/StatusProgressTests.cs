namespace eViSTool.Presentation.Tests;

/// <summary>
/// Ход дела в строку состояния: отчёт, который ещё в очереди окна, когда дело кончилось, не затирает итог. С
/// Progress&lt;T&gt; последний «Спрашиваю модбазу: 16/16» приходил после «Проверено в …».
/// </summary>
public sealed class StatusProgressTests
{
    [Fact]
    public void Report_still_queued_after_Close_is_dropped() => Sta.Run(() =>
    {
        var shown = new List<string>();
        var progress = new StatusProgress(shown.Add);
        ((IProgress<string>)progress).Report("1/2");
        Sta.WaitFor(() => shown.Count == 1);

        ((IProgress<string>)progress).Report("2/2"); // ещё в очереди окна…
        progress.Close();                           // …а дело уже кончилось
        shown.Add("Checked");
        Sta.Wait(Task.Delay(100));

        Assert.Equal(["1/2", "Checked"], shown);
    });

    [Fact]
    public void Report_is_dropped_when_no_longer_relevant() => Sta.Run(() =>
    {
        var shown = new List<string>();
        var relevant = true;
        var progress = new StatusProgress(shown.Add, () => relevant);
        relevant = false; // профиль сменили
        ((IProgress<string>)progress).Report("1/2");
        Sta.Wait(Task.Delay(100));
        Assert.Empty(shown);
    });
}
