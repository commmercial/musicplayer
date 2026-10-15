using YouTubeMusicAPI.Client;
using YouTubeMusicAPI.Models;
using YouTubeMusicAPI.Models.Info;
using YouTubeMusicAPI.Models.Search;
using YouTubeMusicAPI.Models.Streaming;

namespace Vybe.App.Services;

public sealed class YouTubeMusicService
{
    readonly YouTubeMusicClient _client = new();
    readonly Dictionary<string, string> _urlCache = new();
    readonly Dictionary<string, double> _loudnessCache = new();
    readonly Dictionary<string, (DateTimeOffset CreatedAt, List<TrackItem> Tracks)> _searchCache = new(StringComparer.OrdinalIgnoreCase);
    readonly object _searchCacheGate = new();

    public async Task<List<TrackItem>> SearchAsync(string query, int limit = 25, CancellationToken ct = default)
    {
        var cacheKey = $"{query.Trim()}|{limit}";
        lock (_searchCacheGate)
        {
            if (_searchCache.TryGetValue(cacheKey, out var cached) && DateTimeOffset.UtcNow - cached.CreatedAt < TimeSpan.FromSeconds(45))
                return cached.Tracks.Select(CloneTrack).ToList();
        }

        var page = _client.SearchAsync(query, SearchCategory.Songs);
        var items = await page.FetchItemsAsync(0, limit, ct).ConfigureAwait(false);

        var results = new List<TrackItem>();
        foreach (var item in items)
        {
            if (item is not SongSearchResult song) continue;

            results.Add(new TrackItem
            {
                Title = song.Name,
                Subtitle = string.Join(", ", song.Artists.Select(a => a.Name)),
                ArtistName = song.Artists.FirstOrDefault()?.Name ?? "",
                ArtistId = song.Artists.FirstOrDefault()?.Id ?? "",
                Id = song.Id,
                WebUrl = "https://music.youtube.com/watch?v=" + song.Id,
                DurationLabel = Format(song.Duration),
                Source = "YT MUSIC",
                IsOnline = true,
                ArtworkUrl = Largest(song.Thumbnails)
            });
        }
        lock (_searchCacheGate)
        {
            if (_searchCache.Count >= 100)
            {
                foreach (var expired in _searchCache.Where(entry => DateTimeOffset.UtcNow - entry.Value.CreatedAt >= TimeSpan.FromSeconds(45))
                             .Select(entry => entry.Key).ToArray())
                    _searchCache.Remove(expired);
                if (_searchCache.Count >= 100) _searchCache.Clear();
            }
            _searchCache[cacheKey] = (DateTimeOffset.UtcNow, results.Select(CloneTrack).ToList());
        }
        return results;
    }

    static TrackItem CloneTrack(TrackItem track) => new()
    {
        Title = track.Title,
        Subtitle = track.Subtitle,
        Path = track.Path,
        Source = track.Source,
        DurationLabel = track.DurationLabel,
        WebUrl = track.WebUrl,
        Id = track.Id,
        ArtistName = track.ArtistName,
        ArtistId = track.ArtistId,
        ArtistUrl = track.ArtistUrl,
        IsOnline = track.IsOnline,
        ArtworkUrl = track.ArtworkUrl,
        Artwork = track.Artwork,
        ResolvedAt = track.ResolvedAt
    };

    public async Task<(string Name, List<TrackItem> Tracks)> GetArtistTracksAsync(
        string artistId, CancellationToken ct = default)
    {
        var artist = await _client.GetArtistInfoAsync(artistId, ct);
        var tracks = artist.Songs.Select(song => new TrackItem
        {
            Title = song.Name,
            Subtitle = string.Join(", ", song.Artists.Select(a => a.Name)),
            ArtistName = artist.Name,
            ArtistId = artistId,
            Id = song.Id,
            WebUrl = "https://music.youtube.com/watch?v=" + song.Id,
            Source = "YT MUSIC",
            IsOnline = true,
            ArtworkUrl = Largest(song.Thumbnails)
        }).ToList();
        return (artist.Name, tracks);
    }

