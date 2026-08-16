using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Runtime.InteropServices;
using DigitalVibrance.Localization;
using DigitalVibrance.ViewModels;
using Drawing = System.Drawing;
using WinForms = System.Windows.Forms;

namespace DigitalVibrance.Views;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;
    private readonly WinForms.NotifyIcon _tray;
    private readonly WinForms.ToolStripMenuItem _trayShowItem;
    private readonly WinForms.ToolStripMenuItem _trayExitItem;
    private bool _exiting;

    [DllImport("user32.dll")]
    private static extern int SetWindowRgn(IntPtr hWnd, IntPtr hRgn, bool bRedraw);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateRoundRectRgn(
        int nLeftRect,
        int nTopRect,
        int nRightRect,
        int nBottomRect,
        int nWidthEllipse,
        int nHeightEllipse);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr hObject);

    public MainWindow()
    {
        InitializeComponent();

        _vm = new MainViewModel();
        DataContext = _vm;

        _trayShowItem = new WinForms.ToolStripMenuItem();
        _trayExitItem = new WinForms.ToolStripMenuItem();
        _tray = BuildTrayIcon();
        var icon = LoadWindowIcon();
        if (icon is not null)
        {
            BrandIcon.Source = icon;
            Icon = icon;
            BrandGlyphFallback.Visibility = Visibility.Collapsed;
            BrandIcon.Visibility = Visibility.Visible;
        }
        else
        {
            BrandGlyphFallback.Visibility = Visibility.Visible;
            BrandIcon.Visibility = Visibility.Collapsed;
        }

        // The tray menu lives outside WPF's binding system, so it is refreshed by hand.
        Loc.Instance.LanguageChanged += OnLanguageChanged;
        ApplyTrayText();

        if (!_vm.EngineAvailable)
        {
        }

        Loaded += OnLoaded;
        SizeChanged += OnWindowSizeChanged;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        ApplyWindowRegion();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (!_vm.EngineAvailable)
        {
            MessageBox.Show(this,
                Loc.Instance["EngineFailedBody"] + "\n\n" + _vm.EngineError,
                "Digital Vibrance", MessageBoxButton.OK, MessageBoxImage.Warning,
                MessageBoxResult.OK, Loc.Instance.DialogOptions);
        }

        ApplyStartupVisibility();
        ApplyWindowRegion();
    }

    private void OnWindowSizeChanged(object? sender, SizeChangedEventArgs e) => ApplyWindowRegion();

    private void ApplyWindowRegion()
    {
        if (!IsLoaded) return;

        var helper = new WindowInteropHelper(this);
        var hWnd = helper.Handle;
        if (hWnd == IntPtr.Zero) return;

        var dpi = VisualTreeHelper.GetDpi(this);
        var width = (int)Math.Round(ActualWidth * dpi.DpiScaleX);
        var height = (int)Math.Round(ActualHeight * dpi.DpiScaleY);
        var radius = (int)Math.Round(16.0 * dpi.DpiScaleX);

        if (width <= 0 || height <= 0 || radius <= 0) return;

        IntPtr region = CreateRoundRectRgn(0, 0, width + 1, height + 1, radius * 2, radius * 2);
        if (region == IntPtr.Zero) return;

        SetWindowRgn(hWnd, region, true);
        DeleteObject(region);
    }

    // ---------- tray ----------

    private WinForms.NotifyIcon BuildTrayIcon()
    {
        _trayShowItem.Click += (_, _) => RestoreFromTray();
        _trayExitItem.Click += (_, _) => ExitApplication();

        var menu = new WinForms.ContextMenuStrip();
        menu.Items.Add(_trayShowItem);
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add(_trayExitItem);

        var icon = new WinForms.NotifyIcon
        {
            Icon = LoadAppIcon(),
            Text = "Digital Vibrance",
            Visible = true,
            ContextMenuStrip = menu,
        };
        icon.DoubleClick += (_, _) => RestoreFromTray();
        return icon;
    }

    private void OnLanguageChanged(object? sender, EventArgs e) => ApplyTrayText();

    private void ApplyTrayText()
    {
        _trayShowItem.Text = Loc.Instance["TrayShow"];
        _trayExitItem.Text = Loc.Instance["TrayExit"];
    }

    private static Drawing.Icon LoadAppIcon()
    {
        try
        {
            string? exe = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(exe))
            {
                var extracted = Drawing.Icon.ExtractAssociatedIcon(exe);
                if (extracted is not null) return extracted;
            }
        }
        catch (Exception ex) when (ex is IOException or ArgumentException)
        {
            // fall through to the stock icon
        }

        return Drawing.SystemIcons.Application;
    }

    private static ImageSource? LoadWindowIcon()
    {
        try
        {
            if (TryGetEmbeddedIcon() is { } embedded)
            {
                return embedded;
            }

            var iconPath = TryGetPackagedIconPath();
            if (iconPath is not null && TryGetIconBitmap(iconPath, 48) is { } bitmapIcon)
            {
                return bitmapIcon;
            }

            using var icon = LoadAppIcon();
            return Imaging.CreateBitmapSourceFromHIcon(
                icon.Handle,
                Int32Rect.Empty,
                BitmapSizeOptions.FromWidthAndHeight(32, 32));
        }
        catch
        {
            return null;
        }
    }

    private void OnBrandIconFailed(object sender, ExceptionRoutedEventArgs e)
    {
        BrandGlyphFallback.Visibility = Visibility.Visible;
        BrandIcon.Visibility = Visibility.Collapsed;
    }

    private static ImageSource? TryGetIconBitmap(string iconPath, int size)
    {
        try
        {
            using var icon = new Drawing.Icon(iconPath, size, size);
            var bitmap = Imaging.CreateBitmapSourceFromHIcon(
                icon.Handle,
                Int32Rect.Empty,
                BitmapSizeOptions.FromWidthAndHeight(size, size));
            bitmap.Freeze();
            return bitmap;
        }
        catch
        {
            return null;
        }
    }

    private static ImageSource? TryGetEmbeddedIcon()
    {
        try
        {
            var uri = new Uri("pack://application:,,,/DigitalVibrance;component/Assets/app.ico");
            using var stream = Application.GetResourceStream(uri)?.Stream;
            if (stream is null) return null;

            using var icon = new Drawing.Icon(stream);
            var bitmap = Imaging.CreateBitmapSourceFromHIcon(
                icon.Handle,
                Int32Rect.Empty,
                BitmapSizeOptions.FromWidthAndHeight(48, 48));
            bitmap.Freeze();
            return bitmap;
        }
        catch
        {
            return null;
        }
    }

    private static string? TryGetPackagedIconPath()
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico"),
            Path.Combine(AppContext.BaseDirectory, "app.ico"),
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets", "app.ico"),
            Path.Combine(Directory.GetCurrentDirectory(), "Assets", "app.ico"),
            Path.Combine(Directory.GetCurrentDirectory(), "src", "DigitalVibrance", "Assets", "app.ico"),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "Assets", "app.ico")),
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "DigitalVibrance", "Assets", "app.ico"),
        };

        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate)) return candidate;
        }

        return null;
    }

    private void RestoreFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    private void ExitApplication()
    {
        _exiting = true;
        Close();
    }

    // ---------- window chrome ----------
    private void OnTitleBarMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) return;
        try
        {
            DragMove();
        }
        catch
        {
            // Ignore cases where drag is not supported (e.g. while minimized).
        }
    }

    private void OnMinimize(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_exiting && _vm.MinimizeToTray)
        {
            e.Cancel = true;
            Hide();
            return;
        }

        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        Loc.Instance.LanguageChanged -= OnLanguageChanged;
        _tray.Visible = false;
        _tray.Dispose();
        _vm.Dispose(); // saves settings and resets the screen to neutral
        base.OnClosed(e);
        Application.Current.Shutdown();
    }

    // ---------- drag & drop ----------

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = GetDroppedExecutables(e).Length > 0 ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        foreach (string path in GetDroppedExecutables(e))
            _vm.AddGameFromPath(path);

        e.Handled = true;
    }

    private static string[] GetDroppedExecutables(DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return Array.Empty<string>();
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths) return Array.Empty<string>();

        return paths
            .Where(p => string.Equals(Path.GetExtension(p), ".exe", StringComparison.OrdinalIgnoreCase))
            .ToArray();
    }

    private void ApplyStartupVisibility()
    {
        if (!_vm.StartMinimized) return;

        if (_vm.MinimizeToTray)
        {
            Hide();
            return;
        }

        WindowState = WindowState.Minimized;
    }

}
