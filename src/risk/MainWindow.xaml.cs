using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.Win32;
using System.Collections.ObjectModel;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Text.Json;
using Windows.Storage.Streams;
using Windows.Media.Audio;
using Windows.Media.Core;
using Windows.Storage.Pickers;
using Risk.App.Services;
using WinRT.Interop;
using System.Runtime.InteropServices;

namespace Risk.App;

public sealed partial class MainWindow : Window
{
    const string SharedDiscordApplicationId = "1540121277036109854";
    readonly EqualizedAudioPlayer _player = new();
    readonly YouTubeMusicService _yt = new();
    readonly SoundCloudService _sc = new();
    readonly SpotifyPlaylistService _spotify = new();
    readonly DiscordPresenceService _discord = new();
    static readonly HttpClient ArtworkHttp = CreateArtworkHttpClient();
    static readonly SemaphoreSlim ArtworkDownloadGate = new(8, 8);
    static readonly ConcurrentDictionary<string, byte[]> ArtworkBytes = new(StringComparer.OrdinalIgnoreCase);
    readonly ConcurrentDictionary<TrackItem, byte> _artworkLookupAttempted = new();

    readonly RangeObservableCollection<TrackItem> _results = new();
    readonly ObservableCollection<TrackItem> _library = new();
    readonly RangeObservableCollection<TrackItem> _profileTracks = new();
    readonly ObservableCollection<PlaylistItem> _playlists = new();
    readonly Dictionary<string, (string Name, string AvatarUrl, string BannerUrl, List<TrackItem> Tracks)> _artistProfileCache = new(StringComparer.OrdinalIgnoreCase);
    List<TrackItem> _allProfileTracks = new();
    PlaylistItem? _selectedPlaylist;
    FrameworkElement? _pageBeforeExpanded;
    bool _isExpandedPlayer;
    Microsoft.UI.Dispatching.DispatcherQueueTimer? _visualizerTimer;
    readonly Dictionary<ScrollViewer, Microsoft.UI.Dispatching.DispatcherQueueTimer> _carouselScrollTimers = new();
    double[] _latestVisualizerLevels = new double[42];
    double[] _displayedVisualizerLevels = new double[42];
    bool _homeScrollThumbDragging;
    double _homeScrollThumbGrabOffset;

    List<TrackItem> _queue = new();
    List<TrackItem>? _unshuffledQueue;
    int _index = -1;
    TrackItem? _current;
    bool _shuffleEnabled;
    int _repeatMode;
    bool _automaticFadeStarted;
    bool _scrubbing, _updatingSeek, _muted;
    double _lastVolume = 75;
    double _savedVolume = 75;
    int _loadToken;
    int _profileLoadToken;
    string _downloadFolder = "";
    readonly string _dataDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Risk");
    double[] _savedEqualizerGains = Array.Empty<double>();
    string _discordApplicationId = SharedDiscordApplicationId;
    bool _discordActivityEnabled = true;
    bool _discordDurationMissing;
    DateTimeOffset _discordDurationRetryAt;
    bool _crossfadeEnabled;
    double _crossfadeSeconds = 3;
    bool _searchYouTubeMusic = true;
    bool _searchSoundCloud = true;
    bool _rememberVolume;
    bool _startWithWindows;
    bool _autoplaySimilarTracks = true;
    bool _normalizeVolume = true;
    bool _organizeDownloadsByArtist;
    int _searchResultCount = 25;
    int _mp3Bitrate = 320;
    string _preferredDownloadFormat = "ask";
    string _normalizationLevel = "Normal";
    string _streamingQuality = "Auto";
    Windows.UI.Color _themeAccent = Windows.UI.Color.FromArgb(255, 125, 211, 252);
    bool _lightTheme;
    bool _initializing = true;
    Microsoft.UI.Dispatching.DispatcherQueueTimer? _progressTimer;
    Microsoft.UI.Dispatching.DispatcherQueueTimer? _discordStatusTimer;
    CancellationTokenSource? _searchCancellation;
    IntPtr _trayIconHandle;
    IntPtr _windowHandle;
    bool _exitRequested;
    TrayWindowProc? _trayWindowProc;
    const uint TrayCallbackMessage = 0x8001;
    const uint WmLButtonUp = 0x0202;
    const uint WmLButtonDoubleClick = 0x0203;
    const uint WmRButtonUp = 0x0205;

