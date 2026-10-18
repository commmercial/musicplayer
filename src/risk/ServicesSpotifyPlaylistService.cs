using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Risk.App.Services;

public sealed class SpotifyPlaylistService
{
    static readonly HttpClient Http = CreateClient();

    static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(25) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("RiskMusicPlayer/1.0");
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en-US,en;q=0.9");
        return client;
    }

    public async Task<(string Name, string Description, string ArtworkUrl, List<TrackItem> Tracks)> GetPlaylistAsync(
        Uri playlistUrl, CancellationToken ct = default)
    {
        var segments = playlistUrl.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var playlistIndex = Array.FindIndex(segments, segment => string.Equals(segment, "playlist", StringComparison.OrdinalIgnoreCase));
        if (playlistIndex < 0 || playlistIndex + 1 >= segments.Length)
            throw new InvalidOperationException("Paste a Spotify playlist link, such as https://open.spotify.com/playlist/…");

        var playlistId = segments[playlistIndex + 1];
        if (!Regex.IsMatch(playlistId, "^[A-Za-z0-9]{10,64}$", RegexOptions.CultureInvariant))
            throw new InvalidOperationException("That Spotify playlist link does not contain a valid playlist ID.");

        var embedUrl = new Uri($"https://open.spotify.com/embed/playlist/{Uri.EscapeDataString(playlistId)}");
        using var response = await Http.GetAsync(embedUrl, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Forbidden)
            throw new InvalidOperationException("Spotify could not open that playlist. It may be private, unavailable, or removed.");
        response.EnsureSuccessStatusCode();

        var html = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        var stateMatch = Regex.Match(html,
            "<script\\s+id=\"__NEXT_DATA__\"\\s+type=\"application/json\">(.*?)</script>",
            RegexOptions.Singleline | RegexOptions.CultureInvariant);
        if (!stateMatch.Success)
            throw new InvalidOperationException("Spotify did not return playlist details. Check that the playlist is public and try again.");

        using var document = JsonDocument.Parse(stateMatch.Groups[1].Value);
        var entity = document.RootElement
            .GetProperty("props").GetProperty("pageProps").GetProperty("state")
            .GetProperty("data").GetProperty("entity");

        var name = ReadString(entity, "name") ?? "Spotify playlist";
        var artwork = "";
        if (entity.TryGetProperty("coverArt", out var coverArt) &&
            coverArt.TryGetProperty("sources", out var sources) &&
            sources.ValueKind == JsonValueKind.Array && sources.GetArrayLength() > 0)
            artwork = ReadString(sources[0], "url") ?? "";

        if (!entity.TryGetProperty("trackList", out var trackList) || trackList.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("Spotify did not provide tracks for that playlist.");

        var tracks = new List<TrackItem>();
        foreach (var item in trackList.EnumerateArray())
        {
            var title = ReadString(item, "title");
            if (string.IsNullOrWhiteSpace(title)) continue;
            var artist = ReadString(item, "subtitle") ?? "";
            var spotifyUri = ReadString(item, "uri") ?? "";
            var duration = item.TryGetProperty("duration", out var durationValue) && durationValue.TryGetInt64(out var milliseconds)
                ? TimeSpan.FromMilliseconds(milliseconds)
                : TimeSpan.Zero;

            tracks.Add(new TrackItem
            {
                Title = title,
                ArtistName = artist,
                Subtitle = artist,
                ArtworkUrl = ExtractTrackArtwork(item),
                Source = "SPOTIFY",
                Id = spotifyUri,
                WebUrl = spotifyUri.StartsWith("spotify:track:", StringComparison.Ordinal)
                    ? "https://open.spotify.com/track/" + spotifyUri["spotify:track:".Length..]
                    : playlistUrl.ToString(),
                DurationLabel = duration > TimeSpan.Zero ? $"{(int)duration.TotalMinutes}:{duration.Seconds:00}" : ""
            });
        }

        if (tracks.Count == 0)
            throw new InvalidOperationException("No playable tracks were found. The playlist may be empty or unavailable.");

        return (name, "Imported from Spotify", artwork, tracks);
    }

    static string? ReadString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    static string ExtractTrackArtwork(JsonElement track)
    {
        foreach (var property in new[] { "imageUrl", "image_url", "artworkUrl", "artwork_url" })
        {
            var direct = ReadString(track, property);
            if (!string.IsNullOrWhiteSpace(direct)) return direct;
        }

        if (TryGetProperty(track, "coverArt", out var cover) && ReadSources(cover) is { Length: > 0 } coverUrl)
            return coverUrl;
        if (TryGetProperty(track, "albumOfTrack", out var albumOfTrack))
        {
            if (TryGetProperty(albumOfTrack, "coverArt", out cover) && ReadSources(cover) is { Length: > 0 } albumUrl)
                return albumUrl;
        }
        if (TryGetProperty(track, "album", out var album))
        {
            foreach (var property in new[] { "coverArt", "images" })
                if (TryGetProperty(album, property, out var images) && ReadSources(images) is { Length: > 0 } albumUrl)
                    return albumUrl;
        }
        return "";
    }

    static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out value)) return true;
        value = default;
        return false;
    }

    static string ReadSources(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object && value.TryGetProperty("sources", out var sources))
            value = sources;
        if (value.ValueKind == JsonValueKind.Array)
            return value.EnumerateArray()
                .Select(source => new
                {
                    Url = ReadString(source, "url") ?? "",
                    Width = source.TryGetProperty("width", out var width) && width.TryGetInt32(out var number) ? number : 0
                })
                .Where(source => source.Url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(source => source.Width)
                .Select(source => source.Url)
                .FirstOrDefault() ?? "";
        return "";
    }
}
