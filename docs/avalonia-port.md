# Перенос экранов с WPF на Avalonia

Памятка для переноса окна eViSTool с WPF (`src/eViSTool.App`) на Avalonia 12 (`src/eViSTool.Desktop`). Образец переноса — главное окно (`Views/MainWindow.axaml`) и «Мои моды» (`Views/ModsView.axaml`): если что-то непонятно, смотри, как сделано там.

## Где что лежит

| Папка в `src/eViSTool.Desktop` | Что там |
|---|---|
| `Views/` | главное окно и его разделы, вкладки сервера |
| `Dialogs/` | окна программы, наследники `DialogWindow` |
| `Platform/AppWindows/` | по файлу на метод `IAppWindows`: открыть окно и вернуть ответ |
| `Controls/` | свои элементы и прикреплённые свойства: `RemoteImage`, `EnumClass`, `DoubleTap`, `Focus`, `WheelToParent`, `GameIcon` |
| `Converters/` | конвертеры, которых нет среди встроенных |
| `Themes/` | тема Forest: `ForestResources.axaml` (цвета, кисти Fluent, иконки, шрифты), `ForestControls.axaml` (шаблоны), `Forest.axaml` (классы) |

Модели — в `src/eViSTool.Presentation`, общие для обоих окон. Логику из кода за окном WPF переносим в модель, с тестами в `tests/eViSTool.App.Tests`. В коде за окном Avalonia остаётся только то, что касается самого вида: прокрутка, фокус, перетаскивание.

## Тема: что во что превращается

| WPF | Avalonia |
|---|---|
| `Style="{StaticResource Forest.PrimaryButton}"`, `AccentButtonStyle` | `Classes="primary"` |
| `Forest.DangerButton` | `Classes="danger"` |
| `Forest.SplitMain` / `Forest.SplitArrow` | `Classes="primary splitMain"` / `"primary splitArrow"` |
| `Forest.LinkButton`, `Forest.StatButton` | `Theme="{StaticResource Forest.LinkButton}"` и т. п. |
| `Forest.Switch` (CheckBox) | `Theme="{StaticResource Forest.Switch}"` |
| `Forest.Chip`, `Forest.TabHeader`, `Forest.StatTab` (RadioButton) | `Theme="{StaticResource Forest.Chip}"` и т. п. |
| `ItemContainerStyle="{StaticResource Forest.NavItem}"`, `Forest.CardItem` | `ItemContainerTheme="{StaticResource …}"` |
| `Forest.Progress` | `Theme="{StaticResource Forest.Progress}"` (значение — доля 0..1) |
| `ColumnHeaderStyle="{StaticResource Forest.ColumnHeader}"` | `ColumnHeaderTheme="{StaticResource Forest.ColumnHeader}"` |
| `Forest.Icon` (Path) | `Classes="icon"`: цвет линии берётся из цвета текста родителя |
| `Forest.Card`, `Forest.Warning` (Border) | `Classes="card"`, `Classes="warning"` |
| `Forest.PageTitle`, `Forest.MutedText` | `Classes="pageTitle"`, `Classes="muted"` |
| `Forest.SelectableText` | `SelectableTextBlock Classes="selectable"` |
| местные `Hint`, `Label`, `CardTitle`, `ErrorText`, `Row` | `Classes="hint"`, `"label"`, `"cardTitle"`, `"error"`, `Border Classes="row"`; отличие экрана — локальным значением |
| `FontFamily="Consolas"` | `Classes="mono"`, у `Run` — `FontFamily="{DynamicResource Forest.Mono}"` |
| `{StaticResource Forest.Text}` и другие кисти | `{DynamicResource Forest.Text}` |
| `Visibility="Hidden"` (место остаётся) | `Classes="hidden"` |
| бледная строка (`RowFade`) | `Classes.faded="{Binding !IsEnabled}"` |
| точка и текст тона (`ToneDot`, `ToneText`) | `Ellipse Classes="tone"` / `TextBlock Classes="tone"` + `ctl:EnumClass.Value="{Binding Tone}"`; варианты `good`, `update`, `warn`, `danger` |
| местные `Action`, `SmallButton` и т. п. — кнопка поменьше (Padding 12,4, MinHeight 0, FontSize 12) | `Classes="small"` |
| «ключ — значение» в карточке (`CardKey`, `CardValue`) | `Classes="cardKey"`, `Classes="cardValue"` |
| поле поиска с лупой и подсказкой поверх | `TextBox Classes="search" PlaceholderText="…"` с `<TextBox.InnerLeftContent><Path Classes="icon searchIcon" Data="{StaticResource Icon.Search}" /></TextBox.InnerLeftContent>` |