    public MainWindow()
    {
        InitializeComponent();
        Canvas.SetZIndex(SidebarRail, 1);
        Title = "Risk";
        TryApplyAcrylicBackdrop();
        InitializeSystemTray();
        AppWindow.Closing += (_, e) =>
        {
            if (_exitRequested) return;
            e.Cancel = true;
            ShowWindow(_windowHandle, 0);
            ShowTrayNotice("Risk is hidden in the system tray.");
        };
        try
        {
            var localAssets = Path.Combine(AppContext.BaseDirectory, "Assets");
            var sharedAssets = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "Assets"));
            var assets = Directory.Exists(localAssets) ? localAssets : sharedAssets;
            var icon = Path.Combine(assets, "app-icon-v2.ico");
            var logo = Path.Combine(assets, "app-icon.png");
            if (File.Exists(icon)) AppWindow.SetIcon(icon);
            if (File.Exists(logo)) AppLogo.Source = new BitmapImage(new Uri(logo));
        }
        catch { }

        ResultsList.ItemsSource = _results;
        LibraryList.ItemsSource = _library;
        ProfileTracksList.ItemsSource = _profileTracks;
        PlaylistList.ItemsSource = _playlists;
        _playlists.CollectionChanged += (_, _) => RefreshSidebarPlaylists();
        _player.MediaEnded += () => DispatcherQueue.TryEnqueue(HandleMediaEnded);
        _player.PlaybackFailed += message => DispatcherQueue.TryEnqueue(() => StatusText.Text = "Playback failed: " + message);
        _player.AudioLevelsChanged += levels => Interlocked.Exchange(ref _latestVisualizerLevels, levels);
        _player.AudioAnalysisFailed += message => DispatcherQueue.TryEnqueue(() =>
        {
            VisualizerStatus.Text = "Visualizer unavailable: " + message;
            VisualizerStatus.Visibility = Visibility.Visible;
        });
        Closed += async (_, _) =>
        {
            _searchCancellation?.Cancel();
            await _discord.ClearAsync();
            _progressTimer?.Stop();
            _discordStatusTimer?.Stop();
            _visualizerTimer?.Stop();
            foreach (var timer in _carouselScrollTimers.Values) timer.Stop();
            _carouselScrollTimers.Clear();
            _discord.Dispose();
            _player.Dispose();
            RemoveSystemTrayIcon();
        };

        MigrateLegacyDataDirectory();
        LoadSettings();
        HydrateArtwork(_playlists.SelectMany(playlist => playlist.Tracks));
        DiscordActivityToggle.IsOn = _discordActivityEnabled;
        UpdatePlaybackModeButtons();
        _initializing = false;
        ApplyVolume(VolumeSlider.Value);
        _progressTimer = DispatcherQueue.CreateTimer();
        _progressTimer.Interval = TimeSpan.FromMilliseconds(500);
        _progressTimer.IsRepeating = true;
        _progressTimer.Tick += (_, _) => UpdateProgress();
        _progressTimer.Start();
        BuildVisualizerBars();
        HomeDashboard.ViewChanged += (_, _) => UpdateHomeScrollThumb();
        HomeDashboard.SizeChanged += (_, _) => UpdateHomeScrollThumb();
        HomeScrollRail.SizeChanged += (_, _) => UpdateHomeScrollThumb();
        UpdateHomeScrollThumb();
        _visualizerTimer = DispatcherQueue.CreateTimer();
        _visualizerTimer.Interval = TimeSpan.FromMilliseconds(16);
        _visualizerTimer.IsRepeating = true;
        _visualizerTimer.Tick += (_, _) => UpdateVisualizer();
        _ = InitializeAudioAsync();
        ShowPage(HomePage);

        ResizeWindow();
    }

    void InitializeSystemTray()
    {
        try
        {
            _windowHandle = WindowNative.GetWindowHandle(this);
            _trayWindowProc = TrayWindowProcHandler;
            SetWindowSubclass(_windowHandle, _trayWindowProc, (UIntPtr)1, UIntPtr.Zero);
            var localAssets = Path.Combine(AppContext.BaseDirectory, "Assets");
            var sharedAssets = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "Assets"));
            var assets = Directory.Exists(localAssets) ? localAssets : sharedAssets;
            var iconPath = Path.Combine(assets, "app-icon-v2.ico");
            if (File.Exists(iconPath))
                _trayIconHandle = LoadImage(IntPtr.Zero, iconPath, 1, 0, 0, 0x10 | 0x40);
            var data = new NotifyIconData
            {
                Size = Marshal.SizeOf<NotifyIconData>(),
                Window = _windowHandle,
                Id = 1,
                Flags = 1 | 2 | 4,
                CallbackMessage = TrayCallbackMessage,
                Icon = _trayIconHandle,
                Tip = "Risk Music Player",
                Info = "",
                InfoTitle = "",
                InfoTimeoutOrVersion = 0,
                InfoFlags = 0
            };
            Shell_NotifyIcon(0, ref data);
        }
        catch
        {
            // Keep the player usable if Windows refuses to create a notification-area icon.
        }
    }

    void RestoreFromSystemTray()
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            ShowWindow(_windowHandle, 5);
            Activate();
        });
    }

    void ExitFromSystemTray()
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            _exitRequested = true;
            Close();
        });
    }

    void ShowTrayNotice(string message)
    {
        if (_windowHandle == IntPtr.Zero) return;
        var data = CreateTrayData(1 | 2 | 4 | 16);
        data.InfoTitle = "Risk is still running";
        data.Info = message;
        data.InfoTimeoutOrVersion = 4500;
        data.InfoFlags = 1;
        Shell_NotifyIcon(1, ref data);
    }

    void RemoveSystemTrayIcon()
    {
        if (_windowHandle != IntPtr.Zero)
        {
            var data = CreateTrayData(0);
            Shell_NotifyIcon(2, ref data);
            RemoveWindowSubclass(_windowHandle, _trayWindowProc!, (UIntPtr)1);
        }
        if (_trayIconHandle != IntPtr.Zero) DestroyIcon(_trayIconHandle);
        _trayIconHandle = IntPtr.Zero;
    }

    NotifyIconData CreateTrayData(uint flags) => new()
    {
        Size = Marshal.SizeOf<NotifyIconData>(),
        Window = _windowHandle,
        Id = 1,
        Flags = flags,
        CallbackMessage = TrayCallbackMessage,
        Icon = _trayIconHandle,
        Tip = "Risk Music Player",
        Info = "",
        InfoTitle = "",
        InfoTimeoutOrVersion = 0,
        InfoFlags = 0
    };

    IntPtr TrayWindowProcHandler(IntPtr hwnd, uint message, UIntPtr wParam, IntPtr lParam,
        UIntPtr subclassId, UIntPtr referenceData)
    {
        if (message == TrayCallbackMessage)
        {
            var notification = unchecked((uint)lParam.ToInt64());
            if (notification is WmLButtonUp or WmLButtonDoubleClick or 0x0400 or 0x0401)
                RestoreFromSystemTray();
            else if (notification == WmRButtonUp)
                ShowTrayContextMenu();
        }
        return DefSubclassProc(hwnd, message, wParam, lParam);
    }

    void ShowTrayContextMenu()
    {
        var menu = CreatePopupMenu();
        if (menu == IntPtr.Zero) return;
        AppendMenu(menu, 0, 1, "Open Risk");
        AppendMenu(menu, 0x800, 0, null);
        AppendMenu(menu, 0, 2, "Exit Risk");
        GetCursorPos(out var point);
        SetForegroundWindow(_windowHandle);
        var command = TrackPopupMenuEx(menu, 0x0100 | 0x0002, point.X, point.Y, _windowHandle, IntPtr.Zero);
        DestroyMenu(menu);
        PostMessage(_windowHandle, 0, IntPtr.Zero, IntPtr.Zero);
        if (command == 1) RestoreFromSystemTray();
        else if (command == 2) ExitFromSystemTray();
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct NotifyIconData
    {
        public int Size;
        public IntPtr Window;
        public uint Id;
        public uint Flags;
        public uint CallbackMessage;
        public IntPtr Icon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Tip;
        public uint State;
        public uint StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Info;
        public uint InfoTimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string InfoTitle;
        public uint InfoFlags;
        public Guid GuidItem;
        public IntPtr BalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct TrayPoint { public int X; public int Y; }

    delegate IntPtr TrayWindowProc(IntPtr hwnd, uint message, UIntPtr wParam, IntPtr lParam,
        UIntPtr subclassId, UIntPtr referenceData);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern bool Shell_NotifyIcon(uint message, ref NotifyIconData data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr LoadImage(IntPtr instance, string name, uint type, int width, int height, uint flags);
    [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr icon);
    [DllImport("comctl32.dll")] static extern bool SetWindowSubclass(IntPtr hwnd, TrayWindowProc callback, UIntPtr subclassId, UIntPtr referenceData);
    [DllImport("comctl32.dll")] static extern bool RemoveWindowSubclass(IntPtr hwnd, TrayWindowProc callback, UIntPtr subclassId);
    [DllImport("comctl32.dll")] static extern IntPtr DefSubclassProc(IntPtr hwnd, uint message, UIntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr CreatePopupMenu();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool AppendMenu(IntPtr menu, uint flags, nuint id, string? text);
    [DllImport("user32.dll")] static extern bool DestroyMenu(IntPtr menu);
    [DllImport("user32.dll")] static extern int TrackPopupMenuEx(IntPtr menu, uint flags, int x, int y, IntPtr hwnd, IntPtr parameters);
    [DllImport("user32.dll")] static extern bool GetCursorPos(out TrayPoint point);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] static extern bool PostMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr hwnd, int command);

    void TryApplyAcrylicBackdrop()
    {
        // The content root remains transparent, allowing the OS acrylic blur to show.
        try { SystemBackdrop = new DesktopAcrylicBackdrop(); } catch { }
    }

    async Task InitializeAudioAsync()
    {
        try
        {
            await _player.InitializeAsync();
            _player.SetBandGains(_savedEqualizerGains);
            BuildEqualizerBands();
            _player.Volume = _muted ? 0 : Math.Pow(VolumeSlider.Value / 100.0, 2.0);
            EqualizerStatus.Text = "Equalizer is active for all playback.";
        }
        catch { EqualizerStatus.Text = "Audio effects are unavailable on this device."; }
    }

    void ResizeWindow()
    {
        try
        {
            var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary);
            AppWindow.Resize(new Windows.Graphics.SizeInt32
            {
                Width = Math.Min(1280, (int)(area.WorkArea.Width * 0.8)),
                Height = Math.Min(820, (int)(area.WorkArea.Height * 0.85))
            });
        }
        catch { }
    }

    void Root_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        var width = e.NewSize.Width;
        // The sidebar consumes 220 px in the normal window layout. Switch the
        // player to two rows before the remaining width becomes too small for
        // artwork/title, transport controls, and volume to sit side by side.
        var compact = width < 1120;
        var narrow = width < 680;
        Root.ColumnDefinitions[0].Width = new GridLength(narrow ? 60 : width < 900 ? 76 : 220);
        PlayerSidebarSpacer.Width = Root.ColumnDefinitions[0].Width;
        SearchHeader.Margin = width < 760 ? new Thickness(14, 12, 14, 8) : new Thickness(28, 20, 28, 12);
        ContentArea.Margin = width < 760 ? new Thickness(14, 0, 14, 0) : new Thickness(28, 0, 28, 0);

        LibraryPage.ColumnDefinitions[1].Width = compact ? new GridLength(0) : new GridLength(320);
        LibraryPage.RowDefinitions[1].Height = compact ? new GridLength(210) : new GridLength(0);
        Grid.SetColumn(LibraryTracksPanel, 0);
        Grid.SetColumn(LibraryPlaylistsPanel, compact ? 0 : 1);
        Grid.SetRow(LibraryTracksPanel, 0);
        Grid.SetRow(LibraryPlaylistsPanel, compact ? 1 : 0);
        LibraryTracksPanel.Margin = compact ? new Thickness(0, 0, 0, 12) : new Thickness(0, 0, 20, 0);

        var showPlaylistCover = width >= 860;
        PlaylistDetailHeaderGrid.ColumnDefinitions[1].Width = showPlaylistCover ? new GridLength(300) : new GridLength(0);
        PlaylistDetailCoverBorder.Visibility = showPlaylistCover ? Visibility.Visible : Visibility.Collapsed;
        PlaylistDetailHeader.MinHeight = showPlaylistCover ? 300 : 0;
        PlaylistDetailHeader.Padding = width < 680 ? new Thickness(10) : new Thickness(16);
        var stackPlaylistActions = width < 760;
        PlaylistDetailFooter.RowDefinitions[1].Height = stackPlaylistActions ? GridLength.Auto : new GridLength(0);
        Grid.SetRow(PlaylistDetailActions, stackPlaylistActions ? 1 : 0);
        Grid.SetColumn(PlaylistDetailActions, stackPlaylistActions ? 0 : 1);
        Grid.SetColumnSpan(PlaylistDetailActions, stackPlaylistActions ? 2 : 1);
        PlaylistDetailActions.Orientation = stackPlaylistActions ? Orientation.Vertical : Orientation.Horizontal;
        PlaylistPlaybackControls.HorizontalAlignment = stackPlaylistActions ? HorizontalAlignment.Left : HorizontalAlignment.Stretch;

        PlayerLayout.ColumnSpacing = compact ? 12 : 20;
        PlayerLayout.RowDefinitions[1].Height = compact ? GridLength.Auto : new GridLength(0);
        if (compact)
        {
            Grid.SetRow(NowPlayingPanel, 0);
            Grid.SetColumn(NowPlayingPanel, 0);
            Grid.SetRow(VolumePanel, 0);
            Grid.SetColumn(VolumePanel, 1);
            Grid.SetRow(TransportPanel, 1);
            Grid.SetColumn(TransportPanel, 0);
            Grid.SetColumnSpan(TransportPanel, 2);
            VolumeSlider.Width = narrow ? 76 : 100;
            TransportPanel.Width = Math.Clamp(width - Root.ColumnDefinitions[0].ActualWidth -
                (width < 760 ? 28 : 56) - 48 - 16, 280, 380);
        }
        else
        {
            Grid.SetRow(NowPlayingPanel, 0);
            Grid.SetColumn(NowPlayingPanel, 0);
            Grid.SetRow(TransportPanel, 0);
            Grid.SetColumn(TransportPanel, 1);
            Grid.SetColumnSpan(TransportPanel, 1);
            Grid.SetRow(VolumePanel, 0);
            Grid.SetColumn(VolumePanel, 2);
            VolumeSlider.Width = 110;
            TransportPanel.Width = 380;
        }
    }

    // ================= NAV =================

    void ShowPage(FrameworkElement page)
    {
        if (_isExpandedPlayer && page != ExpandedPlayerPage)
        {
            _isExpandedPlayer = false;
            RestoreExpandedPlayerLayout();
            _pageBeforeExpanded = null;
        }
        HomePage.Visibility = page == HomePage ? Visibility.Visible : Visibility.Collapsed;
        LibraryPage.Visibility = page == LibraryPage ? Visibility.Visible : Visibility.Collapsed;
        SettingsPage.Visibility = page == SettingsPage ? Visibility.Visible : Visibility.Collapsed;
        ProfilePage.Visibility = page == ProfilePage ? Visibility.Visible : Visibility.Collapsed;
        ExpandedPlayerPage.Visibility = page == ExpandedPlayerPage ? Visibility.Visible : Visibility.Collapsed;
        EqualizerPage.Visibility = page == EqualizerPage ? Visibility.Visible : Visibility.Collapsed;
        PlaylistDetailPage.Visibility = page == PlaylistDetailPage ? Visibility.Visible : Visibility.Collapsed;

        SettingsNav.IsChecked = page == SettingsPage;
        EqualizerNav.IsChecked = page == EqualizerPage;
        HomeNav.IsChecked = page == HomePage || page == ProfilePage || (page == ExpandedPlayerPage && _pageBeforeExpanded == HomePage);
        LibraryNav.IsChecked = page == LibraryPage || page == PlaylistDetailPage;
        UpdateNavigationTheme();

        AnimateNavigationElement(page);
    }

    void AnimateNavigationElement(FrameworkElement element)
    {
        element.Opacity = 0;
        var slide = new TranslateTransform { Y = 9 };
        element.RenderTransform = slide;
        var transition = new Storyboard();
        var fade = new DoubleAnimation
        {
            From = 0,
            To = 1,
            Duration = TimeSpan.FromMilliseconds(210),
            EnableDependentAnimation = true
        };
        var move = new DoubleAnimation
        {
            From = 9,
            To = 0,
            Duration = TimeSpan.FromMilliseconds(240),
            EnableDependentAnimation = true
        };
        Storyboard.SetTarget(fade, element);
        Storyboard.SetTargetProperty(fade, "Opacity");
        Storyboard.SetTarget(move, slide);
        Storyboard.SetTargetProperty(move, "Y");
        transition.Children.Add(fade);
        transition.Children.Add(move);
        transition.Begin();
    }

    void SettingsTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SettingsTabs.SelectedItem is TabViewItem { Content: FrameworkElement content })
            AnimateNavigationElement(content);
    }

    void HomeNav_Click(object sender, RoutedEventArgs e)
    {
        HomeDashboard.Visibility = Visibility.Visible;
        HomeHeading.Visibility = Visibility.Collapsed;
        ResultsList.Visibility = Visibility.Collapsed;
        ShowPage(HomePage);
    }
    void LibraryNav_Click(object sender, RoutedEventArgs e) => ShowPage(LibraryPage);
    void SettingsNav_Click(object sender, RoutedEventArgs e) => ShowPage(SettingsPage);
    void EqualizerNav_Click(object sender, RoutedEventArgs e) => ShowPage(EqualizerPage);

    void RefreshSidebarPlaylists()
    {
        if (SidebarPlaylistsStack is null) return;
        SidebarPlaylistsStack.Children.Clear();
        BuildHomeDashboard();
        foreach (var playlist in _playlists
                     .OrderByDescending(item => string.Equals(item.Name, "Favorites", StringComparison.OrdinalIgnoreCase))
                     .Take(8))
        {
            var isFavorites = string.Equals(playlist.Name, "Favorites", StringComparison.OrdinalIgnoreCase);
            var surface = new Border
            {
                Width = 36,
                Height = 36,
                CornerRadius = new CornerRadius(9),
                BorderThickness = new Thickness(isFavorites ? 1 : 0),
                BorderBrush = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(36, 255, 255, 255)),
                Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(30, 255, 255, 255))
            };

            if (playlist.Cover is not null)
            {
                surface.Child = new Image { Source = playlist.Cover, Stretch = Stretch.UniformToFill };
            }
            else if (isFavorites)
            {
                surface.Child = new TextBlock
                {
                    Text = "♡",
                    FontSize = 20,
                    Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(_lightTheme
                        ? Windows.UI.Color.FromArgb(255, 55, 55, 60)
                        : Windows.UI.Color.FromArgb(255, 238, 238, 242)),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                };
            }
            else
            {
                surface.Child = new FontIcon
                {
                    Glyph = "\uE8D6",
                    FontSize = 16,
                    Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(_lightTheme
                        ? Windows.UI.Color.FromArgb(255, 70, 70, 76)
                        : Windows.UI.Color.FromArgb(255, 228, 228, 232)),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                };
            }

            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 2 };
            text.Children.Add(new TextBlock
            {
                Text = playlist.Name,
                FontSize = 13,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxWidth = 132
            });
            text.Children.Add(new TextBlock
            {
                Text = $"Playlist · {playlist.Tracks.Count} songs",
                FontSize = 11,
                Opacity = 0.65,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxWidth = 132
            });
            var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
            content.Children.Add(surface);
            content.Children.Add(text);
            var button = new Button
            {
                Width = 196,
                Height = 58,
                Padding = new Thickness(6),
                Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0)),
                BorderThickness = new Thickness(0),
                CornerRadius = new CornerRadius(8),
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Content = content,
                DataContext = playlist
            };
            ToolTipService.SetToolTip(button, playlist.Name);
            button.Click += SidebarPlaylistButton_Click;
            SidebarPlaylistsStack.Children.Add(button);
        }
    }

    void BuildHomeDashboard()
    {
        if (HomeQuickLinks is null || HomePlaylistCards is null || HomeRecentTracks is null) return;
        HomeQuickLinks.Children.Clear();
        HomePlaylistCards.Children.Clear();
        HomeRecentTracks.Children.Clear();
        HomeWelcome.Text = DateTime.Now.Hour switch
        {
            < 5 => "Welcome back",
            < 12 => "Good morning",
            < 18 => "Good afternoon",
            < 22 => "Good evening",
            _ => "Welcome back"
        };

        var playlists = _playlists
            .OrderByDescending(item => string.Equals(item.Name, "Favorites", StringComparison.OrdinalIgnoreCase))
            .ToList();
        foreach (var playlist in playlists.Take(8))
        {
            var image = CreatePlaylistImage(playlist, 56);
            var title = new TextBlock
            {
                Text = playlist.Name,
                FontSize = 14,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center
            };
            var row = new Grid { ColumnSpacing = 12 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(56) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.Children.Add(image);
            Grid.SetColumn(title, 1);
            row.Children.Add(title);
            var quick = new Button
            {
                Width = 272, Height = 58, Padding = new Thickness(0),
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(38, 255, 255, 255)),
                BorderThickness = new Thickness(0), CornerRadius = new CornerRadius(8),
                Content = row, DataContext = playlist
            };
            quick.Click += SidebarPlaylistButton_Click;
            HomeQuickLinks.Children.Add(quick);

            var cover = CreatePlaylistImage(playlist, 150);
            var playlistTitle = new TextBlock
            {
                Text = playlist.Name, FontSize = 15, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 10, 0, 2)
            };
            var description = new TextBlock
            {
                Text = $"Playlist · {playlist.Tracks.Count} songs", FontSize = 12, Opacity = 0.7,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            var cardContent = new StackPanel { Width = 150 };
            cardContent.Children.Add(cover);
            cardContent.Children.Add(playlistTitle);
            cardContent.Children.Add(description);
            var card = new Button
            {
                Width = 174, Height = 222, Padding = new Thickness(12),
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(22, 255, 255, 255)),
                BorderThickness = new Thickness(0), CornerRadius = new CornerRadius(10),
                Content = cardContent, DataContext = playlist
            };
            card.Click += SidebarPlaylistButton_Click;
            HomePlaylistCards.Children.Add(card);
        }

        var recent = playlists.SelectMany(item => item.Tracks)
            .Where(track => !string.IsNullOrWhiteSpace(track.Title))
            .GroupBy(track => string.IsNullOrWhiteSpace(track.Id) ? track.Path : track.Id, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First()).Take(12).ToList();
        HydrateArtwork(recent);
        foreach (var track in recent)
        {
            var coverImage = new Image { Source = track.Artwork, Stretch = Stretch.UniformToFill };
            coverImage.SetBinding(Image.SourceProperty, new Microsoft.UI.Xaml.Data.Binding
            {
                Path = new Microsoft.UI.Xaml.PropertyPath(nameof(TrackItem.Artwork)),
                Mode = Microsoft.UI.Xaml.Data.BindingMode.OneWay
            });
            var cover = new Border
            {
                Width = 150, Height = 150, CornerRadius = new CornerRadius(8),
                Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(44, 255, 255, 255)),
                Child = coverImage
            };
            var title = new TextBlock
            {
                Text = track.Title, FontSize = 14, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 9, 0, 2)
            };
            var artist = new TextBlock
            {
                Text = string.IsNullOrWhiteSpace(track.ArtistName) ? track.Subtitle : track.ArtistName,
                FontSize = 12, Opacity = 0.7, TextTrimming = TextTrimming.CharacterEllipsis
            };
            var tileContent = new StackPanel { Width = 150 };
            tileContent.Children.Add(cover);
            tileContent.Children.Add(title);
            tileContent.Children.Add(artist);
            var tile = new Button
            {
                Width = 174, Height = 220, Padding = new Thickness(12),
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(22, 255, 255, 255)),
                BorderThickness = new Thickness(0), CornerRadius = new CornerRadius(10),
                Content = tileContent, DataContext = track
            };
            tile.Click += (_, _) => Play(track, recent, recent.IndexOf(track), true);
            HomeRecentTracks.Children.Add(tile);
        }
    }

    void HomeCarouselLeft_Click(object sender, RoutedEventArgs e) => ScrollHomeCarousel(sender, -1);
    void HomeCarouselRight_Click(object sender, RoutedEventArgs e) => ScrollHomeCarousel(sender, 1);

    void ScrollHomeCarousel(object sender, int direction)
    {
        if (sender is not Button { Tag: string targetName }) return;
        var scrollViewer = targetName switch
        {
            "HomeQuickLinksScroll" => HomeQuickLinksScroll,
            "HomePlaylistCardsScroll" => HomePlaylistCardsScroll,
            "HomeRecentTracksScroll" => HomeRecentTracksScroll,
            _ => null
        };
        if (scrollViewer is null) return;
        AnimateHomeCarousel(scrollViewer, scrollViewer.HorizontalOffset + direction * 360, 320);
    }

    void HomeCarousel_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        // The cards are horizontal carousels, but a normal wheel gesture should
        // keep scrolling the home page vertically instead of fighting the page.
        var delta = e.GetCurrentPoint(HomeDashboard).Properties.MouseWheelDelta;
        if (delta == 0) return;
        var maxScroll = Math.Max(0, HomeDashboard.ExtentHeight - HomeDashboard.ViewportHeight);
        HomeDashboard.ChangeView(null, Math.Clamp(HomeDashboard.VerticalOffset - delta * 1.1, 0, maxScroll), null, true);
        e.Handled = true;
    }

    void UpdateHomeScrollThumb()
    {
        if (HomeScrollRail is null || HomeScrollThumb is null || HomeDashboard is null) return;
        var viewport = HomeScrollRail.ActualHeight;
        var scrollable = HomeDashboard.ExtentHeight > HomeDashboard.ViewportHeight + 2 && viewport > 0 &&
                         HomeDashboard.Visibility == Visibility.Visible && HomePage.Visibility == Visibility.Visible;
        HomeScrollRail.Visibility = scrollable ? Visibility.Visible : Visibility.Collapsed;
        if (!scrollable) return;

        var thumbHeight = Math.Clamp(viewport * HomeDashboard.ViewportHeight / HomeDashboard.ExtentHeight, 42, viewport);
        HomeScrollThumb.Height = thumbHeight;
        Canvas.SetLeft(HomeScrollThumb, (HomeScrollRail.ActualWidth - HomeScrollThumb.Width) / 2);
        var maxThumbTop = Math.Max(0, viewport - thumbHeight);
        var maxScroll = Math.Max(1, HomeDashboard.ExtentHeight - HomeDashboard.ViewportHeight);
        Canvas.SetTop(HomeScrollThumb, maxThumbTop * HomeDashboard.VerticalOffset / maxScroll);
    }

    void HomeScrollRail_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var pointerY = e.GetCurrentPoint(HomeScrollRail).Position.Y;
        var thumbTop = Canvas.GetTop(HomeScrollThumb);
        if (pointerY >= thumbTop && pointerY <= thumbTop + HomeScrollThumb.Height)
            _homeScrollThumbGrabOffset = pointerY - thumbTop;
        else
        {
            _homeScrollThumbGrabOffset = HomeScrollThumb.Height / 2;
            SetHomeScrollFromThumbTop(pointerY - _homeScrollThumbGrabOffset);
        }
        _homeScrollThumbDragging = true;
        HomeScrollRail.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    void HomeScrollRail_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_homeScrollThumbDragging) return;
        var pointerY = e.GetCurrentPoint(HomeScrollRail).Position.Y;
        SetHomeScrollFromThumbTop(pointerY - _homeScrollThumbGrabOffset);
        e.Handled = true;
    }

    void HomeScrollRail_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        _homeScrollThumbDragging = false;
        HomeScrollRail.ReleasePointerCapture(e.Pointer);
        e.Handled = true;
    }

    void SetHomeScrollFromThumbTop(double requestedTop)
    {
        var maxThumbTop = Math.Max(0, HomeScrollRail.ActualHeight - HomeScrollThumb.Height);
        var maxScroll = Math.Max(0, HomeDashboard.ExtentHeight - HomeDashboard.ViewportHeight);
        if (maxThumbTop <= 0 || maxScroll <= 0) return;
        var ratio = Math.Clamp(requestedTop / maxThumbTop, 0, 1);
        HomeDashboard.ChangeView(null, ratio * maxScroll, null, true);
    }

    void AnimateHomeCarousel(ScrollViewer scrollViewer, double requestedOffset, int durationMs)
    {
        if (_carouselScrollTimers.TryGetValue(scrollViewer, out var previous))
        {
            previous.Stop();
            _carouselScrollTimers.Remove(scrollViewer);
        }

        var start = scrollViewer.HorizontalOffset;
        var target = Math.Clamp(requestedOffset, 0, Math.Max(0, scrollViewer.ExtentWidth - scrollViewer.ViewportWidth));
        if (Math.Abs(target - start) < 1) return;

        var elapsed = Stopwatch.StartNew();
        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(16);
        timer.IsRepeating = true;
        timer.Tick += (queueTimer, _) =>
        {
            var progress = Math.Clamp(elapsed.Elapsed.TotalMilliseconds / durationMs, 0, 1);
            var eased = 1 - Math.Pow(1 - progress, 3);
            scrollViewer.ChangeView(start + (target - start) * eased, null, null, true);
            if (progress < 1) return;
            queueTimer.Stop();
            if (_carouselScrollTimers.TryGetValue(scrollViewer, out var current) && ReferenceEquals(current, queueTimer))
                _carouselScrollTimers.Remove(scrollViewer);
        };
        _carouselScrollTimers[scrollViewer] = timer;
        timer.Start();
    }

    Border CreatePlaylistImage(PlaylistItem playlist, double size)
    {
        var border = new Border
        {
            Width = size, Height = size, CornerRadius = new CornerRadius(8),
            Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(44, 255, 255, 255))
        };
        if (playlist.Cover is not null)
            border.Child = new Image { Source = playlist.Cover, Stretch = Stretch.UniformToFill };
        else if (string.Equals(playlist.Name, "Favorites", StringComparison.OrdinalIgnoreCase))
            border.Child = new TextBlock { Text = "♥", FontSize = size * 0.42, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        else
            border.Child = new FontIcon { Glyph = "\uE8D6", FontSize = size * 0.35, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        return border;
    }

    void SidebarPlaylistButton_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is PlaylistItem playlist)
            OpenPlaylist(playlist);
    }

    // ================= SEARCH =================

    async void SearchBox_QuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        var query = args.QueryText.Trim();
        if (query.Length == 0) return;

        _searchCancellation?.Cancel();
        var searchCancellation = new CancellationTokenSource();
        _searchCancellation = searchCancellation;
        var cancellationToken = searchCancellation.Token;

        HomeDashboard.Visibility = Visibility.Collapsed;
        HomeHeading.Visibility = Visibility.Visible;
        ResultsList.Visibility = Visibility.Visible;
        ShowPage(HomePage);
        _results.Clear();
        try
        {
            // Direct link? Route it straight to a source.
            if (Uri.TryCreate(query, UriKind.Absolute, out var uri))
            {
                var host = uri.Host.ToLowerInvariant();

                if (host.Contains("soundcloud.com"))
                {
                    HomeHeading.Text = "SoundCloud link";
                    StatusText.Text = "Resolving SoundCloud…";
                    var url = await _sc.ResolveStreamUrlAsync(query, cancellationToken);
                    cancellationToken.ThrowIfCancellationRequested();
                    var item = new TrackItem
                    {
                        Title = "SoundCloud track",
                        Subtitle = "SoundCloud",
                        Id = query,
                        WebUrl = query,
                        Path = url,
                        Source = "SOUNDCLOUD",
                        IsOnline = true,
                        ResolvedAt = DateTimeOffset.UtcNow
                    };
                    _results.Add(item);
                    StatusText.Text = "Ready — select it to play.";
                    return;
                }

                if (host.Contains("youtube.com") || host.Contains("youtu.be"))
                {
                    var id = ExtractVideoId(query);
                    if (id is not null)
                    {
                        HomeHeading.Text = "Online music link";
                        var item = new TrackItem
                        {
                            Title = "YouTube track",
                            Subtitle = "Online music",
                            Id = id,
                            WebUrl = "https://music.youtube.com/watch?v=" + id,
                            Source = "YT MUSIC",
                            IsOnline = true,
                            ArtworkUrl = $"https://i.ytimg.com/vi/{id}/mqdefault.jpg"
                        };
                        _results.Add(item);
                        HydrateArtwork(new[] { item });
                        StatusText.Text = "Ready — select it to play.";
                        return;
                    }
                }
            }

            // Search the primary catalogue first. Only use SoundCloud when the
            // primary search returns no tracks or fails.
            HomeHeading.Text = $"Results for \u201c{query}\u201d";
            StatusText.Text = "Searching for matches…";
            var errors = new List<string>();
            List<TrackItem> tracks = new();
            try
            {
                tracks = await _yt.SearchAsync(query, _searchResultCount, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception ex) { errors.Add(ex.Message); }

            if (!ReferenceEquals(_searchCancellation, searchCancellation)) return;
            if (tracks.Count == 0)
            {
                StatusText.Text = "No matches yet — checking another catalogue…";
                try { tracks = await _sc.SearchAsync(query, _searchResultCount, cancellationToken); }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (Exception ex) { errors.Add(ex.Message); }
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (!ReferenceEquals(_searchCancellation, searchCancellation)) return;
            if (tracks.Count == 0 && errors.Count > 0)
                StatusText.Text = "Search failed: " + string.Join("; ", errors);
            else if (tracks.Count == 0)
                StatusText.Text = "Nothing found. Try different wording.";
            else
            {
                // Keep artist identity as the strongest match signal. Popularity only
                // breaks ties between similarly relevant tracks, so a famous but
                // unrelated result cannot outrank the intended recording.
                var ranked = tracks.OrderByDescending(track => SearchRelevance(query, track)).ToList();
                _results.Clear();
                _results.AddRange(ranked);
                HydrateArtwork(ranked);
                StatusText.Text = $"{_results.Count} matching tracks";
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (ReferenceEquals(_searchCancellation, searchCancellation))
                StatusText.Text = "Search failed: " + ex.Message;
        }
        finally
        {
            if (ReferenceEquals(_searchCancellation, searchCancellation))
                _searchCancellation = null;
            searchCancellation.Dispose();
        }
    }

    static double SearchRelevance(string query, TrackItem track)
    {
        static string[] Tokens(string value) => Regex.Matches(value.ToLowerInvariant(), @"[\p{L}\p{N}]+")
            .Select(match => match.Value).Where(token => token.Length > 1).Distinct().ToArray();

        var wanted = Tokens(query);
        if (wanted.Length == 0) return 0;
        var artists = Tokens(track.ArtistName + " " + track.Subtitle).ToHashSet(StringComparer.Ordinal);
        var titles = Tokens(track.Title).ToHashSet(StringComparer.Ordinal);
        var artistHits = wanted.Count(artists.Contains);
        var titleHits = wanted.Count(titles.Contains);
        var artistCoverage = (double)artistHits / wanted.Length;
        var titleCoverage = (double)titleHits / wanted.Length;
        var score = artistCoverage * 100 + titleCoverage * 42;

        var normalizedQuery = string.Join(' ', wanted);
        var normalizedArtist = string.Join(' ', Tokens(track.ArtistName));
        if (normalizedArtist.Length > 0 && (normalizedArtist == normalizedQuery || normalizedQuery.Contains(normalizedArtist, StringComparison.Ordinal)))
            score += 24;

        // SoundCloud supplies public playback and artist-follower counts. Use a
        // logarithmic bonus so huge accounts help rank close matches without
        // overpowering artist/title relevance.
        score += Math.Log10(track.SearchPlayCount + 1d) * 2.5;
        score += Math.Log10(track.SearchArtistFollowers + 1d) * 2.0;
        return score;
    }

    static string? ExtractVideoId(string url)
    {
        var m = System.Text.RegularExpressions.Regex.Match(url, @"(?:v=|youtu\.be/|/shorts/)([A-Za-z0-9_-]{11})");
        return m.Success ? m.Groups[1].Value : null;
    }

    void HydrateArtwork(IEnumerable<TrackItem> items)
    {
        foreach (var item in items)
        {
            if (item.Artwork is null && Uri.TryCreate(item.ArtworkUrl, UriKind.Absolute, out var uri) &&
                (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp))
            {
                _ = LoadArtworkAsync(item, uri);
            }
            else if (string.Equals(item.Source, "SPOTIFY", StringComparison.OrdinalIgnoreCase) &&
                     string.IsNullOrWhiteSpace(item.ArtworkUrl) && _artworkLookupAttempted.TryAdd(item, 0))
            {
                _ = ResolveMissingArtworkAsync(item);
            }
        }
    }

    async Task ResolveMissingArtworkAsync(TrackItem track)
    {
        try
        {
            var query = string.Join(" ", new[] { track.Title, track.ArtistName }
                .Where(value => !string.IsNullOrWhiteSpace(value)));
            if (string.IsNullOrWhiteSpace(query)) return;
            var matches = await _yt.SearchAsync(query, 5);
            static string Normalize(string value) => Regex.Replace(value.ToLowerInvariant(), @"[^\p{L}\p{N}]", "");
            var wantedTitle = Normalize(track.Title);
            var wantedArtist = Normalize(track.ArtistName);
            var match = matches
                .Select(candidate =>
                {
                    var title = Normalize(candidate.Title);
                    var titleScore = title == wantedTitle ? 3
                        : title.Length > 0 && wantedTitle.Length > 0 &&
                          (title.Contains(wantedTitle, StringComparison.Ordinal) || wantedTitle.Contains(title, StringComparison.Ordinal)) ? 2
                        : 0;
                    var artistText = Normalize(candidate.ArtistName + candidate.Subtitle);
                    var artistScore = wantedArtist.Length > 0 && artistText.Contains(wantedArtist, StringComparison.Ordinal) ? 1 : 0;
                    return (Candidate: candidate, TitleScore: titleScore, Score: titleScore + artistScore);
                })
                .Where(result => result.TitleScore >= 2)
                .OrderByDescending(result => result.Score)
                .Select(result => result.Candidate)
                .FirstOrDefault(candidate => !string.IsNullOrWhiteSpace(candidate.ArtworkUrl));

            if (match is null) return;
            DispatcherQueue.TryEnqueue(() =>
            {
                if (!string.IsNullOrWhiteSpace(track.ArtworkUrl)) return;
                track.ArtworkUrl = match.ArtworkUrl;
                track.Artwork = null;
                HydrateArtwork(new[] { track });
            });
        }
        catch { /* Artwork lookup must never interrupt playlist import or playback. */ }
    }

    static HttpClient CreateArtworkHttpClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("RiskMusicPlayer/1.0");
        return client;
    }

    async Task LoadArtworkAsync(TrackItem item, Uri uri)
    {
        try
        {
            var bitmap = await LoadBitmapAsync(uri.AbsoluteUri, 320);
            if (bitmap is null)
            {
                QueueMissingArtworkLookup(item);
                return;
            }
            if (!string.Equals(item.ArtworkUrl, uri.AbsoluteUri, StringComparison.OrdinalIgnoreCase)) return;
            item.Artwork = bitmap;
            if (ReferenceEquals(_current, item)) NowArtwork.Source = bitmap;
        }
        catch
        {
            // Some image CDNs reject HttpClient requests but still serve the same
            // image through WinUI's URI loader.
            try { item.Artwork = new BitmapImage { DecodePixelWidth = 320, UriSource = uri }; }
            catch { /* Keep the row placeholder when the artwork host is unavailable. */ }
            QueueMissingArtworkLookup(item);
        }
    }

    void QueueMissingArtworkLookup(TrackItem item)
    {
        if (string.Equals(item.Source, "SPOTIFY", StringComparison.OrdinalIgnoreCase) &&
            _artworkLookupAttempted.TryAdd(item, 0))
            _ = ResolveMissingArtworkAsync(item);
    }

    async Task<BitmapImage?> LoadBitmapAsync(string imageUrl, int decodeWidth)
    {
        if (!Uri.TryCreate(imageUrl, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)) return null;
        await ArtworkDownloadGate.WaitAsync();
        byte[] bytes;
        try
        {
            if (!ArtworkBytes.TryGetValue(uri.AbsoluteUri, out var cachedBytes))
            {
                bytes = await ArtworkHttp.GetByteArrayAsync(uri);
                if (bytes.Length == 0 || bytes.Length > 12 * 1024 * 1024) return null;
                if (ArtworkBytes.Count > 300) ArtworkBytes.Clear();
                ArtworkBytes.TryAdd(uri.AbsoluteUri, bytes);
            }
            else bytes = cachedBytes;
        }
        finally { ArtworkDownloadGate.Release(); }

        using var stream = new InMemoryRandomAccessStream();
        using (var writer = new DataWriter(stream))
        {
            writer.WriteBytes(bytes);
            await writer.StoreAsync();
            await writer.FlushAsync();
            writer.DetachStream();
        }
        stream.Seek(0);
        var bitmap = new BitmapImage { DecodePixelWidth = decodeWidth };
        await bitmap.SetSourceAsync(stream);
        return bitmap;
    }

    // ================= SELECTION =================

    void ResultsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ResultsList.SelectedItem is not TrackItem track) return;
        Play(track, _results.ToList(), _results.IndexOf(track), true);
    }

    void LibraryList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (LibraryList.SelectedItem is not TrackItem track) return;
        Play(track, _library.ToList(), _library.IndexOf(track), true);
    }

    void ProfileBack_Click(object sender, RoutedEventArgs e) => ShowPage(HomePage);

    void NowArtwork_Click(object sender, RoutedEventArgs e)
    {
        if (_current is null || _isExpandedPlayer) return;
        _pageBeforeExpanded = new[] { HomePage, LibraryPage, SettingsPage, ProfilePage, EqualizerPage, PlaylistDetailPage }
            .FirstOrDefault(page => page.Visibility == Visibility.Visible) ?? HomePage;
        _isExpandedPlayer = true;
        PlayerLayout.Children.Remove(TransportPanel);
        ExpandedControlsHost.Children.Add(TransportPanel);
        TransportPanel.Width = 440;
        TransportPanel.HorizontalAlignment = HorizontalAlignment.Stretch;
        PlayerBar.Visibility = Visibility.Collapsed;
        ExpandedTitle.Text = _current.Title;
        ExpandedArtistButton.Content = string.IsNullOrWhiteSpace(_current.ArtistName) ? _current.Subtitle : _current.ArtistName;
        ExpandedArtwork.Source = _current.Artwork;
        ShowPage(ExpandedPlayerPage);
        if (!string.IsNullOrWhiteSpace(_current.ArtworkUrl))
            _ = LoadExpandedArtworkAsync(_current.ArtworkUrl);
        _visualizerTimer?.Start();
    }

    async Task LoadExpandedArtworkAsync(string artworkUrl)
    {
        var largeUrl = GetHighResolutionArtworkUrl(artworkUrl);
        var image = await LoadBitmapAsync(largeUrl, 1440);
        if (image is null && !string.Equals(largeUrl, artworkUrl, StringComparison.OrdinalIgnoreCase))
            image = await LoadBitmapAsync(artworkUrl, 1440);
        if (image is not null && _isExpandedPlayer && _current?.ArtworkUrl == artworkUrl)
            ExpandedArtwork.Source = image;
    }

    static string GetHighResolutionArtworkUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return url;
        // YouTube Music thumbnails support requesting a larger rendition by
        // changing the size token. This matters because DecodePixelWidth alone
        // cannot restore detail missing from the small search thumbnail.
        var googleThumb = Regex.Match(url, @"=w\d+-h\d+", RegexOptions.IgnoreCase);
        if (googleThumb.Success)
            return url[..googleThumb.Index] + "=w1440-h1440" + url[(googleThumb.Index + googleThumb.Length)..];

        if (url.Contains("i.ytimg.com", StringComparison.OrdinalIgnoreCase))
        {
            var videoThumb = Regex.Replace(url, @"(?:default|mqdefault|hqdefault|sddefault)\.(?:jpg|webp)$",
                "maxresdefault.jpg", RegexOptions.IgnoreCase);
            if (!string.Equals(videoThumb, url, StringComparison.OrdinalIgnoreCase)) return videoThumb;
        }
        return url;
    }

    void CloseExpandedPlayer_Click(object sender, RoutedEventArgs e) => CloseExpandedPlayer();

    void CloseExpandedPlayer()
    {
        if (!_isExpandedPlayer) return;
        var returnPage = _pageBeforeExpanded ?? HomePage;
        _isExpandedPlayer = false;
        RestoreExpandedPlayerLayout();
        ShowPage(returnPage);
    }

    void RestoreExpandedPlayerLayout()
    {
        if (!_isExpandedPlayer && PlayerBar.Visibility == Visibility.Visible) return;
        ExpandedControlsHost.Children.Remove(TransportPanel);
        PlayerLayout.Children.Add(TransportPanel);
        TransportPanel.Width = 380;
        TransportPanel.HorizontalAlignment = HorizontalAlignment.Stretch;
        PlayerBar.Visibility = Visibility.Visible;
        _visualizerTimer?.Stop();
        var compact = Root.ActualWidth < 1120;
        if (compact)
        {
            Grid.SetRow(TransportPanel, 1);
            Grid.SetColumn(TransportPanel, 0);
            Grid.SetColumnSpan(TransportPanel, 2);
        }
        else
        {
            Grid.SetRow(TransportPanel, 0);
            Grid.SetColumn(TransportPanel, 1);
            Grid.SetColumnSpan(TransportPanel, 1);
        }
    }

    void BuildVisualizerBars()
    {
        VisualizerBars.Children.Clear();
        _displayedVisualizerLevels = new double[42];
        for (var index = 0; index < 42; index++)
        {
            VisualizerBars.Children.Add(new Border
            {
                Width = 5,
                Height = 5,
                VerticalAlignment = VerticalAlignment.Center,
                CornerRadius = new CornerRadius(3),
                Background = (Microsoft.UI.Xaml.Media.Brush)Root.Resources["AccentFillColorDefaultBrush"]
            });
        }
    }

    void UpdateVisualizer()
    {
        if (!_isExpandedPlayer) return;
        var playing = _player.IsPlaying;
        var levels = Volatile.Read(ref _latestVisualizerLevels);
        for (var index = 0; index < VisualizerBars.Children.Count; index++)
        {
            if (VisualizerBars.Children[index] is not Border bar) continue;
            var target = playing && index < levels.Length ? levels[index] : 0;
            var current = _displayedVisualizerLevels[index];
            current += (target - current) * (target > current ? 0.62 : 0.22);
            _displayedVisualizerLevels[index] = current;
            bar.Height = 7 + current * 112;
        }
    }

    void ProfileAllTab_Click(object sender, RoutedEventArgs e)
    {
        _profileTracks.Clear();
        _profileTracks.AddRange(_allProfileTracks);
        ProfileDescription.Text = _profileTracks.Count == 0 ? "No tracks available" : $"{_profileTracks.Count} tracks";
    }

    void ProfilePopularTab_Click(object sender, RoutedEventArgs e)
    {
        _profileTracks.Clear();
        _profileTracks.AddRange(_allProfileTracks.OrderByDescending(track => track.SearchPlayCount));
        ProfileDescription.Text = _profileTracks.Count == 0 ? "No tracks available" : $"Popular tracks · {_profileTracks.Count} tracks";
    }

    async void ArtistProfile_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: TrackItem track }) return;
        await OpenArtistProfileAsync(track);
    }

    async void NowArtist_Click(object sender, RoutedEventArgs e)
    {
        if (_current is { ArtistName.Length: > 0 } track)
            await OpenArtistProfileAsync(track);
    }

    async Task OpenArtistProfileAsync(TrackItem track)
    {
        if (track.ArtistName.Length == 0) return;
        if (_isExpandedPlayer) CloseExpandedPlayer();
        var profileToken = ++_profileLoadToken;
        ShowPage(ProfilePage);
        ProfileHeading.Text = track.ArtistName;
        ProfileAvatarImage.Source = null;
        ProfileBannerImage.Source = null;
        ProfileDescription.Text = "Loading artist tracks…";
        _profileTracks.Clear();
        _allProfileTracks.Clear();
        var cacheKey = !string.IsNullOrWhiteSpace(track.ArtistId) ? "yt:" + track.ArtistId
            : !string.IsNullOrWhiteSpace(track.ArtistUrl) ? "sc:" + track.ArtistUrl
            : "name:" + track.ArtistName.Trim();
        try
        {
            (string Name, string AvatarUrl, string BannerUrl, List<TrackItem> Tracks)? profile =
                _artistProfileCache.TryGetValue(cacheKey, out var cached) ? cached : null;
            if (profile is null && track.Source == "YT MUSIC" && !string.IsNullOrWhiteSpace(track.ArtistId))
            {
                try { profile = await _yt.GetArtistTracksAsync(track.ArtistId); } catch { }
            }
            else if (profile is null && track.Source == "SOUNDCLOUD" && !string.IsNullOrWhiteSpace(track.ArtistUrl))
            {
                try { profile = await _sc.GetArtistTracksAsync(track.ArtistUrl); } catch { }
            }

            if ((profile is null || profile.Value.Tracks.Count == 0) && !string.IsNullOrWhiteSpace(track.ArtistName))
            {
                try { profile = await _yt.FindArtistAsync(track.ArtistName); } catch { }
            }
            if ((profile is null || profile.Value.Tracks.Count == 0) && !string.IsNullOrWhiteSpace(track.ArtistName))
            {
                try
                {
                    var artistUrl = await _sc.FindArtistProfileUrlAsync(track.ArtistName);
                    profile = await _sc.GetArtistTracksAsync(artistUrl);
                }
                catch { }
            }
            if (profile is null) throw new InvalidOperationException("No matching artist profile was found.");
            if (profileToken != _profileLoadToken) return;
            var result = profile.Value;
            _artistProfileCache[cacheKey] = result;
            ProfileHeading.Text = result.Name;
            _allProfileTracks = result.Tracks;
            _profileTracks.AddRange(_allProfileTracks);
            HydrateArtwork(_profileTracks);
            if (!string.IsNullOrWhiteSpace(result.AvatarUrl))
                _ = LoadProfileImageAsync(ProfileAvatarImage, result.AvatarUrl, 512);
            if (!string.IsNullOrWhiteSpace(result.BannerUrl))
                _ = LoadProfileImageAsync(ProfileBannerImage, result.BannerUrl, 1600);
            ProfileDescription.Text = $"{_profileTracks.Count} tracks";
            if (_profileTracks.Count == 0) ProfileDescription.Text = "No tracks were available for this artist.";
        }
        catch
        {
            if (profileToken == _profileLoadToken)
                ProfileDescription.Text = "Could not load this artist profile.";
        }
    }

    async Task LoadProfileImageAsync(Image target, string url, int decodeWidth)
    {
        var image = await LoadBitmapAsync(url, decodeWidth);
        if (image is not null) target.Source = image;
    }

    void ProfileTracksList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ProfileTracksList.SelectedItem is not TrackItem track) return;
        Play(track, _profileTracks.ToList(), _profileTracks.IndexOf(track), true);
    }

    // ================= PLAYBACK =================

    async void Play(TrackItem track, List<TrackItem> queue, int index, bool autoplay, bool automaticFade = false, bool forceResolve = false)
    {
        var token = ++_loadToken;
        _automaticFadeStarted = automaticFade;
        if (!ReferenceEquals(queue, _queue))
        {
            var requestedQueue = queue.ToList();
            if (_shuffleEnabled && requestedQueue.Count > 1)
            {
                _unshuffledQueue = requestedQueue;
                _queue = ShuffleStartingWith(requestedQueue, track);
                index = _queue.FindIndex(item => ReferenceEquals(item, track));
            }
            else
            {
                _queue = requestedQueue;
                _unshuffledQueue = null;
                index = Math.Clamp(index, 0, Math.Max(0, _queue.Count - 1));
            }
        }
        else if (index < 0 || index >= _queue.Count || !ReferenceEquals(_queue[index], track))
        {
            index = _queue.FindIndex(item => ReferenceEquals(item, track));
        }
        _current = track;
        _index = index;
        UpdateFavoriteButton(track);

        NowTitle.Text = track.Title;
        NowArtistButton.Content = track.ArtistName.Length > 0 ? track.ArtistName : track.Subtitle;
        NowArtistButton.Visibility = NowArtistButton.Content?.ToString()?.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        NowArtistButton.IsEnabled = track.ArtistName.Length > 0;
        NowArtwork.Source = track.Artwork;
        if (_isExpandedPlayer)
        {
            ExpandedTitle.Text = track.Title;
            ExpandedArtistButton.Content = track.ArtistName.Length > 0 ? track.ArtistName : track.Subtitle;
            ExpandedArtwork.Source = track.Artwork;
            if (!string.IsNullOrWhiteSpace(track.ArtworkUrl)) _ = LoadExpandedArtworkAsync(track.ArtworkUrl);
        }
        ElapsedText.Text = "0:00";
        DurationText.Text = track.DurationLabel.Length > 0 ? track.DurationLabel : "0:00";
        SetSeek(0);

        try
        {
            string url = track.Path;

            if (string.Equals(track.Source, "SPOTIFY", StringComparison.OrdinalIgnoreCase))
            {
                StatusText.Text = $"Finding another version of {track.Title}…";
                var matches = await _yt.SearchAsync($"{track.Title} {track.ArtistName}", 1);
                if (token != _loadToken) return;
                if (matches.Count == 0) throw new InvalidOperationException("No playable match was found for this Spotify track.");
                var match = matches[0];
                track.Source = match.Source;
                track.Id = match.Id;
                track.ArtistName = match.ArtistName;
                track.ArtistId = match.ArtistId;
                track.ArtistUrl = match.ArtistUrl;
                track.IsOnline = true;
                track.WebUrl = match.WebUrl;
                track.ArtworkUrl = match.ArtworkUrl;
                track.Artwork = match.Artwork;
                track.DurationLabel = match.DurationLabel;
                HydrateArtwork(new[] { track });
                if (_isExpandedPlayer && !string.IsNullOrWhiteSpace(track.ArtworkUrl))
                    _ = LoadExpandedArtworkAsync(track.ArtworkUrl);
                url = await _yt.ResolveStreamUrlAsync(track.Id, force: true, quality: _streamingQuality);
                track.Path = url;
                track.ResolvedAt = DateTimeOffset.UtcNow;
                NowArtwork.Source = track.Artwork;
                UpdateFavoriteButton(track);
            }

            if (track.IsOnline && (forceResolve || !track.IsStreamFresh))
            {
                StatusText.Text = $"Resolving {track.Source} stream…";
                url = track.Source == "YT MUSIC"
                    ? await _yt.ResolveStreamUrlAsync(track.Id, force: forceResolve, quality: _streamingQuality)
                    : await _sc.ResolveStreamUrlAsync(track.Id);

                if (token != _loadToken) return;   // user clicked something else

                track.Path = url;
                track.ResolvedAt = DateTimeOffset.UtcNow;
            }

            await _player.PlayAsync(url, autoplay && _crossfadeEnabled, TimeSpan.FromSeconds(_crossfadeSeconds),
                GetNormalizationGain(track));
            if (token != _loadToken) return;
            if (!autoplay) _player.Pause();
            PlayIcon.Glyph = autoplay ? "\uE769" : "\uE768";
            if (autoplay) UpdateDiscordActivity(track);
            if (automaticFade) _automaticFadeStarted = false;
            if (autoplay && _crossfadeEnabled) _ = WarmNextTrackAsync(token);
        }
        catch (Exception ex)
        {
            if (token != _loadToken) return;
            _automaticFadeStarted = false;
            StatusText.Text = $"Couldn't play this track: {ex.Message}";
        }
    }

    void Play_Click(object sender, RoutedEventArgs e)
    {
        if (_current is null)
        {
            if (_library.Count > 0) Play(_library[0], _library.ToList(), 0, true);
            else StatusText.Text = "Search for a track or import some files first.";
            return;
        }

        if (_player.IsPlaying)
        {
            _player.Pause();
            _ = _discord.ClearAsync();
            PlayIcon.Glyph = "\uE768";
        }
        else
        {
            _player.Resume();
            if (_current is { } current)
                UpdateDiscordActivity(current);
            PlayIcon.Glyph = "\uE769";
        }
    }

    async void NowTrack_Click(object sender, RoutedEventArgs e)
    {
        if (_current is not { } track) return;
        var showPlaylistPicker = false;
        var trackCard = new StackPanel { Spacing = 16, MinWidth = 300 };
        var cover = new Image
        {
            Source = track.Artwork,
            Width = 120,
            Height = 120,
            Stretch = Stretch.UniformToFill,
            HorizontalAlignment = HorizontalAlignment.Center
        };
        trackCard.Children.Add(cover);
        var addButton = new Button
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Center,
                Spacing = 8,
                Children =
                {
                    new TextBlock { Text = "Add to playlist", VerticalAlignment = VerticalAlignment.Center },
                    new FontIcon { Glyph = "\uE710", FontSize = 14 }
                }
            }
        };
        trackCard.Children.Add(addButton);
        var actionDialog = new ContentDialog
        {
            Title = track.Title,
            Content = trackCard,
            CloseButtonText = "Close",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = Root.XamlRoot
        };
        addButton.Click += (_, _) =>
        {
            showPlaylistPicker = true;
            actionDialog.Hide();
        };
        await actionDialog.ShowAsync();
        if (showPlaylistPicker)
            await ShowAddToPlaylistDialogAsync(track);
    }

    async Task ShowAddToPlaylistDialogAsync(TrackItem track)
    {
        GetFavoritesPlaylist();
        var selectedPlaylists = _playlists
            .Where(playlist => playlist.Tracks.Any(existing => SameTrack(existing, track)))
            .ToHashSet();
        var rows = new StackPanel { Spacing = 6 };
        var pickerStatus = new TextBlock { Visibility = Visibility.Collapsed, TextWrapping = TextWrapping.Wrap };
        var filter = new TextBox { PlaceholderText = "Filter playlists", MinWidth = 280 };
        filter.TextChanged += (_, _) => RebuildPlaylistPickerRows(track, filter.Text, rows, pickerStatus, selectedPlaylists);
        RebuildPlaylistPickerRows(track, "", rows, pickerStatus, selectedPlaylists);

        var content = new StackPanel { Spacing = 10, MinWidth = 320 };
        content.Children.Add(filter);
        content.Children.Add(pickerStatus);
        content.Children.Add(new ScrollViewer { Content = rows, MaxHeight = 320 });

        var dialog = new ContentDialog
        {
            Title = "Choose playlists",
            Content = content,
            PrimaryButtonText = "Confirm",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = Root.XamlRoot
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        var changed = false;
        foreach (var playlist in _playlists)
        {
            var alreadyAdded = playlist.Tracks.Any(existing => SameTrack(existing, track));
            var shouldBeAdded = selectedPlaylists.Contains(playlist);
            if (alreadyAdded == shouldBeAdded) continue;

            if (shouldBeAdded)
                playlist.Tracks.Add(track);
            else
                playlist.Tracks.RemoveAll(existing => SameTrack(existing, track));

            playlist.UpdatedAt = DateTimeOffset.Now;
            changed = true;
            if (ReferenceEquals(_selectedPlaylist, playlist))
            {
                PlaylistTrackCount.Text = playlist.Tracks.Count.ToString();
                PlaylistTracksList.ItemsSource = null;
                PlaylistTracksList.ItemsSource = playlist.Tracks;
                DownloadPlaylistButton.IsEnabled = playlist.Tracks.Count > 0;
                PlaylistDetailUpdated.Text = $"Updated {playlist.UpdatedAt.LocalDateTime:g}";
            }
        }

        if (changed)
        {
            SavePlaylists();
            RefreshSidebarPlaylists();
        }
        UpdateFavoriteButton(track);
    }

    void RebuildPlaylistPickerRows(
        TrackItem track, string filter, StackPanel rows, TextBlock pickerStatus,
        HashSet<PlaylistItem> selectedPlaylists)
    {
        rows.Children.Clear();
        var matches = _playlists
            .Where(p => p.Name.Contains(filter.Trim(), StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (matches.Count == 0)
        {
            rows.Children.Add(new TextBlock
            {
                Text = _playlists.Count == 0 ? "No playlists yet." : "No matching playlists.",
                Margin = new Thickness(4, 10, 4, 10)
            });
            return;
        }

        foreach (var playlist in matches)
        {
            var isSelected = selectedPlaylists.Contains(playlist);
            var rowGrid = new Grid { ColumnSpacing = 10 };
            rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var cover = new Grid
            {
                Width = 48,
                Height = 48,
                Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                    Windows.UI.Color.FromArgb(0, 0, 0, 0))
            };
            if (playlist.Cover is not null)
                cover.Children.Add(new Image { Source = playlist.Cover, Stretch = Stretch.UniformToFill });
            else
                cover.Children.Add(new FontIcon { Glyph = "\uE8D6", FontSize = 18, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center });
            Grid.SetColumn(cover, 0);
            rowGrid.Children.Add(cover);

            var labels = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 2 };
            labels.Children.Add(new TextBlock
            {
                Text = playlist.Name,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                TextTrimming = TextTrimming.CharacterEllipsis
            });
            var trackCountText = new TextBlock
            {
                Text = $"{playlist.Tracks.Count} tracks",
                FontSize = 12,
                Opacity = 0.72
            };
            labels.Children.Add(trackCountText);
            Grid.SetColumn(labels, 1);
            rowGrid.Children.Add(labels);

            var checkIcon = new FontIcon
            {
                Glyph = isSelected ? "\uE73E" : "",
                FontSize = 13,
                Visibility = isSelected ? Visibility.Visible : Visibility.Collapsed,
                Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                    Windows.UI.Color.FromArgb(255, 255, 255, 255))
            };
            var checkButton = new Border
            {
                Width = 24,
                Height = 24,
                BorderThickness = new Thickness(1),
                BorderBrush = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                    Windows.UI.Color.FromArgb(130, 255, 255, 255)),
                Background = isSelected
                    ? new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 48, 126, 245))
                    : new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0)),
                CornerRadius = new CornerRadius(5),
                Child = checkIcon,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(checkButton, 2);
            rowGrid.Children.Add(checkButton);

            var rowButton = new Button
            {
                Content = rowGrid,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Padding = new Thickness(8),
                CornerRadius = new CornerRadius(8),
                Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                    Windows.UI.Color.FromArgb(28, 255, 255, 255)),
                BorderThickness = new Thickness(0)
            };
            rowButton.Click += (_, _) =>
            {
                if (!selectedPlaylists.Add(playlist)) selectedPlaylists.Remove(playlist);
                var nowSelected = selectedPlaylists.Contains(playlist);
                checkIcon.Glyph = nowSelected ? "\uE73E" : "";
                checkIcon.Visibility = nowSelected ? Visibility.Visible : Visibility.Collapsed;
                checkButton.Background = nowSelected
                    ? new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 48, 126, 245))
                    : new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0));
                pickerStatus.Text = $"{selectedPlaylists.Count} playlist(s) selected.";
                pickerStatus.Visibility = Visibility.Visible;
            };
            rows.Children.Add(rowButton);
        }
    }

    async void AddCurrentToPlaylist_Click(object sender, RoutedEventArgs e)
    {
        if (_current is { } track)
            await ShowAddToPlaylistDialogAsync(track);
    }

    PlaylistItem GetFavoritesPlaylist()
    {
        var playlist = _playlists.FirstOrDefault(item => string.Equals(item.Name, "Favorites", StringComparison.OrdinalIgnoreCase));
        if (playlist is not null) return playlist;
        playlist = new PlaylistItem { Name = "Favorites", Description = "Your favorite songs" };
        _playlists.Insert(0, playlist);
        SavePlaylists();
        return playlist;
    }

    bool IsFavorite(TrackItem track) => _playlists
        .FirstOrDefault(item => string.Equals(item.Name, "Favorites", StringComparison.OrdinalIgnoreCase))?
        .Tracks.Any(existing => SameTrack(existing, track)) == true;

    void UpdateFavoriteButton(TrackItem track)
    {
        var favorite = IsFavorite(track);
        FavoriteIcon.Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(favorite
            ? Windows.UI.Color.FromArgb(255, 48, 126, 245)
            : Windows.UI.Color.FromArgb(255, 230, 230, 230));
        ToolTipService.SetToolTip(FavoriteButton, favorite ? "Remove from favorites" : "Add to favorites");
    }

    void FavoriteCurrent_Click(object sender, RoutedEventArgs e)
    {
        if (_current is not { } track) return;
        var favorites = GetFavoritesPlaylist();
        var existing = favorites.Tracks.FindIndex(item => SameTrack(item, track));
        if (existing >= 0) favorites.Tracks.RemoveAt(existing);
        else favorites.Tracks.Add(track);
        favorites.UpdatedAt = DateTimeOffset.Now;
        SavePlaylists();
        RefreshSidebarPlaylists();
        if (ReferenceEquals(_selectedPlaylist, favorites)) OpenPlaylist(favorites);
        UpdateFavoriteButton(track);
    }

    async void AddTrackToPlaylist_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is TrackItem track)
            await ShowAddToPlaylistDialogAsync(track);
    }

    static bool SameTrack(TrackItem first, TrackItem second)
    {
        if (ReferenceEquals(first, second)) return true;
        if (!string.IsNullOrWhiteSpace(first.Id) && !string.IsNullOrWhiteSpace(second.Id))
            return string.Equals(first.Source, second.Source, StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(first.Id, second.Id, StringComparison.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(first.Path) && !string.IsNullOrWhiteSpace(second.Path))
            return string.Equals(first.Path, second.Path, StringComparison.OrdinalIgnoreCase);
        return string.Equals(first.Title, second.Title, StringComparison.OrdinalIgnoreCase) &&
               string.Equals(first.ArtistName, second.ArtistName, StringComparison.OrdinalIgnoreCase);
    }

    void Previous_Click(object sender, RoutedEventArgs e) => Step(-1);
    void Next_Click(object sender, RoutedEventArgs e) => Step(1);

    void Step(int delta)
    {
        if (_queue.Count == 0) return;
        var next = _index < 0 ? 0 : (_index + delta + _queue.Count) % _queue.Count;
        Play(_queue[next], _queue, next, true);
    }

    void HandleMediaEnded()
    {
        if (_automaticFadeStarted || _queue.Count == 0) return;
        if (_repeatMode == 2 && _index >= 0)
        {
            Play(_queue[_index], _queue, _index, true, forceResolve: _queue[_index].IsOnline);
            return;
        }
        if (_index + 1 < _queue.Count)
        {
            Step(1);
            return;
        }
        if (_repeatMode == 1)
        {
            Play(_queue[0], _queue, 0, true);
            return;
        }
        if (_autoplaySimilarTracks && _current is { } endedTrack)
        {
            StatusText.Text = "Finding similar tracks…";
            _ = PlayAutoplaySimilarTracksAsync(endedTrack, _loadToken);
            return;
        }
        PlayIcon.Glyph = "\uE768";
        _ = _discord.ClearAsync();
    }

    async Task PlayAutoplaySimilarTracksAsync(TrackItem previous, int playbackToken)
    {
        try
        {
            var query = string.IsNullOrWhiteSpace(previous.ArtistName)
                ? previous.Title + " similar songs"
                : previous.ArtistName + " radio";
            var recommendations = new List<TrackItem>();
            if (_searchYouTubeMusic)
                recommendations.AddRange(await _yt.SearchAsync(query, Math.Min(_searchResultCount, 15)));
            if (_searchSoundCloud)
                recommendations.AddRange(await _sc.SearchAsync(query, Math.Min(_searchResultCount, 15)));
            if (playbackToken != _loadToken) return;

            var candidates = recommendations
                .Where(track => !string.Equals(track.Id, previous.Id, StringComparison.OrdinalIgnoreCase))
                .GroupBy(track => track.Source + "|" + track.Id, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .ToList();
            if (candidates.Count == 0)
            {
                StatusText.Text = "No similar tracks were found.";
                PlayIcon.Glyph = "\uE768";
                return;
            }

            var startIndex = Random.Shared.Next(Math.Min(candidates.Count, 5));
            Play(candidates[startIndex], candidates, startIndex, true);
        }
        catch (Exception exception)
        {
            if (playbackToken == _loadToken)
            {
                StatusText.Text = "Couldn't find similar tracks: " + exception.Message;
                PlayIcon.Glyph = "\uE768";
            }
        }
    }

    double GetNormalizationGain(TrackItem track)
    {
        if (!_normalizeVolume || !string.Equals(track.Source, "YT MUSIC", StringComparison.OrdinalIgnoreCase)) return 1;
        var loudness = _yt.GetLoudnessDb(track.Id);
        if (loudness is not double decibels) return 1;
        var target = _normalizationLevel switch
        {
            "Quiet" => -20.0,
            "Loud" => -11.0,
            _ => -14.0
        };
        return Math.Clamp(Math.Pow(10, (target - decibels) / 20), 0.4, 1.75);
    }

    int? GetAutomaticNextIndex()
    {
        if (_queue.Count < 2 || _index < 0) return null;
        if (_index + 1 < _queue.Count) return _index + 1;
        return _repeatMode == 1 ? 0 : null;
    }

    async Task WarmNextTrackAsync(int playbackToken)
    {
        var nextIndex = GetAutomaticNextIndex();
        if (nextIndex is null || nextIndex == _index) return;
        var next = _queue[nextIndex.Value];
        if (!next.IsOnline || next.IsStreamFresh) return;
        try
        {
            var url = next.Source == "YT MUSIC"
                ? await _yt.ResolveStreamUrlAsync(next.Id, quality: _streamingQuality)
                : await _sc.ResolveStreamUrlAsync(next.Id);
            if (playbackToken != _loadToken || !_queue.Contains(next)) return;
            next.Path = url;
            next.ResolvedAt = DateTimeOffset.UtcNow;
        }
        catch { }
    }

    List<TrackItem> ShuffleStartingWith(IReadOnlyList<TrackItem> tracks, TrackItem current)
    {
        var remaining = tracks.Where(item => !ReferenceEquals(item, current)).ToList();
        for (var i = remaining.Count - 1; i > 0; i--)
        {
            var j = Random.Shared.Next(i + 1);
            (remaining[i], remaining[j]) = (remaining[j], remaining[i]);
        }
        return new[] { current }.Concat(remaining).ToList();
    }

    void ShuffleButton_Click(object sender, RoutedEventArgs e)
    {
        _shuffleEnabled = !_shuffleEnabled;
        if (_shuffleEnabled && _queue.Count > 1 && _current is not null)
        {
            _unshuffledQueue = _queue.ToList();
            _queue = ShuffleStartingWith(_queue, _current);
            _index = 0;
        }
        else if (!_shuffleEnabled && _unshuffledQueue is not null)
        {
            var current = _current;
            _queue = _unshuffledQueue;
            _unshuffledQueue = null;
            _index = current is null ? -1 : _queue.FindIndex(item => ReferenceEquals(item, current));
        }
        UpdatePlaybackModeButtons();
        SaveSettings_Click(sender, e);
    }

    void RepeatButton_Click(object sender, RoutedEventArgs e)
    {
        _repeatMode = (_repeatMode + 1) % 3;
        UpdatePlaybackModeButtons();
        SaveSettings_Click(sender, e);
    }

    void UpdatePlaybackModeButtons()
    {
        if (ShuffleIcon is null) return;
        var blue = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 125, 211, 252));
        var idle = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 185, 185, 195));
        var activeWash = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(36, 56, 189, 248));
        var clear = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0));
        ShuffleIcon.Foreground = _shuffleEnabled ? blue : idle;
        ShuffleButton.Background = _shuffleEnabled ? activeWash : clear;
        ShuffleButton.BorderBrush = _shuffleEnabled ? blue : clear;
        RepeatIcon.Glyph = _repeatMode == 2 ? "\uE8ED" : "\uE8EE";
        RepeatIcon.Foreground = _repeatMode > 0 ? blue : idle;
        RepeatButton.Background = _repeatMode > 0 ? activeWash : clear;
        RepeatButton.BorderBrush = _repeatMode > 0 ? blue : clear;
        ToolTipService.SetToolTip(RepeatButton, _repeatMode switch
        {
            1 => "Repeat playlist",
            2 => "Repeat song",
            _ => "Repeat off"
        });
    }

    // ================= SEEK =================

    void UpdateProgress()
    {
        var duration = _player.Duration.TotalSeconds;
        var position = _player.Position.TotalSeconds;

        if (duration > 0)
        {
            DurationText.Text = FormatTime(duration);
            if (!_scrubbing) SetSeek(Math.Clamp(position / duration * 100, 0, 100));
        }
        ElapsedText.Text = FormatTime(position);
        if (_crossfadeEnabled && !_automaticFadeStarted && _player.IsPlaying && duration > 0 &&
            _repeatMode != 2 && GetAutomaticNextIndex() is { } nextIndex && nextIndex != _index &&
            duration - position <= _crossfadeSeconds)
        {
            _automaticFadeStarted = true;
            Play(_queue[nextIndex], _queue, nextIndex, true, automaticFade: true);
        }
        if (_discordDurationMissing && DateTimeOffset.UtcNow >= _discordDurationRetryAt &&
            _player.IsPlaying && _current is { } current && _player.Duration > TimeSpan.Zero)
            UpdateDiscordActivity(current);
    }

    void SetSeek(double percent)
    {
        _updatingSeek = true;
        SeekSlider.Value = percent;
        _updatingSeek = false;
    }

    void SeekSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_updatingSeek) return;
        var duration = _player.Duration.TotalSeconds;
        if (duration <= 0) return;
        _player.Seek(TimeSpan.FromSeconds(duration * e.NewValue / 100.0));
    }

    void SeekSlider_PointerPressed(object sender, PointerRoutedEventArgs e) => _scrubbing = true;
    void SeekSlider_PointerReleased(object sender, PointerRoutedEventArgs e) => _scrubbing = false;

    static string FormatTime(double seconds)
    {
        if (double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds < 0) seconds = 0;
        var t = TimeSpan.FromSeconds(seconds);
        return t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");
    }

    // ================= VOLUME (the fix) =================

    /// <summary>
    /// Slider is 0-100; square it for a perceptual volume taper.
    /// </summary>
    void ApplyVolume(double sliderValue)
    {
        sliderValue = Math.Clamp(sliderValue, 0, 100);
        _muted = sliderValue <= 0.5;
        _player.Volume = _muted ? 0 : Math.Pow(sliderValue / 100.0, 2.0);

        if (VolumeIcon is null) return;
        VolumeIcon.Glyph = sliderValue <= 0.5 ? "\uE74F"
                         : sliderValue < 40 ? "\uE993"
                         : "\uE767";
    }

    void VolumeSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (e.NewValue > 0.5) _lastVolume = e.NewValue;
        ApplyVolume(e.NewValue);
        if (!_initializing && _rememberVolume) SaveSettings_Click(sender, new RoutedEventArgs());
    }

    void Mute_Click(object sender, RoutedEventArgs e)
    {
        if (_muted)
        {
            VolumeSlider.Value = _lastVolume <= 0.5 ? 75 : _lastVolume;
        }
        else
        {
            _lastVolume = VolumeSlider.Value;
            VolumeSlider.Value = 0;
        }
    }

    void UpdateDiscordActivity(TrackItem track)
    {
        if (!_discordActivityEnabled) return;
        var duration = _player.Duration;
        if (duration <= TimeSpan.Zero && TimeSpan.TryParse(track.DurationLabel, out var parsed)) duration = parsed;
        _discordDurationMissing = duration <= TimeSpan.Zero;
        _discordDurationRetryAt = DateTimeOffset.UtcNow.AddSeconds(10);
        _ = PublishDiscordActivityAsync(track, duration);
    }

    async Task PublishDiscordActivityAsync(TrackItem track, TimeSpan duration)
    {
        try
        {
            var artistUrl = track.ArtistUrl;
            if (string.IsNullOrWhiteSpace(artistUrl) &&
                string.Equals(track.Source, "YT MUSIC", StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(track.ArtistId))
                artistUrl = "https://music.youtube.com/channel/" + Uri.EscapeDataString(track.ArtistId);
            await _discord.UpdateAsync(
            _discordApplicationId,
            track.Title,
            string.IsNullOrWhiteSpace(track.ArtistName) ? track.Subtitle : track.ArtistName,
            track.ArtworkUrl,
            track.WebUrl,
            artistUrl,
            duration,
            _player.Position);
            DispatcherQueue.TryEnqueue(() => UpdateDiscordConnectionControls());
        }
        catch (Exception ex)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                UpdateDiscordConnectionControls();
                ShowDiscordStatus(ex.Message);
            });
        }
    }

    // ================= DOWNLOAD =================

    async void Download_Click(object sender, RoutedEventArgs e)
    {
        if (_current is not { } track) { StatusText.Text = "Nothing to download."; return; }
        var format = await ChooseAudioFormatAsync();
        if (format is null) return;

        DownloadButton.IsEnabled = false;
        try
        {
            var progress = new Progress<double>(p => StatusText.Text = $"Downloading {track.Title}… {p:P0}");
            var outputFolder = GetMusicDownloadFolder();
            var file = await DownloadTrackAsFormatAsync(track, outputFolder, format, progress);
            StatusText.Text = $"Saved {format.ToUpperInvariant()} to {file}";
        }
        catch (Exception ex) { StatusText.Text = "Download failed: " + ex.Message; }
        finally { DownloadButton.IsEnabled = true; }
    }

    async void DownloadPlaylist_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedPlaylist is not { } playlist || playlist.Tracks.Count == 0)
        {
            StatusText.Text = "This playlist has no tracks to download.";
            return;
        }
        var format = await ChooseAudioFormatAsync();
        if (format is null) return;

        DownloadPlaylistButton.IsEnabled = false;
        var tracks = playlist.Tracks.ToList();
        var completed = 0;
        var failures = new List<string>();
        var outputFolder = GetMusicDownloadFolder();
        try
        {
            for (var i = 0; i < tracks.Count; i++)
            {
                var track = tracks[i];
                StatusText.Text = $"Downloading playlist: {i + 1}/{tracks.Count} — {track.Title}";
                var progress = new Progress<double>(p =>
                    StatusText.Text = $"Downloading playlist: {i + 1}/{tracks.Count} — {track.Title} {p:P0}");
                try
                {
                    await DownloadTrackAsFormatAsync(track, outputFolder, format, progress);
                    completed++;
                }
                catch (Exception ex) { failures.Add($"{track.Title}: {ex.Message}"); }
            }

            StatusText.Text = failures.Count == 0
                ? $"Downloaded all {completed} tracks as {format.ToUpperInvariant()} to {outputFolder}."
                : $"Downloaded {completed} of {tracks.Count} tracks. Failed: {string.Join("; ", failures.Take(3))}";
        }
        finally { DownloadPlaylistButton.IsEnabled = true; }
    }

    string GetMusicDownloadFolder()
    {
        var chosenFolder = string.IsNullOrWhiteSpace(_downloadFolder)
            ? Environment.GetFolderPath(Environment.SpecialFolder.MyMusic)
            : _downloadFolder;
        var folderName = Path.GetFileName(Path.TrimEndingDirectorySeparator(chosenFolder));
        var outputFolder = string.Equals(folderName, "Music", StringComparison.OrdinalIgnoreCase)
            ? chosenFolder
            : Path.Combine(chosenFolder, "Music");
        Directory.CreateDirectory(outputFolder);
        return outputFolder;
    }
    async Task<string?> ChooseAudioFormatAsync()
    {
        if (_preferredDownloadFormat == "mp3" || _preferredDownloadFormat == "wav")
            return _preferredDownloadFormat;
        var formatPicker = new ComboBox { MinWidth = 260 };
        formatPicker.Items.Add(new ComboBoxItem { Content = "MP3" });
        formatPicker.Items.Add(new ComboBoxItem { Content = "WAV" });
        formatPicker.SelectedIndex = 0;
        var dialog = new ContentDialog
        {
            Title = "Choose download format",
            Content = formatPicker,
            PrimaryButtonText = "Download",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = Root.XamlRoot
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return null;
        return formatPicker.SelectedIndex == 1 ? "wav" : "mp3";
    }

    async Task<string> DownloadTrackAsFormatAsync(TrackItem track, string outputFolder, string format,
        IProgress<double>? progress = null)
    {
        if (_organizeDownloadsByArtist)
        {
            var artistFolder = string.IsNullOrWhiteSpace(track.ArtistName) ? "Unknown Artist" : track.ArtistName;
            foreach (var character in Path.GetInvalidFileNameChars()) artistFolder = artistFolder.Replace(character, '_');
            outputFolder = Path.Combine(outputFolder, artistFolder.Trim().TrimEnd('.'));
        }
        Directory.CreateDirectory(outputFolder);
        if (!track.IsOnline && string.Equals(Path.GetExtension(track.Path), "." + format, StringComparison.OrdinalIgnoreCase))
        {
            var copyTarget = MediaDownloader.UniqueTarget(outputFolder, track.Title, format);
            File.Copy(track.Path, copyTarget);
            return copyTarget;
        }

        var workFolder = Path.Combine(Path.GetTempPath(), "RiskAudio-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workFolder);
        try
        {
            string sourcePath;
            if (!track.IsOnline)
            {
                sourcePath = track.Path;
            }
            else if (track.Source == "YT MUSIC")
            {
                sourcePath = await _yt.DownloadAsync(track.Id, workFolder, progress, quality: _streamingQuality);
            }
            else if (track.Source == "SOUNDCLOUD")
            {
                sourcePath = await _sc.ResolveStreamUrlAsync(track.Id);
            }
            else
            {
                throw new InvalidOperationException("This track source cannot be downloaded.");
            }

            StatusText.Text = $"Converting {track.Title} to {format.ToUpperInvariant()}…";
            return await MediaDownloader.ConvertAudioAsync(sourcePath, outputFolder, track.Title, format,
                mp3Bitrate: _mp3Bitrate);
        }
        finally
        {
            try { if (Directory.Exists(workFolder)) Directory.Delete(workFolder, true); } catch { }
        }
    }

    // ================= LIBRARY =================

    async void Import_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker();
        foreach (var ext in new[] { ".mp3", ".wav", ".flac", ".m4a", ".wma", ".aac", ".ogg", ".opus" })
            picker.FileTypeFilter.Add(ext);

        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
        var files = await picker.PickMultipleFilesAsync();
        if (files is null || files.Count == 0) { StatusText.Text = "No files added."; return; }

        foreach (var file in files)
        {
            if (_library.Any(t => string.Equals(t.Path, file.Path, StringComparison.OrdinalIgnoreCase))) continue;
            _library.Add(new TrackItem
            {
                Title = Path.GetFileNameWithoutExtension(file.Name),
                Subtitle = "Local file",
                Path = file.Path,
                Source = "LOCAL"
            });
        }

        StatusText.Text = $"Library now has {_library.Count} track(s).";
        ShowPage(LibraryPage);
        HydrateArtwork(_library);
    }

    // ================= PLAYLISTS =================

    void PlaylistList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (PlaylistList.SelectedItem is not PlaylistItem playlist) return;
        OpenPlaylist(playlist);
        PlaylistList.SelectedItem = null;
    }

    void PlaylistRow_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not Border surface || surface.DataContext is not PlaylistItem playlist) return;
        surface.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0));
        surface.BorderBrush = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0));
        surface.BorderThickness = new Thickness(0);
    }

    void OpenPlaylist(PlaylistItem playlist)
    {
        _selectedPlaylist = playlist;
        PlaylistDetailTitle.Text = playlist.Name;
        PlaylistDetailDescription.Text = playlist.Description;
        PlaylistDetailUpdated.Text = $"Updated {playlist.UpdatedAt.LocalDateTime:g}";
        PlaylistTrackCount.Text = playlist.Tracks.Count.ToString();
        PlaylistTracksList.ItemsSource = playlist.Tracks;
        DownloadPlaylistButton.IsEnabled = playlist.Tracks.Count > 0;
        PlaylistDetailCover.Source = playlist.Cover;
        HydrateArtwork(playlist.Tracks);
        ShowPage(PlaylistDetailPage);
    }

    void PlaylistBack_Click(object sender, RoutedEventArgs e) => ShowPage(LibraryPage);

    void PlaylistTracksList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_selectedPlaylist is not { } playlist || PlaylistTracksList.SelectedItem is not TrackItem track) return;
        Play(track, playlist.Tracks, playlist.Tracks.IndexOf(track), true);
        PlaylistTracksList.SelectedItem = null;
    }

    void PlayPlaylist_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedPlaylist is not { Tracks.Count: > 0 } playlist) return;
        Play(playlist.Tracks[0], playlist.Tracks, 0, true);
    }

    async void DeletePlaylist_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not PlaylistItem playlist) return;
        var dialog = new ContentDialog
        {
            Title = "Delete playlist?",
            Content = $"Are you sure you want to delete ‘{playlist.Name}’?",
            PrimaryButtonText = "Delete",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = Root.XamlRoot
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        _playlists.Remove(playlist);
        SavePlaylists();
        if (ReferenceEquals(_selectedPlaylist, playlist))
        {
            _selectedPlaylist = null;
            ShowPage(LibraryPage);
        }
    }

    void RemovePlaylistTrack_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedPlaylist is not { } playlist ||
            (sender as FrameworkElement)?.DataContext is not TrackItem track) return;
        if (!playlist.Tracks.Remove(track)) return;

        playlist.UpdatedAt = DateTimeOffset.Now;
        PlaylistTracksList.ItemsSource = null;
        PlaylistTracksList.ItemsSource = playlist.Tracks;
        PlaylistTrackCount.Text = playlist.Tracks.Count.ToString();
        DownloadPlaylistButton.IsEnabled = playlist.Tracks.Count > 0;
        PlaylistDetailUpdated.Text = $"Updated {playlist.UpdatedAt.LocalDateTime:g}";
        SavePlaylists();
    }

    async void ImportPlaylist_Click(object sender, RoutedEventArgs e)
    {
        var linkBox = new TextBox { PlaceholderText = "Paste a Spotify or online playlist link", MinWidth = 420 };
        var dialog = new ContentDialog
        {
            Title = "Import playlist",
            Content = linkBox,
            PrimaryButtonText = "Import",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = Root.XamlRoot
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        if (!Uri.TryCreate(linkBox.Text.Trim(), UriKind.Absolute, out var uri)) return;

        try
        {
            string name, description, artworkUrl;
            List<TrackItem> tracks;
            if (uri.Host.Equals("open.spotify.com", StringComparison.OrdinalIgnoreCase) || uri.Host.Equals("spotify.com", StringComparison.OrdinalIgnoreCase))
                (name, description, artworkUrl, tracks) = await _spotify.GetPlaylistAsync(uri);
            else if (uri.Host.Contains("soundcloud.com", StringComparison.OrdinalIgnoreCase))
                (name, description, artworkUrl, tracks) = await _sc.GetPlaylistAsync(uri.ToString());
            else if (uri.Host.Contains("youtube.com", StringComparison.OrdinalIgnoreCase) || uri.Host.Contains("youtu.be", StringComparison.OrdinalIgnoreCase))
                (name, description, artworkUrl, tracks) = await _yt.GetPlaylistAsync(uri.ToString());
            else throw new InvalidOperationException("Use a Spotify or supported online playlist link.");

            var playlist = new PlaylistItem
            {
                Name = string.IsNullOrWhiteSpace(name) ? "Imported playlist" : name,
                Description = description,
                CoverUrl = artworkUrl,
                Tracks = tracks,
                UpdatedAt = DateTimeOffset.Now
            };
            LoadPlaylistCover(playlist);
            _playlists.Add(playlist);
            SavePlaylists();
            PlaylistList.SelectedItem = null;
            OpenPlaylist(playlist);
            if (uri.Host.Contains("spotify", StringComparison.OrdinalIgnoreCase))
                StatusText.Text = $"Imported {tracks.Count} Spotify tracks. Online versions are matched for playback; Spotify's public embed provides up to 50 tracks.";
        }
        catch (Exception ex)
        {
            var error = new ContentDialog
            {
                Title = "Could not import playlist",
                Content = ex.Message,
                CloseButtonText = "OK",
                XamlRoot = Root.XamlRoot
            };
            await error.ShowAsync();
        }
    }

    async void CreatePlaylist_Click(object sender, RoutedEventArgs e)
    {
        var box = new TextBox { PlaceholderText = "Playlist name", MinWidth = 280 };
        var dialog = new ContentDialog
        {
            Title = "New playlist",
            Content = box,
            PrimaryButtonText = "Create",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = Root.XamlRoot
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        var name = box.Text.Trim();
        if (name.Length == 0) return;
        if (_playlists.Any(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            StatusText.Text = "That playlist already exists.";
            return;
        }

        _playlists.Add(new PlaylistItem { Name = name });
        SavePlaylists();
        StatusText.Text = "Created " + name;
    }

    async void EditPlaylist_Click(object sender, RoutedEventArgs e)
    {
        var playlist = (sender as FrameworkElement)?.DataContext as PlaylistItem ?? _selectedPlaylist;
        if (playlist is null) return;
        var editedTracks = new ObservableCollection<TrackItem>(playlist.Tracks);
        var nameBox = new TextBox { Text = playlist.Name, PlaceholderText = "Playlist title" };
        var descriptionBox = new TextBox
        {
            Text = playlist.Description,
            PlaceholderText = "Description",
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 90
        };
        var coverButton = new Button { Content = "Change playlist image" };
        coverButton.Click += async (_, _) => await PickPlaylistCoverAsync(playlist);
        var basicInfo = new StackPanel { Spacing = 12, MinWidth = 440, Margin = new Thickness(8) };
        basicInfo.Children.Add(coverButton);
        basicInfo.Children.Add(nameBox);
        basicInfo.Children.Add(descriptionBox);

        var trackRows = new StackPanel { Spacing = 4 };
        void RefreshTrackRows()
        {
            trackRows.Children.Clear();
            foreach (var track in editedTracks)
            {
                var row = new Grid { Height = 54, ColumnSpacing = 10, Padding = new Thickness(4, 2, 4, 2) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(42) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var cover = new Border { Width = 38, Height = 38, CornerRadius = new CornerRadius(6) };
                if (track.Artwork is not null)
                    cover.Child = new Image { Source = track.Artwork, Stretch = Stretch.UniformToFill };
                var metadata = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                metadata.Children.Add(new TextBlock { Text = track.Title, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
                metadata.Children.Add(new TextBlock { Text = track.ArtistName.Length > 0 ? track.ArtistName : track.Subtitle, FontSize = 12, Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 170, 170, 178)) });
                var remove = new Button { Content = "×", Width = 34, Height = 34, Padding = new Thickness(0) };
                ToolTipService.SetToolTip(remove, "Remove from playlist");
                remove.Click += (_, _) =>
                {
                    editedTracks.Remove(track);
                    RefreshTrackRows();
                };
                Grid.SetColumn(metadata, 1);
                Grid.SetColumn(remove, 2);
                row.Children.Add(cover);
                row.Children.Add(metadata);
                row.Children.Add(remove);
                trackRows.Children.Add(row);
            }
            if (editedTracks.Count == 0)
                trackRows.Children.Add(new TextBlock { Text = "No tracks in this playlist.", Margin = new Thickness(8), Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 170, 170, 178)) });
        }
        RefreshTrackRows();
        var tracksPage = new ScrollViewer { Content = trackRows, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Height = 300 };
        var tabs = new TabView { IsAddTabButtonVisible = false, MinHeight = 350 };
        tabs.TabItems.Add(new TabViewItem { Header = "Basic info", Content = basicInfo, IsClosable = false });
        tabs.TabItems.Add(new TabViewItem { Header = "Tracks", Content = tracksPage, IsClosable = false });
        var dialog = new ContentDialog
        {
            Title = "Edit playlist",
            Content = tabs,
            PrimaryButtonText = "Save",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = Root.XamlRoot
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        var name = nameBox.Text.Trim();
        if (name.Length == 0) return;
        playlist.Name = name;
        playlist.Description = descriptionBox.Text.Trim();
        playlist.Tracks = editedTracks.ToList();
        playlist.UpdatedAt = DateTimeOffset.Now;
        SavePlaylists();
        RefreshSidebarPlaylists();
        if (_selectedPlaylist == playlist) OpenPlaylist(playlist);
    }

    async Task PickPlaylistCoverAsync(PlaylistItem playlist)
    {
        var picker = new FileOpenPicker();
        foreach (var ext in new[] { ".png", ".jpg", ".jpeg", ".webp", ".bmp" }) picker.FileTypeFilter.Add(ext);
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
        var file = await picker.PickSingleFileAsync();
        if (file is null) return;
        try
        {
            var folder = Path.Combine(_dataDir, "playlist-covers");
            Directory.CreateDirectory(folder);
            var destination = Path.Combine(folder, Guid.NewGuid().ToString("N") + Path.GetExtension(file.Name));
            File.Copy(file.Path, destination, true);
            playlist.CoverPath = destination;
            playlist.CoverUrl = "";
            LoadPlaylistCover(playlist);
            RefreshSidebarPlaylists();
            if (_selectedPlaylist == playlist) PlaylistDetailCover.Source = playlist.Cover;
            playlist.UpdatedAt = DateTimeOffset.Now;
            SavePlaylists();
        }
        catch (Exception ex) { StatusText.Text = "Could not save the playlist cover: " + ex.Message; }
    }

    void MigrateLegacyDataDirectory()
    {
        var legacy = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VYBE");
        if (!Directory.Exists(legacy)) return;
        try
        {
            if (!Directory.Exists(_dataDir))
            {
                Directory.Move(legacy, _dataDir);
                return;
            }
            foreach (var directory in Directory.EnumerateDirectories(legacy, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(legacy, directory);
                Directory.CreateDirectory(Path.Combine(_dataDir, relative));
            }
            foreach (var file in Directory.EnumerateFiles(legacy, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(legacy, file);
                var destination = Path.Combine(_dataDir, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                if (!File.Exists(destination)) File.Copy(file, destination);
            }
        }
        catch { }
    }

    void LoadPlaylistCover(PlaylistItem playlist)
    {
        try
        {
            var source = playlist.CoverPath;
            var legacyData = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VYBE");
            var legacyPrefix = legacyData.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!File.Exists(source) && source.StartsWith(legacyPrefix, StringComparison.OrdinalIgnoreCase))
            {
                var relative = Path.GetRelativePath(legacyData, source);
                var migrated = Path.Combine(_dataDir, relative);
                if (File.Exists(migrated)) source = playlist.CoverPath = migrated;
            }
            if (!File.Exists(source)) source = playlist.CoverUrl;
            playlist.Cover = Uri.TryCreate(source, UriKind.Absolute, out var uri) ? new BitmapImage(uri) : null;
        }
        catch { playlist.Cover = null; }
    }

    void SavePlaylists() => SaveJson(Path.Combine(_dataDir, "playlists.json"), _playlists.ToList());

    // ================= SETTINGS =================

    void ThemeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Root is null || ThemeBox.SelectedIndex < 0) return;
        ApplySelectedTheme();
        if (!_initializing) SaveSettings_Click(sender, e);
    }

    void ApplySelectedTheme()
    {
        var theme = (ThemeBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "Dark";
        var light = theme == "Light";
        var palette = theme switch
        {
            "Midnight" => (Background: Windows.UI.Color.FromArgb(255, 12, 17, 38), Panel: Windows.UI.Color.FromArgb(255, 25, 33, 65), Card: Windows.UI.Color.FromArgb(255, 34, 44, 82), Stroke: Windows.UI.Color.FromArgb(255, 67, 83, 135), Accent: Windows.UI.Color.FromArgb(255, 128, 165, 255)),
            "Ocean" => (Background: Windows.UI.Color.FromArgb(255, 8, 30, 42), Panel: Windows.UI.Color.FromArgb(255, 14, 48, 62), Card: Windows.UI.Color.FromArgb(255, 20, 61, 77), Stroke: Windows.UI.Color.FromArgb(255, 43, 103, 119), Accent: Windows.UI.Color.FromArgb(255, 80, 225, 238)),
            "Rose" => (Background: Windows.UI.Color.FromArgb(255, 38, 17, 30), Panel: Windows.UI.Color.FromArgb(255, 58, 27, 44), Card: Windows.UI.Color.FromArgb(255, 70, 35, 54), Stroke: Windows.UI.Color.FromArgb(255, 115, 63, 88), Accent: Windows.UI.Color.FromArgb(255, 255, 145, 190)),
            "Forest" => (Background: Windows.UI.Color.FromArgb(255, 13, 32, 25), Panel: Windows.UI.Color.FromArgb(255, 23, 49, 37), Card: Windows.UI.Color.FromArgb(255, 31, 62, 46), Stroke: Windows.UI.Color.FromArgb(255, 56, 99, 73), Accent: Windows.UI.Color.FromArgb(255, 120, 225, 165)),
            "Light" => (Background: Windows.UI.Color.FromArgb(255, 245, 246, 250), Panel: Windows.UI.Color.FromArgb(255, 255, 255, 255), Card: Windows.UI.Color.FromArgb(255, 238, 240, 247), Stroke: Windows.UI.Color.FromArgb(255, 210, 213, 224), Accent: Windows.UI.Color.FromArgb(255, 35, 103, 190)),
            _ => (Background: Windows.UI.Color.FromArgb(255, 17, 18, 20), Panel: Windows.UI.Color.FromArgb(255, 27, 28, 31), Card: Windows.UI.Color.FromArgb(255, 37, 38, 42), Stroke: Windows.UI.Color.FromArgb(255, 59, 60, 65), Accent: Windows.UI.Color.FromArgb(255, 125, 211, 252))
        };
        Root.RequestedTheme = light ? ElementTheme.Light : ElementTheme.Dark;
        _lightTheme = light;
        // Tint the content root with the selected palette while retaining acrylic
        // translucency beneath it.
        SetThemeBrush("ThemeBackgroundBrush", Windows.UI.Color.FromArgb(224,
            palette.Background.R, palette.Background.G, palette.Background.B));
        Root.Background = (Microsoft.UI.Xaml.Media.Brush)Root.Resources["ThemeBackgroundBrush"];
        SetThemeBrush("GlassPanelBrush", Windows.UI.Color.FromArgb(174, palette.Panel.R, palette.Panel.G, palette.Panel.B));
        SetThemeBrush("CardBackgroundFillColorSecondaryBrush", palette.Card);
        SetThemeBrush("CardStrokeColorDefaultBrush", palette.Stroke);
        SetThemeBrush("ThemeAccentBrush", palette.Accent);
        SetThemeBrush("AccentTextFillColorPrimaryBrush", palette.Accent);
        SetThemeBrush("AccentFillColorDefaultBrush", palette.Accent);
        _themeAccent = palette.Accent;
        ApplyForegroundTheme(light);
        UpdateNavigationTheme();
    }

    void SetThemeBrush(string key, Windows.UI.Color color)
    {
        if (Root.Resources[key] is Microsoft.UI.Xaml.Media.SolidColorBrush brush)
            brush.Color = color;
        else
            Root.Resources[key] = new Microsoft.UI.Xaml.Media.SolidColorBrush(color);
    }

    void UpdateNavigationTheme()
    {
        if (HomeNav is null || LibraryNav is null || EqualizerNav is null || SettingsNav is null) return;
        foreach (var button in new[] { HomeNav, LibraryNav, EqualizerNav, SettingsNav })
        {
            var active = button.IsChecked == true;
            button.Background = active && button != SettingsNav
                ? new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(42, _themeAccent.R, _themeAccent.G, _themeAccent.B))
                : new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0));
            if (button.Content is StackPanel stack && stack.Children.FirstOrDefault() is FontIcon icon)
                icon.Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(active ? _themeAccent : _lightTheme
                    ? Windows.UI.Color.FromArgb(255, 70, 70, 76)
                    : Windows.UI.Color.FromArgb(255, 228, 228, 232));
        }
    }

    void ApplyForegroundTheme(bool light)
    {
        var foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(light
            ? Windows.UI.Color.FromArgb(255, 0, 0, 0)
            : Windows.UI.Color.FromArgb(255, 243, 240, 250));
        ApplyForegroundThemeTo(Root, foreground);
    }

    static void ApplyForegroundThemeTo(DependencyObject parent, Microsoft.UI.Xaml.Media.Brush foreground)
    {
        var count = VisualTreeHelper.GetChildrenCount(parent);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is TextBlock text) text.Foreground = foreground;
            if (child is Control control) control.Foreground = foreground;
            ApplyForegroundThemeTo(child, foreground);
        }
    }

    void CrossfadeToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;
        _crossfadeEnabled = CrossfadeToggle.IsOn;
        CrossfadeDurationSlider.IsEnabled = _crossfadeEnabled;
        SaveSettings_Click(sender, e);
    }

    void CrossfadeDurationSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        _crossfadeSeconds = Math.Clamp(e.NewValue, 0.5, 12);
        if (CrossfadeDurationText is not null)
            CrossfadeDurationText.Text = $"{_crossfadeSeconds:0.0} seconds";
        if (!_initializing) SaveSettings_Click(sender, new RoutedEventArgs());
    }

    async void ChangeFolder_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FolderPicker();
        picker.FileTypeFilter.Add("*");
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
        var folder = await picker.PickSingleFolderAsync();
        if (folder is null) return;
        _downloadFolder = folder.Path;
        DownloadFolderBox.Text = _downloadFolder;
    }

    void SaveSettings_Click(object sender, RoutedEventArgs e)
    {
        SaveJson(Path.Combine(_dataDir, "settings.json"), new AppSettings
        {
            Theme = (ThemeBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "Dark",
            DownloadFolder = _downloadFolder,
            DiscordApplicationId = SharedDiscordApplicationId,
            DiscordActivityEnabled = _discordActivityEnabled,
            CrossfadeEnabled = _crossfadeEnabled,
            CrossfadeSeconds = _crossfadeSeconds,
            AutoplaySimilarTracks = _autoplaySimilarTracks,
            NormalizeVolume = _normalizeVolume,
            NormalizationLevel = _normalizationLevel,
            StreamingQuality = _streamingQuality,
            ShuffleEnabled = _shuffleEnabled,
            RepeatMode = _repeatMode,
            StartWithWindows = _startWithWindows,
            RememberVolume = _rememberVolume,
            Volume = VolumeSlider.Value,
            SearchYouTubeMusic = _searchYouTubeMusic,
            SearchSoundCloud = _searchSoundCloud,
            SearchResultCount = _searchResultCount,
            PreferredDownloadFormat = _preferredDownloadFormat,
            Mp3Bitrate = _mp3Bitrate,
            OrganizeDownloadsByArtist = _organizeDownloadsByArtist,
            EqualizerGains = Enumerable.Range(0, _player.BandCount).Select(_player.GetBandGain).ToArray()
        });
        _discordApplicationId = SharedDiscordApplicationId;
        if (_discordActivityEnabled && _current is { } track && _player.IsPlaying) UpdateDiscordActivity(track);
        StatusText.Text = "Settings saved.";
    }

    void StartWithWindowsToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;
        _startWithWindows = StartWithWindowsToggle.IsOn;
        try
        {
            using var startupKey = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
            if (_startWithWindows)
            {
                var executable = Environment.ProcessPath ?? throw new InvalidOperationException("Risk executable path is unavailable.");
                startupKey.SetValue("Risk", $"\"{executable}\"");
            }
            else startupKey.DeleteValue("Risk", false);
            SaveSettings_Click(sender, e);
        }
        catch (Exception ex)
        {
            _startWithWindows = !_startWithWindows;
            StartWithWindowsToggle.IsOn = _startWithWindows;
            StatusText.Text = "Couldn't update startup setting: " + ex.Message;
        }
    }

    void RememberVolumeToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;
        _rememberVolume = RememberVolumeToggle.IsOn;
        SaveSettings_Click(sender, e);
    }

    void AutoplaySimilarToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;
        _autoplaySimilarTracks = AutoplaySimilarToggle.IsOn;
        SaveSettings_Click(sender, e);
    }

    void NormalizeVolumeToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;
        _normalizeVolume = NormalizeVolumeToggle.IsOn;
        NormalizationLevelBox.IsEnabled = _normalizeVolume;
        SaveSettings_Click(sender, e);
    }

    void NormalizationLevelBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing) return;
        _normalizationLevel = (NormalizationLevelBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "Normal";
        SaveSettings_Click(sender, new RoutedEventArgs());
    }

    void StreamingQualityBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing) return;
        _streamingQuality = (StreamingQualityBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "Auto";
        SaveSettings_Click(sender, new RoutedEventArgs());
    }

    void Mp3BitrateBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing) return;
        if (int.TryParse((Mp3BitrateBox.SelectedItem as ComboBoxItem)?.Tag?.ToString(), out var bitrate))
            _mp3Bitrate = bitrate;
        SaveSettings_Click(sender, new RoutedEventArgs());
    }

    void OrganizeDownloadsToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;
        _organizeDownloadsByArtist = OrganizeDownloadsToggle.IsOn;
        SaveSettings_Click(sender, e);
    }

    void SearchSourceToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;
        // Source selectors are intentionally hidden; keep both catalogues enabled
        // for primary-search then fallback behavior.
        _searchYouTubeMusic = true;
        _searchSoundCloud = true;
        SaveSettings_Click(sender, e);
    }

    void SearchResultCountSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        _searchResultCount = Math.Clamp((int)Math.Round(e.NewValue / 5) * 5, 5, 50);
        if (SearchResultCountText is not null) SearchResultCountText.Text = _searchResultCount.ToString();
        if (!_initializing) SaveSettings_Click(sender, new RoutedEventArgs());
    }

    void PreferredDownloadFormatBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing) return;
        _preferredDownloadFormat = (PreferredDownloadFormatBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "ask";
        SaveSettings_Click(sender, new RoutedEventArgs());
    }

    void DiscordActivityToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;
        _discordActivityEnabled = DiscordActivityToggle.IsOn;
        if (_discordActivityEnabled)
        {
            if (_current is { } track && _player.IsPlaying) UpdateDiscordActivity(track);
        }
        else _ = _discord.ClearAsync();
        SaveSettings_Click(sender, e);
    }

    async void AuthorizeDiscord_Click(object sender, RoutedEventArgs e)
    {
        ConnectDiscordButton.IsEnabled = false;
        ShowDiscordStatus("Connecting to Discord…");
        try
        {
            await _discord.AuthorizeAsync(SharedDiscordApplicationId);
            UpdateDiscordConnectionControls(true);
            HideDiscordStatus();
            if (_discordActivityEnabled && _current is { } track && _player.IsPlaying) UpdateDiscordActivity(track);
        }
        catch (Exception ex)
        {
            UpdateDiscordConnectionControls(false);
            ShowDiscordStatus(ex.Message);
        }
        finally { ConnectDiscordButton.IsEnabled = true; }
    }

    async void DisconnectDiscord_Click(object sender, RoutedEventArgs e)
    {
        DisconnectDiscordButton.IsEnabled = false;
        try
        {
            _discordActivityEnabled = false;
            DiscordActivityToggle.IsOn = false;
            await _discord.DisconnectAsync();
            UpdateDiscordConnectionControls(false);
            ShowDiscordStatus("Disconnected successfully.", TimeSpan.FromSeconds(2));
        }
        catch (Exception ex)
        {
            UpdateDiscordConnectionControls();
            ShowDiscordStatus(ex.Message);
        }
        finally { DisconnectDiscordButton.IsEnabled = true; }
    }

    void UpdateDiscordConnectionControls(bool? connected = null)
    {
        var isConnected = connected ?? _discord.IsAuthorized;
        ConnectDiscordButton.Visibility = isConnected ? Visibility.Collapsed : Visibility.Visible;
        DisconnectDiscordButton.Visibility = isConnected ? Visibility.Visible : Visibility.Collapsed;
    }

    void ShowDiscordStatus(string message, TimeSpan? duration = null)
    {
        _discordStatusTimer?.Stop();
        DiscordActivityStatus.Text = message;
        DiscordActivityStatus.Visibility = Visibility.Visible;
        if (duration is null) return;
        _discordStatusTimer ??= DispatcherQueue.CreateTimer();
        _discordStatusTimer.IsRepeating = false;
        _discordStatusTimer.Interval = duration.Value;
        _discordStatusTimer.Tick -= DiscordStatusTimer_Tick;
        _discordStatusTimer.Tick += DiscordStatusTimer_Tick;
        _discordStatusTimer.Start();
    }

    void DiscordStatusTimer_Tick(Microsoft.UI.Dispatching.DispatcherQueueTimer sender, object args)
    {
        sender.Stop();
        DiscordActivityStatus.Visibility = Visibility.Collapsed;
    }

    void HideDiscordStatus()
    {
        _discordStatusTimer?.Stop();
        DiscordActivityStatus.Visibility = Visibility.Collapsed;
    }

    void LoadSettings()
    {
        var musicFolder = Environment.GetFolderPath(Environment.SpecialFolder.MyMusic);
        var oldDefaultDownloadFolder = Path.Combine(musicFolder, "VYBE");
        _downloadFolder = Path.Combine(musicFolder, "Risk");

        try
        {
            var path = Path.Combine(_dataDir, "settings.json");
            if (File.Exists(path))
            {
                var s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path));
                if (s is not null)
                {
                    for (var index = 0; index < ThemeBox.Items.Count; index++)
                    {
                        if (string.Equals((ThemeBox.Items[index] as ComboBoxItem)?.Tag?.ToString(), s.Theme, StringComparison.OrdinalIgnoreCase))
                        {
                            ThemeBox.SelectedIndex = index;
                            break;
                        }
                    }
                    if (s.DownloadFolder.Length > 0)
                        _downloadFolder = string.Equals(Path.GetFullPath(s.DownloadFolder), Path.GetFullPath(oldDefaultDownloadFolder), StringComparison.OrdinalIgnoreCase)
                            ? Path.Combine(musicFolder, "Risk")
                            : s.DownloadFolder;
                    _savedEqualizerGains = s.EqualizerGains ?? Array.Empty<double>();
                    _discordApplicationId = SharedDiscordApplicationId;
                    _discordActivityEnabled = s.DiscordActivityEnabled;
                    _crossfadeEnabled = s.CrossfadeEnabled;
                    _crossfadeSeconds = Math.Clamp(s.CrossfadeSeconds, 0.5, 12);
                    _autoplaySimilarTracks = s.AutoplaySimilarTracks;
                    _normalizeVolume = s.NormalizeVolume;
                    _normalizationLevel = s.NormalizationLevel is "Quiet" or "Loud" ? s.NormalizationLevel : "Normal";
                    _streamingQuality = s.StreamingQuality is "Low" or "Normal" or "High" or "VeryHigh"
                        ? s.StreamingQuality
                        : "Auto";
                    _shuffleEnabled = s.ShuffleEnabled;
                    _repeatMode = Math.Clamp(s.RepeatMode, 0, 2);
                    _startWithWindows = s.StartWithWindows;
                    _rememberVolume = s.RememberVolume;
                    _savedVolume = s.Volume;
                    _searchYouTubeMusic = true;
                    _searchSoundCloud = true;
                    _searchResultCount = Math.Clamp(s.SearchResultCount, 5, 50);
                    _preferredDownloadFormat = s.PreferredDownloadFormat is "mp3" or "wav"
                        ? s.PreferredDownloadFormat
                        : "ask";
                    _mp3Bitrate = s.Mp3Bitrate is 128 or 192 or 256 or 320 ? s.Mp3Bitrate : 320;
                    _organizeDownloadsByArtist = s.OrganizeDownloadsByArtist;
                }
            }

            var playlistsPath = Path.Combine(_dataDir, "playlists.json");
            if (File.Exists(playlistsPath))
            {
                var saved = JsonSerializer.Deserialize<List<PlaylistItem>>(File.ReadAllText(playlistsPath));
                if (saved is not null)
                    foreach (var p in saved)
                    {
                        LoadPlaylistCover(p);
                        _playlists.Add(p);
                    }
            }
        }
        catch { StatusText.Text = "Couldn't load settings; using defaults."; }

        if (ThemeBox.SelectedIndex < 0) ThemeBox.SelectedIndex = 0;
        ApplySelectedTheme();
        DownloadFolderBox.Text = _downloadFolder;
        CrossfadeToggle.IsOn = _crossfadeEnabled;
        CrossfadeDurationSlider.Value = _crossfadeSeconds;
        CrossfadeDurationSlider.IsEnabled = _crossfadeEnabled;
        CrossfadeDurationText.Text = $"{_crossfadeSeconds:0.0} seconds";
        AutoplaySimilarToggle.IsOn = _autoplaySimilarTracks;
        NormalizeVolumeToggle.IsOn = _normalizeVolume;
        NormalizationLevelBox.IsEnabled = _normalizeVolume;
        for (var index = 0; index < NormalizationLevelBox.Items.Count; index++)
        {
            if (string.Equals((NormalizationLevelBox.Items[index] as ComboBoxItem)?.Tag?.ToString(),
                    _normalizationLevel, StringComparison.OrdinalIgnoreCase))
            {
                NormalizationLevelBox.SelectedIndex = index;
                break;
            }
        }
        for (var index = 0; index < StreamingQualityBox.Items.Count; index++)
        {
            if (string.Equals((StreamingQualityBox.Items[index] as ComboBoxItem)?.Tag?.ToString(),
                    _streamingQuality, StringComparison.OrdinalIgnoreCase))
            {
                StreamingQualityBox.SelectedIndex = index;
                break;
            }
        }
        StartWithWindowsToggle.IsOn = _startWithWindows;
        RememberVolumeToggle.IsOn = _rememberVolume;
        _searchYouTubeMusic = true;
        _searchSoundCloud = true;
        SearchYouTubeToggle.IsOn = true;
        SearchSoundCloudToggle.IsOn = true;
        SearchResultCountSlider.Value = _searchResultCount;
        SearchResultCountText.Text = _searchResultCount.ToString();
        for (var index = 0; index < PreferredDownloadFormatBox.Items.Count; index++)
        {
            if (string.Equals((PreferredDownloadFormatBox.Items[index] as ComboBoxItem)?.Tag?.ToString(),
                    _preferredDownloadFormat, StringComparison.OrdinalIgnoreCase))
            {
                PreferredDownloadFormatBox.SelectedIndex = index;
                break;
            }
        }
        for (var index = 0; index < Mp3BitrateBox.Items.Count; index++)
        {
            if (string.Equals((Mp3BitrateBox.Items[index] as ComboBoxItem)?.Tag?.ToString(),
                    _mp3Bitrate.ToString(), StringComparison.OrdinalIgnoreCase))
            {
                Mp3BitrateBox.SelectedIndex = index;
                break;
            }
        }
        OrganizeDownloadsToggle.IsOn = _organizeDownloadsByArtist;
        VolumeSlider.Value = _rememberVolume ? Math.Clamp(_savedVolume, 0, 100) : 75;
    }

    static void SaveJson<T>(string path, T value)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }

    sealed class AppSettings
    {
        public string Theme { get; set; } = "Dark";
        public string DownloadFolder { get; set; } = "";
        public string DiscordApplicationId { get; set; } = "";
        public bool DiscordActivityEnabled { get; set; } = true;
        public bool CrossfadeEnabled { get; set; }
        public double CrossfadeSeconds { get; set; } = 3;
        public bool AutoplaySimilarTracks { get; set; } = true;
        public bool NormalizeVolume { get; set; } = true;
        public string NormalizationLevel { get; set; } = "Normal";
        public string StreamingQuality { get; set; } = "Auto";
        public bool ShuffleEnabled { get; set; }
        public int RepeatMode { get; set; }
        public bool StartWithWindows { get; set; }
        public bool RememberVolume { get; set; }
        public double Volume { get; set; } = 75;
        public bool SearchYouTubeMusic { get; set; } = true;
        public bool SearchSoundCloud { get; set; } = true;
        public int SearchResultCount { get; set; } = 25;
        public string PreferredDownloadFormat { get; set; } = "ask";
        public int Mp3Bitrate { get; set; } = 320;
        public bool OrganizeDownloadsByArtist { get; set; }
        public double[]? EqualizerGains { get; set; } = Array.Empty<double>();
    }

    void BuildEqualizerBands()
    {
        EqualizerBandsHost.Children.Clear();
        for (var i = 0; i < _player.BandCount; i++)
        {
            var index = i;
            var frequency = _player.GetBandFrequency(i);
            var label = frequency >= 1000 ? $"{frequency / 1000:0.#} kHz" : $"{frequency:0} Hz";
            var slider = new Slider
            {
                Minimum = -12, Maximum = 12, StepFrequency = 0.5,
                Orientation = Orientation.Vertical, Height = 170, Width = 42,
                Value = _player.GetBandGain(i),
                Header = "dB", IsThumbToolTipEnabled = true
            };
            slider.ValueChanged += (_, e) => _player.SetBandGain(index, e.NewValue);
            var column = new StackPanel { Width = 66, HorizontalAlignment = HorizontalAlignment.Center, Spacing = 4 };
            column.Children.Add(slider);
            column.Children.Add(new TextBlock { Text = label, HorizontalAlignment = HorizontalAlignment.Center, FontSize = 11 });
            EqualizerBandsHost.Children.Add(column);
        }
        if (_player.BandCount == 0) EqualizerStatus.Text = "No equalizer bands were provided by this audio device.";
    }

    void EqualizerPresetBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_player.BandCount == 0 || EqualizerPresetBox.SelectedIndex < 0) return;
        for (var i = 0; i < _player.BandCount; i++)
        {
            var f = _player.GetBandFrequency(i);
            var gain = EqualizerPresetBox.SelectedIndex switch
            {
                1 => f < 150 ? 6 : f < 400 ? 3 : f > 8000 ? -1.5 : 0,
                2 => f >= 300 && f <= 4000 ? 3.5 : f < 100 ? -1 : f > 10000 ? -2 : 0,
                3 => f >= 4000 ? 5 : f <= 100 ? -2 : 0,
                4 => f <= 100 ? 4 : f <= 250 ? 2 : f >= 4000 ? 4 : f >= 500 && f <= 2000 ? -1 : 0,
                _ => 0
            };
            _player.SetBandGain(i, gain);
            if (EqualizerBandsHost.Children.Count > i && EqualizerBandsHost.Children[i] is StackPanel column && column.Children[0] is Slider slider)
                slider.Value = gain;
        }
    }
}

