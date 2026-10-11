using System.Globalization;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Headless.XUnit;
using eViSTool.Desktop.Converters;
using eViSTool.Presentation.ViewModels;

namespace eViSTool.Desktop.Tests;

public class ConverterTests
{
    private static readonly CultureInfo Culture = CultureInfo.InvariantCulture;

    [Fact]
    public void EnumEquals_converts_to_bool()
    {
        var c = EnumEqualsConverter.Instance;
        Assert.Equal(true, c.Convert(ConfigLoadState.Loaded, typeof(bool), "Loaded", Culture));
        Assert.Equal(false, c.Convert(ConfigLoadState.Loading, typeof(bool), "Loaded", Culture));
        Assert.Equal(false, c.Convert(null, typeof(bool), "Loaded", Culture));
    }

    [Fact]
    public void EnumEquals_converts_back_only_when_checked()
    {
        var c = EnumEqualsConverter.Instance;
        Assert.Equal(ConfigLoadState.Missing, c.ConvertBack(true, typeof(ConfigLoadState), "Missing", Culture));
        Assert.Equal(ConfigLoadState.Missing, c.ConvertBack(true, typeof(ConfigLoadState?), "Missing", Culture));
        Assert.Same(BindingOperations.DoNothing, c.ConvertBack(false, typeof(ConfigLoadState), "Missing", Culture));
    }

    /// <summary>Чипы-фильтры: выбор соседнего чипа пишет своё значение, снятие галочки с прежнего — ничего.</summary>
    [AvaloniaFact]
    public void EnumEquals_drives_radio_buttons_both_ways()
    {
        var model = new FilterModel { State = ConfigLoadState.Loading };
        RadioButton Chip(string name) => new()
        {
            GroupName = "chips",
            [!RadioButton.IsCheckedProperty] = new Binding(nameof(FilterModel.State))
            {
                Mode = BindingMode.TwoWay, Converter = EnumEqualsConverter.Instance, ConverterParameter = name,
            },
        };
        var loading = Chip("Loading");
        var loaded = Chip("Loaded");
        var window = new Window { DataContext = model, Content = new StackPanel { Children = { loading, loaded } } };
        window.Show();

        Assert.True(loading.IsChecked);
        Assert.False(loaded.IsChecked);

        loaded.IsChecked = true;
        Assert.Equal(ConfigLoadState.Loaded, model.State);
        Assert.False(loading.IsChecked);

        model.State = ConfigLoadState.Loading;
        Assert.True(loading.IsChecked);
        Assert.False(loaded.IsChecked);
        window.Close();
    }

    [Theory]
    [InlineData(1000, "w", 240)] // 4 в ряд: 250 − 6 − 4
    [InlineData(1000, "h", 135)] // 16:9
    [InlineData(520, "w", 250)] // ровно 2 по 260
    [InlineData(0, "w", 190)] // ширины ещё нет — как у ряда 400: 2 по 200
    public void ScreenshotThumb_splits_row(double row, string part, double expected) =>
        Assert.Equal(expected, ScreenshotThumbConverter.Instance.Convert(row, typeof(double), part, Culture));

    private sealed class FilterModel : CommunityToolkit.Mvvm.ComponentModel.ObservableObject
    {
        private ConfigLoadState _state;

        public ConfigLoadState State
        {
            get => _state;
            set => SetProperty(ref _state, value);
        }
    }
}
