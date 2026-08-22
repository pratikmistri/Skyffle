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

        // ambient loading: dim the content column while a forecast is in flight
        // (no spinner, so nothing shifts the layout)
        ViewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.IsLoading))
            {
                AnimateContentOpacity(ViewModel.IsLoading ? 0.45 : 1.0);
            }
        };

        ViewModel.WeatherApplied += c =>
        {
            // debug hooks: SKYFFLE_FORCE_WMO=<code> [SKYFFLE_FORCE_NIGHT=1] previews any
            // condition; SKYFFLE_FORCE_SUN / SKYFFLE_FORCE_MOONPOS pin the sun/moon on
            // their arcs (0..1); SKYFFLE_FORCE_MOON=0..1 pins the phase (0 new, 0.5 full);
            // SKYFFLE_FORCE_BOLT=1 makes every storm flash a drawn strike
            if (int.TryParse(Environment.GetEnvironmentVariable("SKYFFLE_FORCE_WMO"), out int forced))
            {
                c = c with
                {
                    WmoCode = forced,
                    IsDay = Environment.GetEnvironmentVariable("SKYFFLE_FORCE_NIGHT") != "1",
                    CloudCoverPercent = 80,
                };
            }
            if (TryEnvDouble("SKYFFLE_FORCE_SUN", out double s)) c = c with { SunProgress01 = s };
            if (TryEnvDouble("SKYFFLE_FORCE_MOONPOS", out double mp)) c = c with { MoonProgress01 = mp };
            if (TryEnvDouble("SKYFFLE_FORCE_MOON", out double ph)) c = c with { MoonPhase01 = ph };
            sky.ApplyWeather(c);
            sky.AlwaysBolt = Environment.GetEnvironmentVariable("SKYFFLE_FORCE_BOLT") == "1";
            // late/cleared frames present ClearColor; keep it near what the shader
            // will draw so neither day nor night ever flashes the wrong brightness
            SkyCanvas.ClearColor = c.IsDay
                ? Windows.UI.Color.FromArgb(0xFF, 0x41, 0x61, 0x8E)
                : Windows.UI.Color.FromArgb(0xFF, 0x0A, 0x12, 0x26);
        };

        Closed += (_, _) =>
        {
            // layout/scroll events can still fire during teardown; touching
            // Window.Content after close throws COMException (window closed)
            windowClosed = true;
            SkyCanvas.RemoveFromVisualTree();
        };
        // don't burn a full GPU frame budget on a sky nobody can see
        VisibilityChanged += (_, e) => SkyCanvas.Paused = !e.Visible;

        AttachHeroContrast();
        CentrePlaceholder();

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
    private bool windowClosed;

    private readonly List<(Windows.UI.Color Light, Windows.UI.Color Dark, Microsoft.UI.Xaml.Media.SolidColorBrush Brush)> heroInks = [];
    private double heroDarkness;
    private Microsoft.UI.Xaml.DispatcherTimer? contrastTimer;

    /// <summary>
    /// Adaptive ink for the condition/H-L/feels block only (the lines the low sun
    /// actually crosses): the type cross-fades from white to a deep sky navy
    /// (dark-on-bright is real contrast) and back as the sun moves away. The city
    /// and big temperature keep their usual white. Nothing is drawn over the sky.
    /// </summary>
    private void AttachHeroContrast()
    {
        (TextBlock Tb, byte Alpha)[] blocks =
        [
            (ConditionText, 0xB8), (HiLoText, 0xB8), (FeelsText, 0x7D),
        ];
        foreach (var (tb, a) in blocks)
        {
            var light = Windows.UI.Color.FromArgb(a, 0xFF, 0xFF, 0xFF);
            // dark ink raises thin alphas so secondary lines don't go muddy over the glow
            var dark = Windows.UI.Color.FromArgb(Math.Max(a, (byte)0xD8), 0x14, 0x21, 0x3A);
            var brush = new Microsoft.UI.Xaml.Media.SolidColorBrush(light);
            tb.Foreground = brush;
            heroInks.Add((light, dark, brush));
        }
        contrastTimer = new Microsoft.UI.Xaml.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        contrastTimer.Tick += (_, _) => UpdateHeroContrast();
        contrastTimer.Start();
        Closed += (_, _) => contrastTimer.Stop();
    }

    /// <summary>
    /// Centres the search field's placeholder. TextAlignment centres what the user
    /// types, but the ghost text lives in a separate presenter inside the TextBox
    /// template that ignores it, so reach in once the template is realised.
    /// </summary>
    private void CentrePlaceholder()
    {
        SearchBox.Loaded += (_, _) =>
        {
            if (FindDescendant(SearchBox, "PlaceholderTextContentPresenter") is ContentControl ghost)
            {
                ghost.HorizontalAlignment = Microsoft.UI.Xaml.HorizontalAlignment.Stretch;
                ghost.HorizontalContentAlignment = Microsoft.UI.Xaml.HorizontalAlignment.Center;
                // centred text puts the caret mid-word in the ghost, so drop the
                // ghost while the field has focus and the caret is showing
                SearchBox.GotFocus += (_, _) => ghost.Opacity = 0;
                SearchBox.LostFocus += (_, _) => ghost.Opacity = 1;
            }
        };
    }

    private static Microsoft.UI.Xaml.FrameworkElement? FindDescendant(Microsoft.UI.Xaml.DependencyObject root, string name)
    {
        int count = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            var child = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(root, i);
            if (child is Microsoft.UI.Xaml.FrameworkElement fe && fe.Name == name) return fe;
            if (FindDescendant(child, name) is { } hit) return hit;
        }
        return null;
    }

    /// <summary>Eases the hero ink toward its target each tick (10 Hz, ~0.4 s settle).</summary>
    private void UpdateHeroContrast()
    {
        if (windowClosed || Content is null) return;
        double w = SkyCanvas.ActualWidth, h = SkyCanvas.ActualHeight;
        if (w < 1 || h < 1 || HeroPanel.ActualWidth < 1) return;

        // mirror the shader's sun placement (fractions of the canvas, hourly-card horizon)
        double horizonFrac = 0.62;
        try
        {
            var hp = HourlyCard.TransformToVisual(Content).TransformPoint(new Windows.Foundation.Point(0, 0));
            horizonFrac = Math.Clamp(hp.Y / h, 0.30, 0.92);
        }
        catch { /* not in the tree yet; the fallback fraction is close enough */ }
        double sp = sky.SunProgress;
        double alt = Math.Sin(sp * Math.PI);
        double sunX = w * (0.10 + 0.80 * sp);
        double sunY = h * (horizonFrac - alt * (horizonFrac - 0.13));

        // distance from the sun to the nearest point of the condition/H-L/feels
        // block (the only lines that adapt), against the glow's effective radius
        double target = 0;
        try
        {
            var top = ConditionText.TransformToVisual(Content).TransformPoint(new Windows.Foundation.Point(0, 0));
            var bot = FeelsText.TransformToVisual(Content).TransformPoint(
                new Windows.Foundation.Point(FeelsText.ActualWidth, FeelsText.ActualHeight));
            double right = Math.Max(top.X + ConditionText.ActualWidth, bot.X);
            double nx = Math.Clamp(sunX, top.X, right);
            double ny = Math.Clamp(sunY, top.Y, bot.Y);
            double dist = Math.Sqrt((sunX - nx) * (sunX - nx) + (sunY - ny) * (sunY - ny));
            double glowR = 0.30 * h;
            target = Math.Clamp(1.0 - dist / glowR, 0, 1) * sky.Daylight * (1.0 - sky.Cloud);
        }
        catch { /* transient layout churn; keep the previous target */ }

        heroDarkness += (target - heroDarkness) * 0.35;
        if (Math.Abs(target - heroDarkness) < 0.005) heroDarkness = target;
        foreach (var (light, dark, brush) in heroInks)
        {
            brush.Color = LerpColor(light, dark, heroDarkness);
        }
    }

    private static Windows.UI.Color LerpColor(Windows.UI.Color a, Windows.UI.Color b, double t) =>
        Windows.UI.Color.FromArgb(
            (byte)(a.A + (b.A - a.A) * t), (byte)(a.R + (b.R - a.R) * t),
            (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));

    private void AnimateContentOpacity(double to)
    {
        if (windowClosed) return;
        var anim = new Microsoft.UI.Xaml.Media.Animation.DoubleAnimation
        {
            To = to,
            Duration = new Microsoft.UI.Xaml.Duration(TimeSpan.FromMilliseconds(250)),
            EasingFunction = new Microsoft.UI.Xaml.Media.Animation.QuadraticEase(),
        };
        Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTarget(anim, ContentScroller);
        Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTargetProperty(anim, "Opacity");
        var sb = new Microsoft.UI.Xaml.Media.Animation.Storyboard();
        sb.Children.Add(anim);
        sb.Begin();
    }

    private static bool TryEnvDouble(string name, out double value) =>
        double.TryParse(Environment.GetEnvironmentVariable(name),
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out value);

    // Longest render-target edge the sky is allowed; larger surfaces (maximized on
    // high-DPI) pushed frame time past vsync on this GPU and the animation flickered.
    private const double MaxRenderEdge = 2400.0;

    /// <summary>Renders the sky at reduced resolution on large surfaces and lets the swap chain scale it up.</summary>
    private void UpdateRenderScale()
    {
        if (windowClosed) return; // Window.Content throws once the window is closed
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
        if (windowClosed) return; // Window.Content throws once the window is closed
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
            sky.Daylight, sky.Cloud, sky.Rain, sky.Snow, sky.Fog, sky.Lightning,
            sky.Bolt, sky.BoltSeed, sky.Wind,
            sky.SunProgress, sky.MoonProgress, sky.MoonPhase,
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

    private void OnDayRowClick(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        if (sender is not Microsoft.UI.Xaml.FrameworkElement { DataContext: DayItem day } row) return;
        ViewModel.ToggleDay(day);
        if (!day.IsExpanded) return;
        // the chart adds a few hundred pixels mid-page, and the scroll viewer's own
        // anchoring can carry the viewport away from the row that was clicked; lay the
        // new height out, then scroll the row and its chart back under the pointer
        if (row.Parent is Microsoft.UI.Xaml.FrameworkElement group)
        {
            group.UpdateLayout();
            group.StartBringIntoView(new Microsoft.UI.Xaml.BringIntoViewOptions { AnimationDesired = true });
        }
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
