using Avalonia.Controls;

namespace eViSTool.Desktop.Views;

/// <summary>Вкладка «Игроки» раздела «Сервер». DataContext — ServerPlayersViewModel, его задаёт ServerView. Логика вся в модели.</summary>
public partial class ServerPlayersTab : UserControl
{
    public ServerPlayersTab() => InitializeComponent();
}