Тему трогаем только между группами. Если экрану нужен стиль, которого в теме нет, его пишем в `UserControl.Styles` или `Window.Styles` своего файла и упоминаем в отчёте; общее потом переедет в тему.

## Разметка

- На корне и в каждом `DataTemplate` ставим `x:DataType`: привязки компилируются, опечатка в имени свойства ломает сборку, а не молча гасит поле.
- `Visibility="{Binding X, Converter={StaticResource BoolToVis}}"` пишем как `IsVisible="{Binding X}"`. Инверсия делается через `!`: `IsVisible="{Binding !X}"`, `IsEnabled="{Binding !IsBusy}"`. Строка не пустая — `{x:Static StringConverters.IsNotNullOrEmpty}`, объект есть — `{x:Static ObjectConverters.IsNotNull}`, число больше нуля — `{x:Static conv:Counts.Positive}`.
- Перечисление ↔ RadioButton или видимость: `{Binding Filter, Converter={x:Static conv:EnumEqualsConverter.Instance}, ConverterParameter=History}`.
- `DataTrigger` по значению модели заменяем классом: `Classes.def="{Binding IsDefault}"` и стиль `Border.row.def`. По перечислению — `ctl:EnumClass.Value`. Приоритет стилей решает порядок объявления: последний побеждает, как порядок триггеров в WPF.
- Значение, которое меняет класс или состояние, задаём стилем, а не атрибутом. Атрибут (локальное значение) в Avalonia сильнее любого стиля, и класс его не перебьёт.
- `RelativeSource AncestorType=X` превращается в `$parent[X]`. С приведением типа: `{Binding $parent[UserControl].((vm:ModsViewModel)DataContext).SomeCommand}`. `ElementName=Foo` пишем как `#Foo`.
- Не ставим `DataContext="{Binding Child}"` на элемент, у которого ниже другие привязки. `x:DataType` на таком элементе действует и на саму привязку `DataContext`, и сборка падает. Вместо этого: `<ContentControl Content="{Binding Child}">` с `ContentTemplate` и `DataTemplate x:DataType="vm:ChildType"`, или прямые пути `Child.Name`.
- `ToolTip="…"` пишем как `ToolTip.Tip="…"`. `ToolTipService.ShowOnDisabled` превращается в `ToolTip.ShowOnDisabled="True"`. `ShowDuration` убираем: подсказка Avalonia висит, пока курсор на месте.
- `UpdateSourceTrigger=PropertyChanged` убираем: у `TextBox` это и так по умолчанию. `LostFocus` оставляем.
- Пробелы между `Run` пишем явно (`<Run Text=" · " />`) и ставим `Run` вплотную, без перевода строки: перенос строки в разметке пробелом не становится.
- `x:Name` у `ColumnDefinition` поля в коде не даёт. Колонку берём по индексу: `Split.ColumnDefinitions[2]`.
- `RenderOptions.BitmapScalingMode` → `RenderOptions.BitmapInterpolationMode`, `Cursor="SizeWE"` → `SizeWestEast`, картинки из ресурсов — `avares://eViSTool.Desktop/Assets/…`.
- Картинки по URL (логотипы, скриншоты): `<Image ctl:RemoteImage.Source="{Binding Logo}" />`. Сама `Image` http не грузит. Заглушку из букв показываем, пока у картинки нет `Source`: `IsVisible="{Binding #Logo1.Source, Converter={x:Static ObjectConverters.IsNull}}"`.
- `AutomationProperties.AutomationId` переносим один в один: по ним работает стенд.
- Меню по кнопке (`ContextMenu`, открываемое в Click) делаем через `<Button.Flyout><MenuFlyout>…</MenuFlyout></Button.Flyout>`, код не нужен. Контекстное меню таблицы наследует `DataContext` модели экрана; команде передаём выбранную строку модели (`CommandParameter="{Binding Selected}"`), а строку под правым кликом выделяет код окна (см. `ModsView.axaml.cs`).
- Двойной щелчок по строке: `ctl:DoubleTap.Command="{Binding OpenPageCommand}"`. Без `CommandParameter` в команду идёт элемент строки. Щелчок по кнопке или переключателю внутри строки команду не вызывает.
- Поле со списком и свободным вводом: встроенный `ComboBox IsEditable="True" Text="{Binding …}"` (текст двусторонний, обновляется на каждый символ). `DisplayMemberPath` → `DisplayMemberBinding="{Binding Title}"`, `TextSearch.TextPath` → `TextSearch.TextBinding="{Binding Name, DataType=vm:…}"` (без `DataType=` сборка падает). Автодополнения при вводе и групп у него нет. Группы делаем плоским списком: заголовок категории — отдельный тип элемента со своим шаблоном, контейнер заголовка выключен стилем.
- Колесо над закрытым списком крутит страницу, а не значение: `ctl:WheelToParent.IsEnabled="True"`. Фокус при открытии окна: `ctl:Focus.OnOpen="True"` (и `ctl:Focus.SelectAll="True"`).
- Значок игры: `Source="{Binding IconSearchDirs, Converter={x:Static conv:GameIconConverter.Instance}}"`.
- Таблица: `DataGrid` из `Avalonia.Controls.DataGrid`. `GridView`/`ListView` с колонками тоже переносим на `DataGrid`.
- Вложенный тип модели в разметке пишется через плюс: `vm:MainViewModel+DefaultServerOption` (в `DataType=`, `x:DataType`).
- `ContentControl` по умолчанию прижимает содержимое влево: для карточки во всю ширину нужен `HorizontalContentAlignment="Stretch"`. `ComboBox` сам не растягивается, как в WPF: `HorizontalAlignment="Stretch"`.
- Если модель в сеттере откатывает значение (отказались сохранить, текст не JSON), привязка Avalonia этого не увидит: во время своей записи она оповещения не слушает, а WPF после записи перечитывал источник. Такое место видно на экране как «переключатель остался в новом положении». Сверяй значение после записи в коде экрана (пример — `ModConfigView.axaml.cs`, чипы «Форма/Текст») или не откатывай в сеттере.
- Полоса прокрутки уже не ложится поверх содержимого: тема оставляет под неё место, как WPF. Ничего делать не надо.

