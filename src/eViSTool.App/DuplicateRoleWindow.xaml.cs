using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using eViSTool.App.ViewModels;
using eViSTool.Core.Localization;
using eViSTool.Core.Server.Config;

namespace eViSTool.App;

/// <summary>Дублирование роли: код и название копии. Код проверяется по тем же правилам, что и в редакторе ролей.</summary>
public partial class DuplicateRoleWindow : Window
{
    private readonly IReadOnlyList<string> _takenCodes;

    /// <summary>Код новой роли (после «Создать копию»).</summary>
    public string RoleCode { get; private set; } = "";

    /// <summary>Название новой роли; пусто — оставить как у исходной.</summary>
    public string RoleName { get; private set; } = "";

    public DuplicateRoleWindow(string sourceCode, string sourceName, IReadOnlyList<string> takenCodes)
    {
        _takenCodes = takenCodes;
        InitializeComponent();

        Heading.Text = Loc.T("roles.dupHeading", sourceName);
        NameBox.Text = Loc.T("roles.dupNameDefault", sourceName);
        CodeBox.Text = SuggestCode(sourceCode);
        Check();
        Loaded += (_, _) =>
        {
            CodeBox.Focus();
            CodeBox.SelectAll();
        };
    }

    /// <summary>Свободный код на основе исходного: suplayer → suplayer2, suplayer3…</summary>
    private string SuggestCode(string sourceCode)
    {
        var stem = sourceCode.Any(char.IsWhiteSpace) || sourceCode.Length == 0 ? "role" : sourceCode;
        for (var n = 2; ; n++)
        {
            var code = stem + n.ToString(CultureInfo.InvariantCulture);
            if (RoleCatalog.CheckCode(code, _takenCodes) == RoleCodeProblem.None) return code;
        }
    }

    private void CodeBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (IsInitialized) Check();
    }

    private bool Check()
    {
        var code = CodeBox.Text.Trim();
        var error = RoleItemViewModel.CodeProblemText(RoleCatalog.CheckCode(code, _takenCodes), code);
        ErrorText.Text = error ?? "";
        ErrorText.Visibility = error is null ? Visibility.Collapsed : Visibility.Visible;
        GoButton.IsEnabled = error is null;
        return error is null;
    }

    private void Go_Click(object sender, RoutedEventArgs e)
    {
        if (!Check()) return;
        RoleCode = CodeBox.Text.Trim();
        RoleName = NameBox.Text.Trim();
        DialogResult = true;
    }
}
