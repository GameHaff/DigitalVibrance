using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Threading;
using DigitalVibrance.Core;
using DigitalVibrance.Localization;
using DigitalVibrance.Services;
using Microsoft.Win32;

namespace DigitalVibrance.ViewModels;

/// <summary>
/// What the app is currently doing. Kept as an enum rather than a display string so the status
/// dot's colour triggers keep working in every language.
/// </summary>
public enum StatusKind
{
    NoProfiles,
    Unavailable,
    Disabled,
    Preview,
    Active,
    Desktop,
    Waiting,
}

public sealed class MainViewModel : ObservableObject, IDisposable
{
    private readonly AppConfig _config;
    private readonly VibranceEngine _engine;
    private readonly GameWatcher _watcher;
    private readonly StartupLaunchService _startupLaunchService;
    private readonly DispatcherTimer _saveTimer;

    private GameProfile? _selectedGame;
    private bool _livePreview = true;
    private bool _startWithWindows;
    private bool _isLanguageMenuOpen;
    private StatusKind _status = StatusKind.NoProfiles;
    private string? _detailKey;
    private string? _detailText;
    private bool _disposed;

    public MainViewModel()
    {
        _config = ConfigStore.Load();

        // A stored choice wins; otherwise follow the OS on first run.
        Loc.Instance.Use(_config.Language ?? Loc.Instance.DetectSystemCode());
        Loc.Instance.LanguageChanged += OnLanguageChanged;

        ThemeManager.Instance.Use(ThemeManager.Parse(_config.Theme));
        ThemeManager.Instance.ThemeChanged += OnThemeChanged;

        _engine = new VibranceEngine();
        _watcher = new GameWatcher();
        _watcher.Changed += (_, _) => Reevaluate();
        _startupLaunchService = new StartupLaunchService();

        _startWithWindows = _startupLaunchService.IsEnabled();
        if (_config.StartWithWindows != _startWithWindows)
        {
            if (!_startupLaunchService.SetEnabled(_config.StartWithWindows))
            {
                _startWithWindows = _startupLaunchService.IsEnabled();
            }
        }
        if (_config.StartWithWindows != _startWithWindows)
        {
            _config.StartWithWindows = _startWithWindows;
            ScheduleSave();
        }

        Games = new ObservableCollection<GameProfile>(_config.Games);
        foreach (var game in Games)
        {
            game.Icon = IconLoader.Load(game.ExecutablePath);
            Subscribe(game);
        }
        Games.CollectionChanged += OnGamesChanged;

        Desktop.PropertyChanged += OnProfileChanged;

        _saveTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(700),
        };
        _saveTimer.Tick += (_, _) => { _saveTimer.Stop(); SaveNow(); };

        AddGameCommand = new RelayCommand(AddGame);
        RemoveGameCommand = new RelayCommand(p => RemoveGame(p as GameProfile));
        SelectGameCommand = new RelayCommand(p => SelectedGame = p as GameProfile);
        CloseDetailCommand = new RelayCommand(() => SelectedGame = null);
        ResetSelectedCommand = new RelayCommand(() => (SelectedGame?.Color ?? Desktop).ResetToNeutral());
        OpenConfigFolderCommand = new RelayCommand(OpenConfigFolder);
        SetLanguageCommand = new RelayCommand(p => SetLanguage(p as LanguagePack));
        ToggleThemeCommand = new RelayCommand(ToggleTheme);