    public async Task<(string Name, string Description, string ArtworkUrl, List<TrackItem> Tracks)> GetPlaylistAsync(
        string playlistUrl, CancellationToken ct = default)
    {
        var match = System.Text.RegularExpressions.Regex.Match(playlistUrl, @"[?&]list=([A-Za-z0-9_-]+)");
        var id = match.Success ? match.Groups[1].Value : playlistUrl.Trim().TrimEnd('/').Split('/').Last();
        if (id.Length == 0) throw new InvalidOperationException("Could not find a YouTube Music playlist ID in that link.");

        var browseId = _client.GetCommunityPlaylistBrowseId(id);
        var info = await _client.GetCommunityPlaylistInfoAsync(browseId, ct);
        var page = _client.GetCommunityPlaylistSongsAsync(browseId);
        var songs = await page.FetchItemsAsync(0, Math.Clamp(info.SongCount, 1, 500), ct);
        var tracks = songs.Select(song => new TrackItem
        {
            Title = song.Name,
            Subtitle = string.Join(", ", song.Artists.Select(a => a.Name)),
            ArtistName = song.Artists.FirstOrDefault()?.Name ?? "",
            Id = song.Id,
            WebUrl = "https://music.youtube.com/watch?v=" + song.Id,
            DurationLabel = Format(song.Duration),
            Source = "YT MUSIC",
            IsOnline = true,
            ArtworkUrl = Largest(song.Thumbnails)
        }).ToList();

        return (info.Name, info.Description ?? "", Largest(info.Thumbnails), tracks);
    }

    public double? GetLoudnessDb(string videoId) =>
        _loudnessCache.TryGetValue(videoId, out var loudness) && double.IsFinite(loudness) ? loudness : null;

    public async Task<string> ResolveStreamUrlAsync(string videoId, bool force = false, CancellationToken ct = default,
        string quality = "Auto")
    {
        var cacheKey = videoId + "|" + quality;
        if (!force && _urlCache.TryGetValue(cacheKey, out var cached))
            return cached;

        var data = await _client.GetStreamingDataAsync(videoId, ct);
        var best = SelectAudioStream(data.StreamInfo.OfType<AudioStreamInfo>(), quality)
            ?? throw new InvalidOperationException("YouTube returned no audio stream for this track.");

        _urlCache[cacheKey] = best.Url;
        if (double.IsFinite(best.LoudnessDb)) _loudnessCache[videoId] = best.LoudnessDb;
        return best.Url;
    }

    static AudioStreamInfo? SelectAudioStream(IEnumerable<AudioStreamInfo> streams, string quality)
    {
        var available = streams.OrderBy(stream => stream.Bitrate).ToArray();
        if (available.Length == 0) return null;
        return quality switch
        {
            "Low" => available[0],
            "Normal" => available.OrderBy(stream => Math.Abs(stream.Bitrate - 96000)).First(),
            "High" => available.OrderBy(stream => Math.Abs(stream.Bitrate - 160000)).First(),
            _ => available[available.Length - 1]
        };
    }

    /// <summary>Downloads straight from the API stream - no yt-dlp, no ffmpeg.</summary>
    public async Task<string> DownloadAsync(string videoId, string folder, IProgress<double>? progress = null,
        CancellationToken ct = default, string quality = "Auto")
    {
        var data = await _client.GetStreamingDataAsync(videoId, ct);
        var best = SelectAudioStream(data.StreamInfo.OfType<AudioStreamInfo>(), quality)
            ?? throw new InvalidOperationException("No audio stream available.");

        var safe = Sanitize(videoId);
        var ext = string.IsNullOrWhiteSpace(best.Container.Format) ? "m4a" : best.Container.Format;
        var target = Path.Combine(folder, $"{safe}.{ext}");

        await using var source = await best.GetStreamAsync(ct);
        await using var file = File.Create(target);

        var buffer = new byte[81920];
        long total = 0, length = best.ContentLenght;
        int read;
        while ((read = await source.ReadAsync(buffer, ct)) > 0)
        {
            await file.WriteAsync(buffer.AsMemory(0, read), ct);
            total += read;
            if (length > 0) progress?.Report((double)total / length);
        }
        return target;
    }

    static string Largest(Thumbnail[] thumbs) =>
        thumbs.OrderByDescending(t => t.Width).FirstOrDefault()?.Url ?? "";

    static string Format(TimeSpan d) =>
        d.TotalHours >= 1 ? d.ToString(@"h\:mm\:ss") : d.ToString(@"m\:ss");

    static string Sanitize(string name)
    {
        foreach (var c in System.IO.Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        return name;
    }
}

