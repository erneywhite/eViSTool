using System.Windows.Controls;
using System.Windows.Input;

namespace eViSTool.App;

/// <summary>Вкладка «Расписание» раздела «Сервер». DataContext — ServerScheduleViewModel.</summary>
public partial class ServerScheduleView : UserControl
{
    public ServerScheduleView() => InitializeComponent();

    // папка для копий применяется, когда поле отпустили или нажали Enter: на каждую букву проверять путь незачем
    private void BackupDirBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) BackupDirBox.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
    }
}