## Окна (диалоги)

- Окно наследует `Dialogs/DialogWindow` (`<dialogs:DialogWindow x:Class="…">`): значок, центр владельца, Escape закрывает без ответа.
- Ответ возвращаем через `Close(ответ)` на `this`, открываем через `new XWindow { … }.ShowModal<TResult>()`. Модели ждут ответа на месте, как с `ShowDialog` в WPF: вложенный цикл диспетчера проверен на Windows и на Linux (Wayland).
- Открывает окно метод `IAppWindows` в своём файле `Platform/AppWindows/AvaloniaAppWindows.<Метод>.cs`: заменить заглушку на настоящее окно. Другие файлы `AppWindows` не трогаем.
- `DialogResult = true` становится `Close(результат)`, `IsCancel` — Escape (уже в базе), `IsDefault` у кнопки остаётся `IsDefault="True"`.

## Проверка

- `dotnet build eViSTool.slnx` без ошибок и предупреждений, `dotnet test eViSTool.slnx` зелёный. Свои элементы (`Controls/`, `Converters/`) проверяются headless-тестами в `tests/eViSTool.Desktop.Tests` (xunit v3).
- Снимки рядом: `pwsh build/snapshot.ps1 -Screens <экран>` → `<экран>.wpf.png` и `<экран>.avalonia.png` во временной папке. Окна берут данные стенда (`tests/snapshots/fixture`: серверный профиль «Стенд», четыре мода, конфиги модов в `ModConfig`), настоящие данные пользователя не трогаются. Нужного состояния на снимке нет (выбранная строка, открытая вкладка) — добавь экран в `Snapshots.Navigate` (`src/eViSTool.Presentation/Snapshots.cs`), как `mods.card`.
- Шрифт другой (Noto Sans вместо Segoe UI, Cascadia Mono вместо Consolas), поэтому текст чуть шире и плотнее. Это ожидаемо. Ищем другое: пропавшие элементы, чужие цвета Fluent (синий акцент, белые рамки), обрезанный текст, лишние отступы.
