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

    public async Task<List<TrackItem>> SearchAsync(string query, int limit = 25, CancellationToken ct = default)
    {
        var page = _client.SearchAsync(query, SearchCategory.Songs);
        var items = await page.FetchItemsAsync(0, limit, ct);

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
        return results;
    }

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

    public async Task<string> ResolveStreamUrlAsync(string videoId, bool force = false, CancellationToken ct = default)
    {
        if (!force && _urlCache.TryGetValue(videoId, out var cached))
            return cached;

        var data = await _client.GetStreamingDataAsync(videoId, ct);

        var best = data.StreamInfo
            .OfType<AudioStreamInfo>()
            .OrderByDescending(s => s.Bitrate)
            .FirstOrDefault()
            ?? throw new InvalidOperationException("YouTube returned no audio stream for this track.");

        _urlCache[videoId] = best.Url;
        return best.Url;
    }

    /// <summary>Downloads straight from the API stream - no yt-dlp, no ffmpeg.</summary>
    public async Task<string> DownloadAsync(string videoId, string folder, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        var data = await _client.GetStreamingDataAsync(videoId, ct);
        var best = data.StreamInfo
            .OfType<AudioStreamInfo>()
            .OrderByDescending(s => s.Bitrate)
            .FirstOrDefault()
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
