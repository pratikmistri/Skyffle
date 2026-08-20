using Microsoft.Graphics.Canvas.UI;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Graphics;
using ComputeSharp;
using ComputeSharp.D2D1.WinUI;
using Skyffle.Graphics;
using Skyffle.ViewModels;

namespace Skyffle;

public sealed partial class MainWindow : Microsoft.UI.Xaml.Window
{
    public MainViewModel ViewModel { get; } = new();

    private readonly SkyState sky = new();
    private PixelShaderEffect<SkyShader>? skyEffect;

    public MainWindow()
    {
        InitializeComponent();

        ExtendsContentIntoTitleBar = true;
        var titleBar = AppWindow.TitleBar;
        titleBar.PreferredHeightOption = Microsoft.UI.Windowing.TitleBarHeightOption.Tall;
        // caption buttons are system-drawn and ignore the XAML dark theme; recolor for the sky
        titleBar.ButtonBackgroundColor = Microsoft.UI.Colors.Transparent;
        titleBar.ButtonInactiveBackgroundColor = Microsoft.UI.Colors.Transparent;
        titleBar.ButtonForegroundColor = Microsoft.UI.Colors.White;
        titleBar.ButtonInactiveForegroundColor = Windows.UI.Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF);
        titleBar.ButtonHoverBackgroundColor = Windows.UI.Color.FromArgb(0x28, 0xFF, 0xFF, 0xFF);
        titleBar.ButtonHoverForegroundColor = Microsoft.UI.Colors.White;
        titleBar.ButtonPressedBackgroundColor = Windows.UI.Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF);
        titleBar.ButtonPressedForegroundColor = Microsoft.UI.Colors.White;
        SetTitleBar(TitleBarDragRegion);
        AppWindow.Resize(new SizeInt32(1040, 1200));

        ViewModel.WeatherApplied += (code, isDay, cloudCover, windKmh) =>
        {
            // debug hook: SKYFFLE_FORCE_WMO=<code> [SKYFFLE_FORCE_NIGHT=1] previews any condition
            if (int.TryParse(Environment.GetEnvironmentVariable("SKYFFLE_FORCE_WMO"), out int forced))
            {
                sky.ApplyWeather(forced, Environment.GetEnvironmentVariable("SKYFFLE_FORCE_NIGHT") != "1", 80, windKmh);
            }
            else
            {
                sky.ApplyWeather(code, isDay, cloudCover, windKmh);
            }
        };

        Closed += (_, _) => SkyCanvas.RemoveFromVisualTree();

        // keep the shader's picture of the UI surfaces fresh so rain lands on them
        if (Content is Microsoft.UI.Xaml.FrameworkElement root)
        {
            root.LayoutUpdated += (_, _) => UpdateCardRects();
        }
        ContentScroller.ViewChanged += (_, _) => UpdateCardRects();
    }

    private void UpdateCardRects()
    {
        if (Content is not Microsoft.UI.Xaml.UIElement root || Content.XamlRoot is null) return;
        float scale = (float)Content.XamlRoot.RasterizationScale;
        // the forecast cards catch the rain; the top bar is left out on purpose
        sky.CardA = GetRect(HourlyCard, root, scale);
        sky.CardB = GetRect(DailyCard, root, scale);
        sky.CardC = default;
        sky.CardD = default;

        // detail cards are a uniform VariableSizedWrapGrid (174x120 cells, 4px item
        // margin — keep in sync with MainWindow.xaml); the shader derives each
        // card's rect from the grid so every card catches rain individually
        var panel = DetailsHost.ItemsPanelRoot;
        if (panel is not null && panel.ActualWidth > 1 && ViewModel.Details.Count > 0)
        {
            var rect = GetRect(panel, root, scale);
            int columns = Math.Max(1, (int)Math.Round(panel.ActualWidth / 174.0));
            sky.DetailsGrid = new float4(rect.X, rect.Y, 174f * scale, 120f * scale);
            sky.DetailsMeta = new float4(columns, ViewModel.Details.Count, 4f * scale, 0);
        }
        else
        {
            sky.DetailsGrid = default;
            sky.DetailsMeta = default;
        }
    }

    private static float4 GetRect(Microsoft.UI.Xaml.FrameworkElement el, Microsoft.UI.Xaml.UIElement root, float scale)
    {
        try
        {
            var p = el.TransformToVisual(root).TransformPoint(new Windows.Foundation.Point(0, 0));
            return new float4(
                (float)p.X * scale, (float)p.Y * scale,
                (float)el.ActualWidth * scale, (float)el.ActualHeight * scale);
        }
        catch
        {
            return default; // element not in the tree yet; zero-height rects are ignored by the shader
        }
    }

    private void OnCreateResources(CanvasAnimatedControl sender, CanvasCreateResourcesEventArgs args)
    {
        skyEffect = new PixelShaderEffect<SkyShader>();
    }

    private void OnDraw(ICanvasAnimatedControl sender, CanvasAnimatedDrawEventArgs args)
    {
        if (skyEffect is null) return;

        sky.Step(args.Timing.ElapsedTime.TotalSeconds);

        float t = (float)args.Timing.TotalTime.TotalSeconds;
        float scale = sender.Dpi / 96f;
        var res = new float2((float)sender.Size.Width * scale, (float)sender.Size.Height * scale);

        skyEffect.ConstantBuffer = new SkyShader(
            t, res,
            sky.Daylight, sky.Cloud, sky.Rain, sky.Snow, sky.Fog, sky.Lightning, sky.Wind,
            sky.CardA, sky.CardB, sky.CardC, sky.CardD,
            sky.DetailsGrid, sky.DetailsMeta);

        args.DrawingSession.DrawImage(skyEffect);
    }

    private async void OnSearchTextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason == AutoSuggestionBoxTextChangeReason.UserInput)
        {
            await ViewModel.SearchAsync(sender.Text);
        }
    }

    private void OnHeroRightTapped(object sender, Microsoft.UI.Xaml.Input.RightTappedRoutedEventArgs e)
    {
        ViewModel.ToggleUnitCommand.Execute(null);
        e.Handled = true;
    }

    private void OnSuggestionChosen(AutoSuggestBox sender, AutoSuggestBoxSuggestionChosenEventArgs args)
    {
        if (args.SelectedItem is Models.GeoResult geo)
        {
            ViewModel.AddLocation(geo);
            sender.Text = "";
        }
    }
}
