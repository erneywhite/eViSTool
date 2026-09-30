using CommunityToolkit.Mvvm.ComponentModel;
using eViSTool.Core.Localization;
using eViSTool.Core.ModDb;

namespace eViSTool.App.ViewModels;

public enum QueueState { Waiting, Working, Done, Failed, Cancelled }

/// <summary>Пункт очереди установки: обновление, откат или конкретная версия (из модбазы или сохранённая копия).</summary>
public sealed partial class UpdateQueueItem : ObservableObject
{
    public UpdateQueueItem(string modId, string name, string from, string to, ModDbRelease? release, string? path)
    {
        ModId = modId;
        Name = name;
        From = from;
        To = to;
        Release = release;
        Path = path;
    }

    public string ModId { get; }
    public string Name { get; }
    public string From { get; }
    public string To { get; }
    public string Versions => From.Length > 0 ? $"{From} → {To}" : To;

    /// <summary>Что ставить: релиз модбазы (скачать) или сохранённая копия.</summary>
    public ModDbRelease? Release { get; }
    public string? Path { get; }

    [ObservableProperty] private QueueState _state = QueueState.Waiting;
    [ObservableProperty] private double _progress;
    [ObservableProperty] private string _message = "";

    public bool IsWorking => State == QueueState.Working;

    public string StateText => State switch
    {
        QueueState.Waiting => Loc.T("queue.waiting"),
        QueueState.Working => Progress > 0 ? Progress.ToString("P0") : Loc.T("queue.working"),
        QueueState.Done => Loc.T("queue.done"),
        QueueState.Failed => Loc.T("queue.failed"),
        _ => Loc.T("queue.cancelled"),
    };

    public RowTone Tone => State switch
    {
        QueueState.Done => RowTone.Good,
        QueueState.Working => RowTone.Update,
        QueueState.Failed => RowTone.Danger,
        _ => RowTone.Muted,
    };

    partial void OnStateChanged(QueueState value)
    {
        OnPropertyChanged(nameof(IsWorking));
        OnPropertyChanged(nameof(StateText));
        OnPropertyChanged(nameof(Tone));
    }

    partial void OnProgressChanged(double value) => OnPropertyChanged(nameof(StateText));
}
