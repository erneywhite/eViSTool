using Avalonia.Interactivity;
using eViSTool.Core.Notifications;
using eViSTool.Presentation.ViewModels;

namespace eViSTool.Desktop.Dialogs;

/// <summary>
/// Канал оповещений (DataContext — <see cref="NotifyChannelEditorViewModel"/>): тип, его поля, поиск чата Telegram,
/// проверочное сообщение. Ответ — канал после «Сохранить», закрыли без сохранения — null. Здесь только вид: закрыть
/// окно с ответом модели.
/// </summary>
public partial class NotifyChannelWindow : DialogWindow
{
    private NotifyChannelEditorViewModel? _model;

    public NotifyChannelWindow() => InitializeComponent();

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_model is not null)
        {
            _model.Finished -= OnFinished;
            _model.ChatListRequested -= OpenChatList;
        }
        _model = DataContext as NotifyChannelEditorViewModel;
        if (_model is null) return;
        _model.Finished += OnFinished;
        _model.ChatListRequested += OpenChatList;
    }

    private void OnFinished(NotifyChannel channel) => Close(channel);

    /// <summary>Нашлось несколько чатов — раскрыть список, чтобы выбрали, куда слать.</summary>
    private void OpenChatList() => ChatBox.IsDropDownOpen = true;

    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close();
}