        Reevaluate();
    }

    public ObservableCollection<GameProfile> Games { get; }

    public RelayCommand AddGameCommand { get; }
    public RelayCommand RemoveGameCommand { get; }
    public RelayCommand SelectGameCommand { get; }
    public RelayCommand CloseDetailCommand { get; }
    public RelayCommand ResetSelectedCommand { get; }
    public RelayCommand OpenConfigFolderCommand { get; }
    public RelayCommand SetLanguageCommand { get; }
    public RelayCommand ToggleThemeCommand { get; }

    // ---------- theme ----------

    /// <summary>Segoe MDL2 glyph on the theme button: shows the theme it will switch to.</summary>
    public string ThemeGlyph => ThemeManager.Instance.ToggleGlyph;

    private void ToggleTheme()
    {
        ThemeManager.Instance.Toggle();
        _config.Theme = ThemeManager.Instance.Current.ToString();
        ScheduleSave();
    }

    private void OnThemeChanged(object? sender, EventArgs e) => Raise(nameof(ThemeGlyph));

    // ---------- language ----------

    public IReadOnlyList<LanguagePack> Languages => Loc.Instance.Available;

    public LanguagePack CurrentLanguage => Loc.Instance.Current;

    /// <summary>Mirrors the whole layout for right-to-left scripts such as Arabic.</summary>
    public FlowDirection FlowDirection =>
        Loc.Instance.IsRightToLeft ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;

    public bool IsLanguageMenuOpen
    {
        get => _isLanguageMenuOpen;
        set => Set(ref _isLanguageMenuOpen, value);
    }

    private void SetLanguage(LanguagePack? pack)
    {
        IsLanguageMenuOpen = false;
        if (pack is null) return;

        Loc.Instance.Use(pack.Code);
        _config.Language = pack.Code;
        ScheduleSave();
    }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        Raise(nameof(CurrentLanguage));
        Raise(nameof(FlowDirection));
        Raise(nameof(StatusText));
        Raise(nameof(ActiveDetail));
    }

    // ---------- state exposed to the view ----------

    public bool MasterEnabled
    {
        get => _config.MasterEnabled;
        set
        {
            if (_config.MasterEnabled == value) return;
            _config.MasterEnabled = value;
            Raise();
            ScheduleSave();
            Reevaluate();
        }
    }

    public bool ApplyToDesktop
    {
        get => _config.ApplyToDesktop;
        set
        {
            if (_config.ApplyToDesktop == value) return;
            _config.ApplyToDesktop = value;
            Raise();
            ScheduleSave();
            Reevaluate();
        }
    }

    public bool StartWithWindows
    {
        get => _startWithWindows;
        set
        {
            if (_startWithWindows == value) return;

            if (_startupLaunchService.SetEnabled(value))
            {
                _startWithWindows = value;
                _config.StartWithWindows = value;
                Raise();
                ScheduleSave();
                return;
            }

            _startWithWindows = _startupLaunchService.IsEnabled();
            Raise();
        }
    }

    public bool StartMinimized
    {
        get => _config.StartMinimized;
        set
        {
            if (_config.StartMinimized == value) return;
            _config.StartMinimized = value;
            Raise();
            ScheduleSave();
        }
    }

    public bool MinimizeToTray
    {
        get => _config.MinimizeToTray;
        set
        {
            if (_config.MinimizeToTray == value) return;
            _config.MinimizeToTray = value;
            Raise();
            ScheduleSave();
        }
    }

    public ColorProfile Desktop => _config.Desktop;

    public GameProfile? SelectedGame
    {
        get => _selectedGame;
        set
        {
            if (ReferenceEquals(_selectedGame, value)) return;
            if (_selectedGame is not null) _selectedGame.IsSelected = false;
            _selectedGame = value;
            if (_selectedGame is not null) _selectedGame.IsSelected = true;
            Raise();
            Raise(nameof(HasSelection));
            Raise(nameof(NoSelection));
            Raise(nameof(EditedProfile));
            Reevaluate();
        }
    }

    public bool HasSelection => _selectedGame is not null;

    public bool NoSelection => _selectedGame is null;

    /// <summary>Whichever profile the sliders are currently bound to.</summary>
    public ColorProfile EditedProfile => _selectedGame?.Color ?? Desktop;

    /// <summary>While on, the selected profile is painted on screen so edits are visible.</summary>
    public bool LivePreview
    {
        get => _livePreview;
        set { if (Set(ref _livePreview, value)) Reevaluate(); }
    }

    public bool HasGames => Games.Count > 0;

    public bool NoGames => Games.Count == 0;

    public bool EngineAvailable => _engine.IsAvailable;

    public string EngineError => _engine.Error ?? "";

    public StatusKind Status
    {
        get => _status;
        private set { if (Set(ref _status, value)) Raise(nameof(StatusText)); }
    }

    public string StatusText => Loc.Instance[_status switch
    {
        StatusKind.Unavailable => "StatusEngineUnavailable",
        StatusKind.Disabled => "StatusDisabled",
        StatusKind.Preview => "StatusPreview",
        StatusKind.Active => "StatusActive",
        StatusKind.Desktop => "StatusDesktop",
        StatusKind.Waiting => "StatusWaiting",
        _ => "StatusNoProfiles",
    }];

    /// <summary>
    /// Extra context next to the status. Sometimes a translated phrase, sometimes a game name —
    /// the key is kept rather than the resolved text so it re-translates on a language switch.
    /// </summary>
    public string ActiveDetail =>
        _detailKey is not null ? Loc.Instance[_detailKey] : _detailText ?? "";

    // ---------- commands ----------

    private void AddGame()
    {
        var dialog = new OpenFileDialog
        {
            Title = Loc.Instance["PickTitle"],
            Filter = Loc.Instance["PickFilter"],
            CheckFileExists = true,
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
        };

        if (dialog.ShowDialog() == true) AddGameFromPath(dialog.FileName);
    }

    /// <summary>Shared by the file picker and by dropping an .exe onto the window.</summary>
    public void AddGameFromPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;

        string key = Path.GetFileName(path).ToLowerInvariant();

        var existing = Games.FirstOrDefault(g => g.MatchKey == key);
        if (existing is not null)
        {
            SelectedGame = existing;
            Show(Loc.Instance.Format("DuplicateBody", existing.Name, existing.ExecutableName),
                Loc.Instance["DuplicateTitle"], MessageBoxImage.Information);
            return;
        }

        var game = new GameProfile
        {
            Name = IconLoader.GuessName(path),
            ExecutablePath = path,
            Icon = IconLoader.Load(path),
        };
        game.Color.Vibrance = 75; // a visible starting point rather than a no-op

        Games.Add(game);
        SelectedGame = game;
    }

    private void RemoveGame(GameProfile? game)
    {
        if (game is null) return;

        var answer = MessageBox.Show(
            Loc.Instance.Format("DeleteBody", game.Name),
            Loc.Instance["DeleteTitle"],
            MessageBoxButton.YesNo, MessageBoxImage.Question,
            MessageBoxResult.No, Loc.Instance.DialogOptions);

        if (answer != MessageBoxResult.Yes) return;

        if (ReferenceEquals(game, _selectedGame)) SelectedGame = null;
        Games.Remove(game);
    }

    private void OpenConfigFolder()
    {
        try
        {
            System.IO.Directory.CreateDirectory(ConfigStore.Directory);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = ConfigStore.Directory,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            Show(ex.Message, Loc.Instance["ErrorTitle"], MessageBoxImage.Warning);
        }
    }

    private static void Show(string text, string caption, MessageBoxImage icon) =>
        MessageBox.Show(text, caption, MessageBoxButton.OK, icon,
            MessageBoxResult.OK, Loc.Instance.DialogOptions);

    // ---------- the decision that drives the screen ----------

    private void Reevaluate()
    {
        if (_disposed) return;

        GameProfile? active = null;
        ColorProfile? target = null;
        StatusKind status;
        string? detailKey = null;
        string? detailText = null;

        if (!_engine.IsAvailable)
        {
            status = StatusKind.Unavailable;
            detailText = _engine.Error ?? "";
        }
        else if (!MasterEnabled)
        {
            status = StatusKind.Disabled;
            detailKey = "StatusDisabledDetail";
        }
        else if (_livePreview && _selectedGame is not null)
        {
            target = _selectedGame.Color;
            status = StatusKind.Preview;
            detailText = _selectedGame.Name;
        }
        else
        {
            active = FindActiveGame();
            if (active is not null)
            {
                target = active.Color;
                status = StatusKind.Active;
                detailText = $"{active.Name} ({active.ExecutableName})";
            }
            else if (ApplyToDesktop && !Desktop.IsNeutral)
            {
                target = Desktop;
                status = StatusKind.Desktop;
            }
            else
            {
                status = HasGames ? StatusKind.Waiting : StatusKind.NoProfiles;
            }
        }

        foreach (var game in Games)
            game.IsActive = ReferenceEquals(game, active);

        Status = status;
        _detailKey = detailKey;
        _detailText = detailText;
        Raise(nameof(ActiveDetail));

        _engine.Apply(target?.Build() ?? ColorMatrix.Identity);
    }

    /// <summary>
    /// Foreground wins over merely-running, so a profile set to "while running" still yields
    /// to whatever game you actually alt-tabbed into.
    /// </summary>
    private GameProfile? FindActiveGame()
    {
        string? foreground = _watcher.ForegroundExecutable;

        if (!string.IsNullOrEmpty(foreground))
        {
            var focused = Games.FirstOrDefault(g => g.Enabled && g.MatchKey == foreground);
            if (focused is not null) return focused;
        }

        return Games.FirstOrDefault(g =>
            g.Enabled && g.Mode == ActivationMode.WhileRunning && _watcher.IsRunning(g.MatchKey));
    }

    // ---------- change tracking ----------

    private void OnGamesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null)
            foreach (GameProfile game in e.OldItems) Unsubscribe(game);

        if (e.NewItems is not null)
            foreach (GameProfile game in e.NewItems) Subscribe(game);

        _config.Games = Games.ToList();
        Raise(nameof(HasGames));
        Raise(nameof(NoGames));
        ScheduleSave();
        Reevaluate();
    }

    private void Subscribe(GameProfile game)
    {
        game.PropertyChanged += OnGamePropertyChanged;
        game.Color.PropertyChanged += OnProfileChanged;
    }

    private void Unsubscribe(GameProfile game)
    {
        game.PropertyChanged -= OnGamePropertyChanged;
        game.Color.PropertyChanged -= OnProfileChanged;
    }

    private void OnGamePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Selection and activity are view state, not settings — they must not trigger a save.
        if (e.PropertyName is nameof(GameProfile.IsSelected)
            or nameof(GameProfile.IsActive)
            or nameof(GameProfile.Icon))
            return;

        ScheduleSave();

        if (e.PropertyName is nameof(GameProfile.Enabled)
            or nameof(GameProfile.Mode)
            or nameof(GameProfile.ExecutablePath))
            Reevaluate();
    }

    private void OnProfileChanged(object? sender, PropertyChangedEventArgs e)
    {
        ScheduleSave();
        Reevaluate();
    }

    private void ScheduleSave()
    {
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    public void SaveNow()
    {
        _config.Games = Games.ToList();
        ConfigStore.Save(_config);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        Loc.Instance.LanguageChanged -= OnLanguageChanged;
        ThemeManager.Instance.ThemeChanged -= OnThemeChanged;

        _saveTimer.Stop();
        SaveNow();

        _watcher.Dispose();
        _engine.Dispose(); // resets the screen to identity on the way out
    }
}
