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

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtrW(IntPtr hwnd, int index, IntPtr newProc);

    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "CallWindowProcW")]
    private static extern IntPtr CallWindowProcW(IntPtr prevProc, IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    private delegate IntPtr WndProcDelegate(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    private const int GWLP_WNDPROC = -4;
    private const uint WM_GETMINMAXINFO = 0x0024;

    // below ~760 epx the centered search box collides with the title-bar buttons
    private const int MinWidthEpx = 760;
    private const int MinHeightEpx = 520;

    private WndProcDelegate? wndProcHook; // field ref keeps the delegate alive for the hook's lifetime
    private IntPtr prevWndProc;

    private IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        var result = CallWindowProcW(prevWndProc, hwnd, msg, wParam, lParam);
        if (msg == WM_GETMINMAXINFO)
        {
            // read the DPI per message, not at construction: the window may since have
            // moved to a monitor with a different scale, and ptMinTrackSize is physical px
            double scale = GetDpiForWindow(hwnd) / 96.0;
            // MINMAXINFO: ptReserved, ptMaxSize, ptMaxPosition, ptMinTrackSize (offset 24), ptMaxTrackSize
            System.Runtime.InteropServices.Marshal.WriteInt32(lParam, 24, (int)(MinWidthEpx * scale));
            System.Runtime.InteropServices.Marshal.WriteInt32(lParam, 28, (int)(MinHeightEpx * scale));
        }
        return result;
    }

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

        // WinAppSDK 1.6 has no presenter min-size API, so clamp via WM_GETMINMAXINFO
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        wndProcHook = WndProc;
        prevWndProc = SetWindowLongPtrW(hwnd, GWLP_WNDPROC,
            System.Runtime.InteropServices.Marshal.GetFunctionPointerForDelegate(wndProcHook));

        // WM_GETMINMAXINFO only guards user drags, so clamp the startup size ourselves
        double startScale = GetDpiForWindow(hwnd) / 96.0;
        AppWindow.Resize(new SizeInt32(
            Math.Max(1040, (int)(MinWidthEpx * startScale)),
            Math.Max(1200, (int)(MinHeightEpx * startScale))));

        ViewModel.WeatherApplied += (code, isDay, cloudCover, windKmh) =>
        {
            // debug hook: SKYFFLE_FORCE_WMO=<code> [SKYFFLE_FORCE_NIGHT=1] previews any condition
            if (int.TryParse(Environment.GetEnvironmentVariable("SKYFFLE_FORCE_WMO"), out int forced))
            {
                code = forced;
                isDay = Environment.GetEnvironmentVariable("SKYFFLE_FORCE_NIGHT") != "1";
                cloudCover = 80;
            }
            sky.ApplyWeather(code, isDay, cloudCover, windKmh);
            // late/cleared frames present ClearColor; keep it near what the shader
            // will draw so neither day nor night ever flashes the wrong brightness
            SkyCanvas.ClearColor = isDay
                ? Windows.UI.Color.FromArgb(0xFF, 0x41, 0x61, 0x8E)
                : Windows.UI.Color.FromArgb(0xFF, 0x0A, 0x12, 0x26);
        };

        Closed += (_, _) => SkyCanvas.RemoveFromVisualTree();
        // don't burn a full GPU frame budget on a sky nobody can see
        VisibilityChanged += (_, e) => SkyCanvas.Paused = !e.Visible;

        // keep the shader's picture of the UI surfaces fresh so rain lands on them
        if (Content is Microsoft.UI.Xaml.FrameworkElement root)
        {
            root.LayoutUpdated += (_, _) => UpdateCardRects();
            // XamlRoot.Changed covers RasterizationScale changes (different-DPI monitor,
            // system scale), which don't necessarily fire SizeChanged
            root.Loaded += (_, _) =>
            {
                if (root.XamlRoot is { } xr && !xamlRootHooked)
                {
                    xamlRootHooked = true;
                    xr.Changed += (_, _) => UpdateRenderScale();
                }
            };
        }
        ContentScroller.ViewChanged += (_, _) => UpdateCardRects();
        SkyCanvas.SizeChanged += (_, _) => UpdateRenderScale();
        // column balancing runs on real size/content changes, not every layout pass;
        // Loaded catches the case where items land before the panel is materialized
        DetailsHost.SizeChanged += (_, _) => UpdateDetailsColumns();
        DetailsHost.Loaded += (_, _) => UpdateDetailsColumns();
        ViewModel.Details.CollectionChanged += (_, _) => UpdateDetailsColumns();
    }

    private bool xamlRootHooked;

    // Longest render-target edge the sky is allowed; larger surfaces (maximized on
    // high-DPI) pushed frame time past vsync on this GPU and the animation flickered.
    private const double MaxRenderEdge = 2400.0;

    /// <summary>Renders the sky at reduced resolution on large surfaces and lets the swap chain scale it up.</summary>
    private void UpdateRenderScale()
    {
        double rs = Content?.XamlRoot?.RasterizationScale ?? 1.0;
        double longest = Math.Max(SkyCanvas.ActualWidth, SkyCanvas.ActualHeight) * rs;
        float target = longest > MaxRenderEdge ? (float)(MaxRenderEdge / longest) : 1f;
        if (Math.Abs(SkyCanvas.DpiScale - target) > 0.02f)
        {
            SkyCanvas.DpiScale = target;
            UpdateCardRects();
        }
    }

    private void UpdateCardRects()
    {
        if (Content is not Microsoft.UI.Xaml.UIElement root || Content.XamlRoot is null) return;
        // canvas DPI, not RasterizationScale: it tracks DpiScale so rects stay in render-target pixels
        float scale = SkyCanvas.Dpi / 96f;
        // the forecast cards catch the rain; the top bar is left out on purpose
        sky.CardA = GetRect(HourlyCard, root, scale);
        sky.CardB = GetRect(DailyCard, root, scale);
        sky.CardC = default;
        sky.CardD = default;

        // detail cards are a uniform VariableSizedWrapGrid (120-tall cells, 4px item
        // margin — keep in sync with MainWindow.xaml); the shader derives each
        // card's rect from the grid so every card catches rain individually.
        // Read-only here: the wrap grid's actual laid-out values are used so the
        // shader never sees a column count the panel hasn't applied yet
        var panel = DetailsHost.ItemsPanelRoot;
        if (panel is VariableSizedWrapGrid wrap && panel.ActualWidth > 1
            && wrap.ItemWidth > 0 && wrap.MaximumRowsOrColumns > 0 && ViewModel.Details.Count > 0)
        {
            var rect = GetRect(panel, root, scale);
            sky.DetailsGrid = new float4(rect.X, rect.Y, (float)wrap.ItemWidth * scale, 120f * scale);
            sky.DetailsMeta = new float4(wrap.MaximumRowsOrColumns, ViewModel.Details.Count, 4f * scale, 0);
        }
        else
        {
            sky.DetailsGrid = default;
            sky.DetailsMeta = default;
        }
    }

    /// <summary>
    /// Balances the wrap grid so the last row is as full as possible (9 cards → 3×3)
    /// instead of leaving one card hanging alone, then stretches the cells so the grid
    /// spans the same width as the big cards. Runs on size/content changes only —
    /// mutating layout from inside a LayoutUpdated handler risks feedback loops.
    /// </summary>
    private void UpdateDetailsColumns()
    {
        if (DetailsHost.ItemsPanelRoot is not VariableSizedWrapGrid wrap
            || DetailsHost.ActualWidth <= 1 || ViewModel.Details.Count == 0) return;
        int maxFit = Math.Max(1, (int)(DetailsHost.ActualWidth / 174.0));
        int columns = BalancedColumns(ViewModel.Details.Count, maxFit);
        double itemWidth = Math.Floor(DetailsHost.ActualWidth / columns);
        if (wrap.MaximumRowsOrColumns != columns) wrap.MaximumRowsOrColumns = columns;
        if (Math.Abs(wrap.ItemWidth - itemWidth) > 0.5) wrap.ItemWidth = itemWidth;
    }

    /// <summary>
    /// Column count that leaves the fewest empty cells in the last row,
    /// so the grid ends in a straight line whenever the count allows it.
    /// Ties go to the wider layout.
    /// </summary>
    private static int BalancedColumns(int count, int maxFit)
    {
        if (count <= maxFit) return count;
        int best = maxFit;
        int bestEmpty = (maxFit - count % maxFit) % maxFit;
        for (int c = maxFit - 1; c >= 2 && bestEmpty > 0; c--)
        {
            int empty = (c - count % c) % c;
            if (empty < bestEmpty) { best = c; bestEmpty = empty; }
        }
        return best;
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
            scale,
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
