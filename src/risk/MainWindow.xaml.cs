using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.Web.WebView2.Core;
using System.Collections.ObjectModel;
using System.Text.Json;
using Windows.Media.Audio;
using Windows.Media.Core;
using Windows.Storage.Pickers;
using Vybe.App.Services;
using WinRT.Interop;

namespace Vybe.App;

public sealed partial class MainWindow : Window
{
    const string SharedDiscordApplicationId = "1540121277036109854";
    readonly EqualizedAudioPlayer _player = new();
    readonly YouTubeMusicService _yt = new();
    readonly SoundCloudService _sc = new();
    readonly DiscordPresenceService _discord = new();
    readonly HttpClient _soundCloudEmbedHttp = new();
    readonly WebView2 _playlistImportWebView;

    readonly ObservableCollection<TrackItem> _results = new();
    readonly ObservableCollection<TrackItem> _library = new();
    readonly ObservableCollection<TrackItem> _profileTracks = new();
    readonly ObservableCollection<PlaylistItem> _playlists = new();
    PlaylistItem? _selectedPlaylist;

    List<TrackItem> _queue = new();
    List<TrackItem>? _unshuffledQueue;
    int _index = -1;
    TrackItem? _current;
    bool _shuffleEnabled;
    int _repeatMode;
    bool _automaticFadeStarted;
    bool _scrubbing, _updatingSeek, _muted;
    double _lastVolume = 75;
    int _loadToken;
    string _downloadFolder = "";
    readonly string _dataDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VYBE");
    double[] _savedEqualizerGains = Array.Empty<double>();
    string _discordApplicationId = SharedDiscordApplicationId;
    bool _discordActivityEnabled = true;
    bool _discordDurationMissing;
    bool _crossfadeEnabled;
    double _crossfadeSeconds = 3;
    bool _initializing = true;
    Microsoft.UI.Dispatching.DispatcherQueueTimer? _progressTimer;
    Microsoft.UI.Dispatching.DispatcherQueueTimer? _discordStatusTimer;
    CancellationTokenSource? _searchCancellation;

    public MainWindow()
    {
        InitializeComponent();
        _playlistImportWebView = new WebView2
        {
            Width = 1,
            Height = 1,
            Opacity = 0,
            IsHitTestVisible = false,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top
        };
        Root.Children.Add(_playlistImportWebView);
        Title = "Risk";
        try { SystemBackdrop = new DesktopAcrylicBackdrop(); } catch { }
        try
        {
            var icon = Path.Combine(AppContext.BaseDirectory, "Assets", "app-icon-v2.ico");
            var logo = Path.Combine(AppContext.BaseDirectory, "Assets", "app-icon.png");
            if (File.Exists(icon)) AppWindow.SetIcon(icon);
            if (File.Exists(logo)) AppLogo.Source = new BitmapImage(new Uri(logo));
        }
        catch { }

        ResultsList.ItemsSource = _results;
        LibraryList.ItemsSource = _library;
        ProfileTracksList.ItemsSource = _profileTracks;
        PlaylistList.ItemsSource = _playlists;
        _player.MediaEnded += () => DispatcherQueue.TryEnqueue(HandleMediaEnded);
        _player.PlaybackFailed += message => DispatcherQueue.TryEnqueue(() => StatusText.Text = "Playback failed: " + message);
        Closed += async (_, _) =>
        {
            _searchCancellation?.Cancel();
            await _discord.ClearAsync();
            _progressTimer?.Stop();
            _discordStatusTimer?.Stop();
            _discord.Dispose();
            _player.Dispose();
        };

        LoadSettings();
        DiscordActivityToggle.IsOn = _discordActivityEnabled;
        UpdatePlaybackModeButtons();
        _initializing = false;
        ApplyVolume(VolumeSlider.Value);
        _progressTimer = DispatcherQueue.CreateTimer();
        _progressTimer.Interval = TimeSpan.FromMilliseconds(500);
        _progressTimer.IsRepeating = true;
        _progressTimer.Tick += (_, _) => UpdateProgress();
        _progressTimer.Start();
        _ = InitializeAudioAsync();
        ShowPage(HomePage);

        ResizeWindow();
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
        var compact = width < 980;
        var narrow = width < 680;
        Root.ColumnDefinitions[0].Width = new GridLength(narrow ? 60 : 76);
        SearchHeader.Margin = width < 760 ? new Thickness(14, 12, 14, 8) : new Thickness(28, 20, 28, 12);
        ContentArea.Margin = width < 760 ? new Thickness(14, 0, 14, 0) : new Thickness(28, 0, 28, 0);

        LibraryPage.ColumnDefinitions[1].Width = compact ? new GridLength(0) : new GridLength(260);
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
        HomePage.Visibility = page == HomePage ? Visibility.Visible : Visibility.Collapsed;
        LibraryPage.Visibility = page == LibraryPage ? Visibility.Visible : Visibility.Collapsed;
        SettingsPage.Visibility = page == SettingsPage ? Visibility.Visible : Visibility.Collapsed;
        ProfilePage.Visibility = page == ProfilePage ? Visibility.Visible : Visibility.Collapsed;
        EqualizerPage.Visibility = page == EqualizerPage ? Visibility.Visible : Visibility.Collapsed;
        PlaylistDetailPage.Visibility = page == PlaylistDetailPage ? Visibility.Visible : Visibility.Collapsed;

        HomeNav.IsChecked = page == HomePage;
        LibraryNav.IsChecked = page == LibraryPage;
        SettingsNav.IsChecked = page == SettingsPage;
        EqualizerNav.IsChecked = page == EqualizerPage;
    }

