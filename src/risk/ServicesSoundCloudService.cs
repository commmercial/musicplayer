using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Vybe.App.Services;

/// <summary>
/// SoundCloud without the official API, without an OAuth token and without the web widget.
/// Scrapes a public client_id from SoundCloud's own JS bundle, then uses the internal
/// api-v2 endpoints to search and to resolve a direct progressive MP3 stream URL.
/// </summary>
public sealed class SoundCloudService
{
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

    string? _clientId;
    readonly SemaphoreSlim _gate = new(1, 1);

    async Task<string> ClientIdAsync(CancellationToken ct)
    {
        if (_clientId is not null) return _clientId;

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_clientId is not null) return _clientId;

            var html = await Http.GetStringAsync("https://soundcloud.com/", ct).ConfigureAwait(false);
            var scripts = Regex.Matches(html, @"src=""(https://a-v2\.sndcdn\.com/assets/[^""]+\.js)""");

            foreach (Match script in scripts)
            {
                var js = await Http.GetStringAsync(script.Groups[1].Value, ct).ConfigureAwait(false);
                var match = Regex.Match(js, @"client_id[:=]\s*""?([a-zA-Z0-9]{32})");
                if (match.Success)
                {
                    _clientId = match.Groups[1].Value;
                    return _clientId;
                }
            }
            throw new InvalidOperationException("Could not obtain a SoundCloud client id.");
        }
        finally { _gate.Release(); }
    }

    public async Task<List<TrackItem>> SearchAsync(string query, int limit = 15, CancellationToken ct = default)
    {
        var clientId = await ClientIdAsync(ct).ConfigureAwait(false);
        var url = $"https://api-v2.soundcloud.com/search/tracks?q={Uri.EscapeDataString(query)}&client_id={clientId}&limit={limit}&offset=0";

        using var doc = JsonDocument.Parse(await Http.GetStringAsync(url, ct).ConfigureAwait(false));
        if (!doc.RootElement.TryGetProperty("collection", out var collection))
            return new List<TrackItem>();

        var results = new List<TrackItem>();
        foreach (var item in collection.EnumerateArray())
        {
            var permalink = Str(item, "permalink_url");
            var title = Str(item, "title");
            if (permalink.Length == 0 || title.Length == 0) continue;
            if (item.TryGetProperty("streamable", out var s) && !s.GetBoolean()) continue;

            results.Add(new TrackItem
            {
                Title = title,
                Subtitle = item.TryGetProperty("user", out var u) ? Str(u, "username") : "SoundCloud",
                ArtistName = item.TryGetProperty("user", out var user) ? Str(user, "username") : "",
                ArtistUrl = item.TryGetProperty("user", out var artistUser) ? Str(artistUser, "permalink_url") : "",
                Id = permalink,
                WebUrl = permalink,
                Source = "SOUNDCLOUD",
                IsOnline = true,
                DurationLabel = Format(TimeSpan.FromMilliseconds(item.TryGetProperty("duration", out var d) ? d.GetDouble() : 0)),
                ArtworkUrl = Str(item, "artwork_url").Replace("-large", "-t500x500")
            });
        }
        return results;
    }

    public async Task<(string Name, List<TrackItem> Tracks)> GetArtistTracksAsync(
        string artistUrl, CancellationToken ct = default)
    {
        var clientId = await ClientIdAsync(ct);
        var resolveUrl = $"https://api-v2.soundcloud.com/resolve?url={Uri.EscapeDataString(artistUrl)}&client_id={clientId}";
        using var artistDoc = JsonDocument.Parse(await Http.GetStringAsync(resolveUrl, ct));
        var artist = artistDoc.RootElement;
        var artistId = artist.TryGetProperty("id", out var id) ? id.ToString() : "";
        var artistName = Str(artist, "username");
        if (artistId.Length == 0) throw new InvalidOperationException("SoundCloud did not return this artist profile.");

        var tracksUrl = $"https://api-v2.soundcloud.com/users/{artistId}/tracks?client_id={clientId}&limit=100&offset=0";
        using var tracksDoc = JsonDocument.Parse(await Http.GetStringAsync(tracksUrl, ct));
        var collection = tracksDoc.RootElement.TryGetProperty("collection", out var items)
            ? items : tracksDoc.RootElement;
        var tracks = new List<TrackItem>();

        if (collection.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in collection.EnumerateArray())
            {
                var permalink = Str(item, "permalink_url");
                var title = Str(item, "title");
                if (permalink.Length == 0 || title.Length == 0) continue;
                tracks.Add(new TrackItem
                {
                    Title = title,
                    Subtitle = artistName,
                    ArtistName = artistName,
                    ArtistUrl = artistUrl,
                    Id = permalink,
                    WebUrl = permalink,
                    Source = "SOUNDCLOUD",
                    IsOnline = true,
                    DurationLabel = Format(TimeSpan.FromMilliseconds(item.TryGetProperty("duration", out var duration) ? duration.GetDouble() : 0)),
                    ArtworkUrl = Str(item, "artwork_url").Replace("-large", "-t500x500")
                });
            }
        }
        return (artistName, tracks);
    }

    public async Task<(string Name, string Description, string ArtworkUrl, List<TrackItem> Tracks)> GetPlaylistAsync(
        string playlistUrl, CancellationToken ct = default)
    {
        var clientId = await ClientIdAsync(ct);
        var resolveUrl = $"https://api-v2.soundcloud.com/resolve?url={Uri.EscapeDataString(playlistUrl)}&client_id={clientId}";
        using var doc = JsonDocument.Parse(await Http.GetStringAsync(resolveUrl, ct));
        var root = doc.RootElement;
        if (Str(root, "kind") is not ("playlist" or "system-playlist"))
            throw new InvalidOperationException("That SoundCloud link is not a playlist.");

        var tracks = new List<TrackItem>();
        if (root.TryGetProperty("tracks", out var collection) && collection.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in collection.EnumerateArray())
            {
                var permalink = Str(item, "permalink_url");
                var title = Str(item, "title");
                if (permalink.Length == 0 || title.Length == 0) continue;
                var artist = item.TryGetProperty("user", out var user) ? Str(user, "username") : "SoundCloud";
                tracks.Add(new TrackItem
                {
                    Title = title, Subtitle = artist, ArtistName = artist,
                    ArtistUrl = item.TryGetProperty("user", out user) ? Str(user, "permalink_url") : "",
                    Id = permalink, WebUrl = permalink, Source = "SOUNDCLOUD", IsOnline = true,
                    DurationLabel = Format(TimeSpan.FromMilliseconds(item.TryGetProperty("duration", out var d) ? d.GetDouble() : 0)),
                    ArtworkUrl = Str(item, "artwork_url").Replace("-large", "-t500x500")
                });
            }
        }

        return (Str(root, "title"), Str(root, "description"),
            Str(root, "artwork_url").Replace("-large", "-t500x500"), tracks);
    }

    /// <summary>Resolves a SoundCloud permalink into a direct progressive MP3 URL.</summary>
    public async Task<string> ResolveStreamUrlAsync(string permalink, CancellationToken ct = default)
    {
        var clientId = await ClientIdAsync(ct);

        var resolveUrl = $"https://api-v2.soundcloud.com/resolve?url={Uri.EscapeDataString(permalink)}&client_id={clientId}";
        using var doc = JsonDocument.Parse(await Http.GetStringAsync(resolveUrl, ct));

        var track = doc.RootElement;
        if (Str(track, "kind") == "playlist" && track.TryGetProperty("tracks", out var tracks) && tracks.GetArrayLength() > 0)
            track = tracks[0];

        if (!track.TryGetProperty("media", out var media) ||
            !media.TryGetProperty("transcodings", out var transcodings) ||
            transcodings.GetArrayLength() == 0)
            throw new InvalidOperationException("This SoundCloud track has no streamable media.");

        string? chosen = null;
        foreach (var t in transcodings.EnumerateArray())
        {
            var protocol = t.TryGetProperty("format", out var f) ? Str(f, "protocol") : "";
            var candidate = Str(t, "url");
            if (protocol == "progressive") { chosen = candidate; break; }
            chosen ??= candidate;
        }
        if (chosen is null) throw new InvalidOperationException("No usable transcoding found.");

        var streamUrl = chosen + (chosen.Contains('?') ? "&" : "?") + "client_id=" + clientId;
        using var streamDoc = JsonDocument.Parse(await Http.GetStringAsync(streamUrl, ct));
        return Str(streamDoc.RootElement, "url");
    }

    static string Str(JsonElement el, string prop) =>
        el.ValueKind == JsonValueKind.Object && el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? "" : "";

    static string Format(TimeSpan d) =>
        d.TotalHours >= 1 ? d.ToString(@"h\:mm\:ss") : d.ToString(@"m\:ss");
}

