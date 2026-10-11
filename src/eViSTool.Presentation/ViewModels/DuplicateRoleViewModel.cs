using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using eViSTool.Core.Localization;
using eViSTool.Core.Server.Config;

namespace eViSTool.Presentation.ViewModels;

/// <summary>
/// Окно «Дублировать роль»: код и название копии. Код при открытии подбирается свободный (<see cref="SuggestCode"/>)
/// и проверяется по тем же правилам, что и в редакторе ролей (<see cref="RoleCatalog.CheckCode"/>): не пустой, без
/// пробелов, не занят другой ролью. Пока код не годится, под полем видна причина (<see cref="Error"/>), а «Создать копию»
/// недоступна (<see cref="CanGo"/>). Выбор сделан — <see cref="Finished"/> с ответом, окно закрывается с ним.
/// </summary>
public sealed partial class DuplicateRoleViewModel : ObservableObject
{
    /// <summary>
    /// Код для поля при открытии — только для снимка окна (dialog.duplicaterole.taken): подобранный код всегда свободен,
    /// и другого способа показать окно с занятым кодом у снимка нет. Берётся один раз, первой созданной моделью.
    /// </summary>
    internal static string? SnapshotCode;

    private readonly IReadOnlyList<string> _takenCodes;
    private string _error = "";
    private bool _canGo;

    /// <param name="sourceCode">Код роли, которую копируем (от него отталкивается подсказка).</param>
    /// <param name="sourceName">Название исходной роли: попадает в заголовок и в название копии по умолчанию.</param>
    /// <param name="takenCodes">Коды всех ролей списка — новый код не должен с ними совпадать (регистр не важен).</param>
    public DuplicateRoleViewModel(string sourceCode, string sourceName, IReadOnlyList<string> takenCodes)
    {
        _takenCodes = takenCodes;
        Heading = Loc.T("roles.dupHeading", sourceName);
        _name = Loc.T("roles.dupNameDefault", sourceName);
        _code = Interlocked.Exchange(ref SnapshotCode, null) ?? SuggestCode(sourceCode, takenCodes);
        Check();
    }

    /// <summary>Крупная надпись окна: «Копия роли «…»».</summary>
    public string Heading { get; }

    /// <summary>Код новой роли (как ввели, с пробелами по краям; в ответ идёт без них).</summary>
    [ObservableProperty] private string _code;

    /// <summary>Название новой роли; пусто — копия оставит название исходной.</summary>
    [ObservableProperty] private string _name;

    /// <summary>Чем не годится код; пусто — годится.</summary>
    public string Error
    {
        get => _error;
        private set
        {
            if (!SetProperty(ref _error, value)) return;
            OnPropertyChanged(nameof(HasError));
        }
    }

    /// <summary>Код не годится — <see cref="Error"/> виден под полем.</summary>
    public bool HasError => _error.Length > 0;

    /// <summary>Код годится — «Создать копию» доступна.</summary>
    public bool CanGo
    {
        get => _canGo;
        private set
        {
            if (SetProperty(ref _canGo, value)) GoCommand.NotifyCanExecuteChanged();
        }
    }

    /// <summary>Ответ окна; null — ещё не выбрали.</summary>
    public RoleCopyChoice? Result { get; private set; }

    /// <summary>Выбор сделан — окно закрывается с этим ответом.</summary>
    public event Action<RoleCopyChoice>? Finished;

    partial void OnCodeChanged(string value) => Check();

    /// <summary>
    /// Свободный код на основе исходного: suplayer → suplayer2, suplayer3… Если исходный код пустой или с пробелами,
    /// основой служит «role».
    /// </summary>
    public static string SuggestCode(string sourceCode, IReadOnlyList<string> takenCodes)
    {
        var stem = sourceCode.Any(char.IsWhiteSpace) || sourceCode.Length == 0 ? "role" : sourceCode;
        for (var n = 2; ; n++)
        {
            var code = stem + n.ToString(CultureInfo.InvariantCulture);
            if (RoleCatalog.CheckCode(code, takenCodes) == RoleCodeProblem.None) return code;
        }
    }

    /// <summary>Проверить код (без пробелов по краям) и обновить причину и доступность кнопки.</summary>
    private bool Check()
    {
        var code = (Code ?? "").Trim();
        var error = RoleItemViewModel.CodeProblemText(RoleCatalog.CheckCode(code, _takenCodes), code);
        Error = error ?? "";
        CanGo = error is null;
        return error is null;
    }

    /// <summary>«Создать копию»: код и название без пробелов по краям; пустое название — оставить как у исходной.</summary>
    [RelayCommand(CanExecute = nameof(CanGo))]
    private void Go()
    {
        if (!Check()) return;
        var choice = new RoleCopyChoice(Code.Trim(), (Name ?? "").Trim());
        Result = choice;
        Finished?.Invoke(choice);
    }
}