    void HomeNav_Click(object sender, RoutedEventArgs e) => ShowPage(HomePage);
    void LibraryNav_Click(object sender, RoutedEventArgs e) => ShowPage(LibraryPage);
    void SettingsNav_Click(object sender, RoutedEventArgs e) => ShowPage(SettingsPage);
    void EqualizerNav_Click(object sender, RoutedEventArgs e) => ShowPage(EqualizerPage);

    // ================= SEARCH =================

    async void SearchBox_QuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        var query = args.QueryText.Trim();
        if (query.Length == 0) return;

        _searchCancellation?.Cancel();
        var searchCancellation = new CancellationTokenSource();
        _searchCancellation = searchCancellation;
        var cancellationToken = searchCancellation.Token;

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
                        HomeHeading.Text = "YouTube Music link";
                        var item = new TrackItem
                        {
                            Title = "YouTube track",
                            Subtitle = "YouTube Music",
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

            // Otherwise: search both sources in parallel.
            HomeHeading.Text = $"Results for \u201c{query}\u201d";
            StatusText.Text = "Searching YouTube Music and SoundCloud…";

            var ytTask = _yt.SearchAsync(query, 25, cancellationToken);
            var scTask = _sc.SearchAsync(query, 15, cancellationToken);
            var remaining = new HashSet<Task<List<TrackItem>>> { ytTask, scTask };
            var ytCount = 0;
            var scCount = 0;
            var errors = new List<string>();

            while (remaining.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var completed = await Task.WhenAny(remaining);
                remaining.Remove(completed);
                try
                {
                    var tracks = await completed;
                    if (!ReferenceEquals(_searchCancellation, searchCancellation)) return;
                    if (ReferenceEquals(completed, ytTask)) ytCount = tracks.Count;
                    else scCount = tracks.Count;
                    foreach (var track in tracks) _results.Add(track);
                    HydrateArtwork(tracks);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    errors.Add(ex.Message);
                }

                if (remaining.Count > 0)
                    StatusText.Text = _results.Count > 0
                        ? $"Found {_results.Count} tracks; checking the other source…"
                        : "Still searching the other source…";
            }

            if (errors.Count > 0 && _results.Count == 0)
                StatusText.Text = "Search failed: " + string.Join("; ", errors);
            else if (_results.Count == 0)
                StatusText.Text = "Nothing found. Try different wording.";
            else
                StatusText.Text = $"{_results.Count} tracks · YouTube Music {ytCount} · SoundCloud {scCount}";
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

    static string? ExtractVideoId(string url)
    {
        var m = System.Text.RegularExpressions.Regex.Match(url, @"(?:v=|youtu\.be/|/shorts/)([A-Za-z0-9_-]{11})");
        return m.Success ? m.Groups[1].Value : null;
    }

    void HydrateArtwork(IEnumerable<TrackItem> items)
    {
        foreach (var item in items)
        {
            if (item.Artwork is not null ||
                !Uri.TryCreate(item.ArtworkUrl, UriKind.Absolute, out var uri) ||
                (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)) continue;
            try
            {
                // Loading from the URI avoids converting a non-seekable HttpClient
                // response stream to IRandomAccessStream, which can fail on Windows.
                item.Artwork = new BitmapImage { DecodePixelWidth = 128, UriSource = uri };
            }
            catch { /* the row's music-note placeholder remains visible */ }
        }
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
        ShowPage(ProfilePage);
        ProfileHeading.Text = track.ArtistName;
        ProfileDescription.Text = "Loading artist tracks…";
        _profileTracks.Clear();
        try
        {
            var result = track.Source == "YT MUSIC"
                ? await _yt.GetArtistTracksAsync(track.ArtistId)
                : await _sc.GetArtistTracksAsync(track.ArtistUrl);
            ProfileHeading.Text = result.Name;
            foreach (var item in result.Tracks) _profileTracks.Add(item);
            HydrateArtwork(_profileTracks);
            ProfileDescription.Text = $"{_profileTracks.Count} tracks · Select a song to play";
            if (_profileTracks.Count == 0) ProfileDescription.Text = "No tracks were available for this artist.";
        }
        catch (Exception ex) { ProfileDescription.Text = "Could not load artist: " + ex.Message; }
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

        NowTitle.Text = track.Title;
        NowArtistButton.Content = track.ArtistName.Length > 0 ? track.ArtistName : track.Subtitle;
        NowArtistButton.Visibility = NowArtistButton.Content?.ToString()?.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        NowArtistButton.IsEnabled = track.ArtistName.Length > 0;
        NowArtwork.Source = track.Artwork;
        ElapsedText.Text = "0:00";
        DurationText.Text = track.DurationLabel.Length > 0 ? track.DurationLabel : "0:00";
        SetSeek(0);

        try
        {
            string url = track.Path;

            if (track.IsOnline && (forceResolve || !track.IsStreamFresh))
            {
                StatusText.Text = $"Resolving {track.Source} stream…";
                url = track.Source == "YT MUSIC"
                    ? await _yt.ResolveStreamUrlAsync(track.Id, force: forceResolve)
                    : await _sc.ResolveStreamUrlAsync(track.Id);

                if (token != _loadToken) return;   // user clicked something else

                track.Path = url;
                track.ResolvedAt = DateTimeOffset.UtcNow;
            }

            await _player.PlayAsync(url, autoplay && _crossfadeEnabled, TimeSpan.FromSeconds(_crossfadeSeconds));
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
        var rows = new StackPanel { Spacing = 6 };
        var pickerStatus = new TextBlock { Visibility = Visibility.Collapsed, TextWrapping = TextWrapping.Wrap };
        var filter = new TextBox { PlaceholderText = "Filter playlists", MinWidth = 440 };
        filter.TextChanged += (_, _) => RebuildPlaylistPickerRows(track, filter.Text, rows, pickerStatus);
        RebuildPlaylistPickerRows(track, "", rows, pickerStatus);

        var content = new StackPanel { Spacing = 10, MinWidth = 480 };
        content.Children.Add(filter);
        content.Children.Add(pickerStatus);
        content.Children.Add(new ScrollViewer { Content = rows, MaxHeight = 320 });

        var dialog = new ContentDialog
        {
            Title = "Add to playlist",
            Content = content,
            CloseButtonText = "Done",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = Root.XamlRoot
        };
        await dialog.ShowAsync();
    }

    void RebuildPlaylistPickerRows(TrackItem track, string filter, StackPanel rows, TextBlock pickerStatus)
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
            var alreadyAdded = playlist.Tracks.Any(existing => SameTrack(existing, track));
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
            cover.Children.Add(new TextBlock
            {
                Text = "♫",
                FontSize = 20,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            });
            if (playlist.Cover is not null)
                cover.Children.Add(new Image { Source = playlist.Cover, Stretch = Stretch.UniformToFill });
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

            var stateLabelText = new TextBlock
            {
                Text = alreadyAdded ? "Added" : "Add",
                TextAlignment = TextAlignment.Center
            };
            var stateLabel = new Border
            {
                MinWidth = 76,
                VerticalAlignment = VerticalAlignment.Center,
                Padding = new Thickness(10, 6, 10, 6),
                CornerRadius = new CornerRadius(6),
                Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                    Windows.UI.Color.FromArgb(36, 255, 255, 255)),
                Child = stateLabelText
            };
            Grid.SetColumn(stateLabel, 2);
            rowGrid.Children.Add(stateLabel);

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
                var wasAdded = playlist.Tracks.Any(existing => SameTrack(existing, track));
                if (wasAdded)
                    playlist.Tracks.RemoveAll(existing => SameTrack(existing, track));
                else
                    playlist.Tracks.Add(track);

                playlist.UpdatedAt = DateTimeOffset.Now;
                SavePlaylists();
                if (ReferenceEquals(_selectedPlaylist, playlist))
                {
                    PlaylistTrackCount.Text = playlist.Tracks.Count.ToString();
                    PlaylistTracksList.ItemsSource = null;
                    PlaylistTracksList.ItemsSource = playlist.Tracks;
                }

                var added = !wasAdded;
                stateLabelText.Text = added ? "Added" : "Add";
                trackCountText.Text = $"{playlist.Tracks.Count} tracks";
                pickerStatus.Text = added ? $"Added to {playlist.Name}." : $"Removed from {playlist.Name}.";
                pickerStatus.Visibility = Visibility.Visible;
            };
            rows.Children.Add(rowButton);
        }
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
        PlayIcon.Glyph = "\uE768";
        _ = _discord.ClearAsync();
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
                ? await _yt.ResolveStreamUrlAsync(next.Id)
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
        if (_discordDurationMissing && _player.IsPlaying && _current is { } current && _player.Duration > TimeSpan.Zero)
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
        _ = PublishDiscordActivityAsync(track, duration);
    }

    async Task PublishDiscordActivityAsync(TrackItem track, TimeSpan duration)
    {
        try
        {
            await _discord.UpdateAsync(
            _discordApplicationId,
            track.Title,
            string.IsNullOrWhiteSpace(track.ArtistName) ? track.Subtitle : track.ArtistName,
            track.ArtworkUrl,
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
                sourcePath = await _yt.DownloadAsync(track.Id, workFolder, progress);
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
            return await MediaDownloader.ConvertAudioAsync(sourcePath, outputFolder, track.Title, format);
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

    async void DeletePlaylist_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not PlaylistItem playlist) return;
        var dialog = new ContentDialog
        {
            Title = "Delete playlist?",
            Content = $"Are you sure you want to delete \"{playlist.Name}\"?",
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
            PlaylistTracksList.ItemsSource = null;
            ShowPage(LibraryPage);
        }
    }

    void RemovePlaylistTrack_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedPlaylist is not { } playlist || (sender as FrameworkElement)?.DataContext is not TrackItem track) return;
        playlist.Tracks.Remove(track);
        playlist.UpdatedAt = DateTimeOffset.Now;
        PlaylistTrackCount.Text = playlist.Tracks.Count.ToString();
        DownloadPlaylistButton.IsEnabled = playlist.Tracks.Count > 0;
        PlaylistDetailUpdated.Text = $"Updated {playlist.UpdatedAt.LocalDateTime:g}";
        PlaylistTracksList.ItemsSource = null;
        PlaylistTracksList.ItemsSource = playlist.Tracks;
        SavePlaylists();
    }

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

    async void ImportPlaylist_Click(object sender, RoutedEventArgs e)
    {
        var linkBox = new TextBox { PlaceholderText = "Paste a SoundCloud or YouTube Music playlist link", MinWidth = 380 };
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
            var convertingFromSoundCloud = uri.Host.Contains("soundcloud.com", StringComparison.OrdinalIgnoreCase);
            if (convertingFromSoundCloud)
            {
                try
                {
                    (name, description, artworkUrl, tracks) = await _sc.GetPlaylistAsync(uri.ToString());
                }
                catch (Exception ex) when (ex is InvalidOperationException || ex is HttpRequestException { StatusCode: System.Net.HttpStatusCode.Forbidden or System.Net.HttpStatusCode.NotFound })
                {
                    (name, description, artworkUrl, tracks) = await ImportSoundCloudPlaylistFromWidgetAsync(uri);
                }
            }
            else if (uri.Host.Contains("youtube.com", StringComparison.OrdinalIgnoreCase) || uri.Host.Contains("youtu.be", StringComparison.OrdinalIgnoreCase))
                (name, description, artworkUrl, tracks) = await _yt.GetPlaylistAsync(uri.ToString());
            else throw new InvalidOperationException("Use a SoundCloud or YouTube Music playlist link.");

            var soundCloudTrackCount = tracks.Count;
            var unmatchedSoundCloudTracks = 0;
            if (convertingFromSoundCloud)
                (tracks, unmatchedSoundCloudTracks) = await ConvertSoundCloudTracksToYouTubeMusicAsync(tracks);
            if (tracks.Count == 0 && convertingFromSoundCloud)
                throw new InvalidOperationException("No YouTube Music matches were found for this SoundCloud playlist.");

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
            if (convertingFromSoundCloud)
                StatusText.Text = $"Added {tracks.Count} YouTube Music matches from {soundCloudTrackCount} SoundCloud songs" +
                                  (unmatchedSoundCloudTracks > 0 ? $" · skipped {unmatchedSoundCloudTracks} without a close match" : "");
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

    async Task<(List<TrackItem> Tracks, int Unmatched)> ConvertSoundCloudTracksToYouTubeMusicAsync(List<TrackItem> sourceTracks)
    {
        var converted = new List<TrackItem>();
        var usedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int unmatched = 0;
        for (var i = 0; i < sourceTracks.Count; i++)
        {
            var source = sourceTracks[i];
            StatusText.Text = $"Finding YouTube Music matches · {i + 1} of {sourceTracks.Count}";
            var query = string.Join(" ", new[] { source.Title, source.ArtistName }.Where(s => !string.IsNullOrWhiteSpace(s)));
            try
            {
                var candidates = await _yt.SearchAsync(query, 10);
                var best = candidates
                    .Select(candidate => (Track: candidate, Score: ScoreTrackMatch(source, candidate)))
                    .Where(item => item.Score >= 0.62 && !usedIds.Contains(item.Track.Id))
                    .OrderByDescending(item => item.Score)
                    .Select(item => item.Track)
                    .FirstOrDefault();
                if (best is null) { unmatched++; continue; }
                usedIds.Add(best.Id);
                converted.Add(best);
            }
            catch
            {
                unmatched++;
            }
        }
        return (converted, unmatched);
    }

    static double ScoreTrackMatch(TrackItem source, TrackItem candidate)
    {
        static string[] Tokens(string value) => new string(value.ToLowerInvariant()
                .Select(c => char.IsLetterOrDigit(c) ? c : ' ').ToArray())
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(token => token is not ("official" or "audio" or "video" or "lyrics" or "lyric" or "visualizer" or "topic" or "feat" or "ft"))
            .ToArray();

        var sourceTitle = Tokens(source.Title);
        var candidateTitle = Tokens(candidate.Title);
        if (sourceTitle.Length == 0 || candidateTitle.Length == 0) return 0;
        var titleOverlap = sourceTitle.Intersect(candidateTitle).Count() / (double)sourceTitle.Distinct().Count();
        var sourceArtist = Tokens(source.ArtistName);
        var candidateArtist = Tokens(candidate.ArtistName);
        var artistOverlap = sourceArtist.Length == 0 ? 0.5 :
            sourceArtist.Intersect(candidateArtist).Count() / (double)sourceArtist.Distinct().Count();
        var durationScore = 0.5;
        if (TimeSpan.TryParse(source.DurationLabel, out var sourceDuration) &&
            TimeSpan.TryParse(candidate.DurationLabel, out var candidateDuration) && sourceDuration.TotalSeconds > 0)
        {
            var difference = Math.Abs(sourceDuration.TotalSeconds - candidateDuration.TotalSeconds);
            durationScore = Math.Max(0, 1 - difference / Math.Max(20, sourceDuration.TotalSeconds * 0.2));
        }
        return titleOverlap * 0.72 + artistOverlap * 0.20 + durationScore * 0.08;
    }

    async Task<(string Name, string Description, string ArtworkUrl, List<TrackItem> Tracks)> ImportSoundCloudPlaylistFromWidgetAsync(Uri playlistUri)
    {
        try
        {
            await _playlistImportWebView.EnsureCoreWebView2Async();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("Could not start the built-in web player needed to read this SoundCloud playlist.", ex);
        }

        var completion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnMessage(CoreWebView2 sender, CoreWebView2WebMessageReceivedEventArgs args)
        {
            sender.WebMessageReceived -= OnMessage;
            try { completion.TrySetResult(args.TryGetWebMessageAsString()); }
            catch (Exception ex) { completion.TrySetException(ex); }
        }
        _playlistImportWebView.CoreWebView2.WebMessageReceived += OnMessage;

        var encodedUrl = Uri.EscapeDataString(playlistUri.ToString());
        var html = """
            <!doctype html><html><head><meta charset="utf-8"></head><body>
            <iframe id="sc" width="1" height="1" allow="autoplay" style="border:0"
              src="https://w.soundcloud.com/player/?url=ENCODED_URL&amp;auto_play=false&amp;hide_related=true&amp;show_comments=false&amp;show_user=true&amp;visual=false"></iframe>
            <script src="https://w.soundcloud.com/player/api.js"></script>
            <script>
            (() => {
              const widget = SC.Widget(document.getElementById('sc'));
              let sent = false;
              const send = value => { if (!sent) { sent = true; window.chrome.webview.postMessage(JSON.stringify(value)); } };
              setTimeout(() => send({error:'SoundCloud did not load this playlist. Make sure it is public and embeddable.'}), 25000);
              widget.bind(SC.Widget.Events.READY, () => widget.getSounds(sounds => {
                if (!Array.isArray(sounds) || sounds.length === 0) {
                  send({error:'SoundCloud returned no songs. This playlist may be private or embedding may be disabled.'});
                  return;
                }
                const tracks = sounds.map(s => ({title:s.title||'', permalink_url:s.permalink_url||'', duration:s.duration||0,
                  artwork_url:s.artwork_url||'', user:s.user||{}}));
                send({tracks});
              }));
            })();
            </script></body></html>
            """.Replace("ENCODED_URL", encodedUrl, StringComparison.Ordinal);
        _playlistImportWebView.NavigateToString(html);

        string message;
        try { message = await completion.Task.WaitAsync(TimeSpan.FromSeconds(35)); }
        catch (TimeoutException ex) { throw new InvalidOperationException("SoundCloud took too long to load the playlist. Try again, or check that it is public and embeddable.", ex); }
        finally { _playlistImportWebView.CoreWebView2.WebMessageReceived -= OnMessage; }

        using var response = JsonDocument.Parse(message);
        var root = response.RootElement;
        if (root.TryGetProperty("error", out var error))
            throw new InvalidOperationException(error.GetString() ?? "SoundCloud could not load this playlist.");

        var tracks = new List<TrackItem>();
        foreach (var item in root.GetProperty("tracks").EnumerateArray())
        {
            var title = item.TryGetProperty("title", out var titleValue) ? titleValue.GetString() ?? "" : "";
            var permalink = item.TryGetProperty("permalink_url", out var urlValue) ? urlValue.GetString() ?? "" : "";
            if (title.Length == 0 || permalink.Length == 0) continue;
            var user = item.TryGetProperty("user", out var userValue) ? userValue : default;
            var artist = user.ValueKind == JsonValueKind.Object && user.TryGetProperty("username", out var username) ? username.GetString() ?? "SoundCloud" : "SoundCloud";
            var artistUrl = user.ValueKind == JsonValueKind.Object && user.TryGetProperty("permalink_url", out var artistUrlValue) ? artistUrlValue.GetString() ?? "" : "";
            var artwork = item.TryGetProperty("artwork_url", out var artworkValue) ? artworkValue.GetString() ?? "" : "";
            var duration = item.TryGetProperty("duration", out var durationValue) && durationValue.TryGetDouble(out var ms) ? ms : 0;
            tracks.Add(new TrackItem
            {
                Title = title, Subtitle = artist, ArtistName = artist, ArtistUrl = artistUrl,
                Id = permalink, WebUrl = permalink, Source = "SOUNDCLOUD", IsOnline = true,
                DurationLabel = TimeSpan.FromMilliseconds(duration).ToString(@"m\:ss"),
                ArtworkUrl = artwork.Replace("-large", "-t500x500", StringComparison.OrdinalIgnoreCase)
            });
        }
        if (tracks.Count == 0) throw new InvalidOperationException("SoundCloud did not return any importable songs for this playlist.");

        var playlistName = "SoundCloud playlist";
        var playlistArtwork = tracks[0].ArtworkUrl;
        try
        {
            var oembedUrl = $"https://soundcloud.com/oembed?format=json&url={Uri.EscapeDataString(playlistUri.ToString())}";
            using var oembed = JsonDocument.Parse(await _soundCloudEmbedHttp.GetStringAsync(oembedUrl));
            if (oembed.RootElement.TryGetProperty("title", out var title)) playlistName = title.GetString() ?? playlistName;
            if (oembed.RootElement.TryGetProperty("thumbnail_url", out var thumbnail)) playlistArtwork = thumbnail.GetString() ?? playlistArtwork;
        }
        catch { /* The track list is still usable if SoundCloud's optional playlist details endpoint is unavailable. */ }
        return (playlistName, "", playlistArtwork.Replace("-large", "-t500x500", StringComparison.OrdinalIgnoreCase), tracks);
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
        var content = new StackPanel { Spacing = 12, MinWidth = 360 };
        content.Children.Add(coverButton);
        content.Children.Add(nameBox);
        content.Children.Add(descriptionBox);
        var dialog = new ContentDialog
        {
            Title = "Edit playlist",
            Content = content,
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
        playlist.UpdatedAt = DateTimeOffset.Now;
        SavePlaylists();
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
            if (_selectedPlaylist == playlist) PlaylistDetailCover.Source = playlist.Cover;
            playlist.UpdatedAt = DateTimeOffset.Now;
            SavePlaylists();
        }
        catch (Exception ex) { StatusText.Text = "Could not save the playlist cover: " + ex.Message; }
    }

    void LoadPlaylistCover(PlaylistItem playlist)
    {
        try
        {
            var source = File.Exists(playlist.CoverPath) ? playlist.CoverPath : playlist.CoverUrl;
            playlist.Cover = Uri.TryCreate(source, UriKind.Absolute, out var uri) ? new BitmapImage(uri) : null;
        }
        catch { playlist.Cover = null; }
    }

    void SavePlaylists() => SaveJson(Path.Combine(_dataDir, "playlists.json"), _playlists.ToList());

    // ================= SETTINGS =================

    void ThemeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Root is null || ThemeBox.SelectedIndex < 0) return;
        Root.RequestedTheme = ThemeBox.SelectedIndex == 1 ? ElementTheme.Light : ElementTheme.Dark;
        ApplyForegroundTheme(ThemeBox.SelectedIndex == 1);
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
            Theme = ThemeBox.SelectedIndex == 1 ? "Light" : "Dark",
            DownloadFolder = _downloadFolder,
            DiscordApplicationId = SharedDiscordApplicationId,
            DiscordActivityEnabled = _discordActivityEnabled,
            CrossfadeEnabled = _crossfadeEnabled,
            CrossfadeSeconds = _crossfadeSeconds,
            ShuffleEnabled = _shuffleEnabled,
            RepeatMode = _repeatMode,
            EqualizerGains = Enumerable.Range(0, _player.BandCount).Select(_player.GetBandGain).ToArray()
        });
        _discordApplicationId = SharedDiscordApplicationId;
        if (_discordActivityEnabled && _current is { } track && _player.IsPlaying) UpdateDiscordActivity(track);
        StatusText.Text = "Settings saved.";
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
        _downloadFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyMusic), "VYBE");

        try
        {
            var path = Path.Combine(_dataDir, "settings.json");
            if (File.Exists(path))
            {
                var s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path));
                if (s is not null)
                {
                    ThemeBox.SelectedIndex = s.Theme == "Light" ? 1 : 0;
                    if (s.DownloadFolder.Length > 0) _downloadFolder = s.DownloadFolder;
                    _savedEqualizerGains = s.EqualizerGains ?? Array.Empty<double>();
                    _discordApplicationId = SharedDiscordApplicationId;
                    _discordActivityEnabled = s.DiscordActivityEnabled;
                    _crossfadeEnabled = s.CrossfadeEnabled;
                    _crossfadeSeconds = Math.Clamp(s.CrossfadeSeconds, 0.5, 12);
                    _shuffleEnabled = s.ShuffleEnabled;
                    _repeatMode = Math.Clamp(s.RepeatMode, 0, 2);
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

        DownloadFolderBox.Text = _downloadFolder;
        CrossfadeToggle.IsOn = _crossfadeEnabled;
        CrossfadeDurationSlider.Value = _crossfadeSeconds;
        CrossfadeDurationSlider.IsEnabled = _crossfadeEnabled;
        CrossfadeDurationText.Text = $"{_crossfadeSeconds:0.0} seconds";
        VolumeSlider.Value = 75;
        ApplyForegroundTheme(ThemeBox.SelectedIndex == 1);
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
        public bool ShuffleEnabled { get; set; }
        public int RepeatMode { get; set; }
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
