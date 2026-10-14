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

        await _gate.WaitAsync(ct);
        try
        {
            if (_clientId is not null) return _clientId;

            var html = await Http.GetStringAsync("https://soundcloud.com/", ct);
            var scripts = Regex.Matches(html, @"src=""(https://a-v2\.sndcdn\.com/assets/[^""]+\.js)""");

            foreach (Match script in scripts)
            {
                var js = await Http.GetStringAsync(script.Groups[1].Value, ct);
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
        var clientId = await ClientIdAsync(ct);
        var url = $"https://api-v2.soundcloud.com/search/tracks?q={Uri.EscapeDataString(query)}&client_id={clientId}&limit={limit}&offset=0";

        using var doc = JsonDocument.Parse(await Http.GetStringAsync(url, ct));
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
        var artistId = artist.TryGetProperty("id", out var artistIdValue) ? artistIdValue.ToString() : "";
        var artistName = Str(artist, "username");
        if (artistId.Length == 0) throw new InvalidOperationException("SoundCloud did not return this artist profile.");

        var tracksUrl = $"https://api-v2.soundcloud.com/users/{artistId}/tracks?client_id={clientId}&limit=100&offset=0";
        using var tracksDoc = JsonDocument.Parse(await Http.GetStringAsync(tracksUrl, ct));
        var collection = tracksDoc.RootElement.TryGetProperty("collection", out var artistItems)
            ? artistItems : tracksDoc.RootElement;
        var artistTracks = new List<TrackItem>();
        if (collection.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in collection.EnumerateArray())
            {
                var permalink = Str(item, "permalink_url");
                var title = Str(item, "title");
                if (permalink.Length == 0 || title.Length == 0) continue;
                artistTracks.Add(new TrackItem
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
        return (artistName, artistTracks);
    }

    public async Task<(string Name, string Description, string ArtworkUrl, List<TrackItem> Tracks)> GetPlaylistAsync(
        string playlistUrl, CancellationToken ct = default)
    {
        var clientId = await ClientIdAsync(ct);
        var resolveUrl = $"https://api-v2.soundcloud.com/resolve?url={Uri.EscapeDataString(playlistUrl)}&client_id={clientId}";
        using var doc = JsonDocument.Parse(await GetPlaylistResolveResponseAsync(resolveUrl, ct));
        var root = doc.RootElement;
        if (Str(root, "kind") is not ("playlist" or "system-playlist"))
            throw new InvalidOperationException("That SoundCloud link is not a playlist.");

        var playlistId = root.TryGetProperty("id", out var playlistIdValue) ? playlistIdValue.ToString() : "";
        var playlistUrn = Str(root, "urn");
        var preferredUri = Str(root, "tracks_uri");
        var endpoints = new List<string>();
        if (preferredUri.Length > 0) endpoints.Add(preferredUri);
        if (playlistId.Length > 0)
            endpoints.Add($"https://api-v2.soundcloud.com/playlists/{Uri.EscapeDataString(playlistId)}/tracks");
        var canonicalId = playlistUrn.Length > 0 ? playlistUrn : playlistId;
        if (canonicalId.Length > 0)
            endpoints.Add($"https://api.soundcloud.com/playlists/{Uri.EscapeDataString(canonicalId)}/tracks");
        endpoints = endpoints.Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        var tracks = new List<TrackItem>();
        var expectedCount = root.TryGetProperty("track_count", out var countElement) &&
                            countElement.TryGetInt32(out var trackCount) ? Math.Max(0, trackCount) : 0;
        string? pageUrl = null;
        string? selectedEndpoint = null;
        string? pageJson = null;
        HttpRequestException? notFound = null;
        HttpRequestException? forbidden = null;
        foreach (var endpoint in endpoints)
        {
            var candidate = SetQuery(endpoint, ("client_id", clientId), ("limit", "200"), ("linked_partitioning", "true"));
            try
            {
                pageJson = await Http.GetStringAsync(candidate, ct);
                pageUrl = candidate;
                selectedEndpoint = endpoint;
                break;
            }
            catch (HttpRequestException ex) when (ex.StatusCode is System.Net.HttpStatusCode.NotFound or System.Net.HttpStatusCode.Forbidden)
            {
                if (ex.StatusCode == System.Net.HttpStatusCode.Forbidden) forbidden = ex;
                else notFound = ex;
            }
        }
        if (pageUrl is null && forbidden is not null)
            throw new InvalidOperationException("SoundCloud denied access to this playlist. Check that it is public and shareable; some playlists cannot be imported through the public API.", forbidden);
        if (pageUrl is null)
            throw new InvalidOperationException("SoundCloud could not find a track endpoint for this playlist. It may be unavailable or unsupported.", notFound);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var offset = 0;
        while (pageUrl is not null && tracks.Count < (expectedCount > 0 ? expectedCount : 20000) && tracks.Count < 20000)
        {
            using var page = JsonDocument.Parse(pageJson ?? await Http.GetStringAsync(pageUrl, ct));
            pageJson = null;
            var pageRoot = page.RootElement;
            var pageCollection = pageRoot.ValueKind == JsonValueKind.Array
                ? pageRoot
                : pageRoot.TryGetProperty("collection", out var pageItems) ? pageItems : default;
            if (pageCollection.ValueKind != JsonValueKind.Array || pageCollection.GetArrayLength() == 0) break;

            var pageSize = pageCollection.GetArrayLength();
            foreach (var item in pageCollection.EnumerateArray())
            {
                var track = ParsePlaylistTrack(item);
                if (track is not null && seen.Add(track.Id)) tracks.Add(track);
            }

            var nextHref = Str(pageRoot, "next_href");
            if (nextHref.Length > 0)
            {
                pageUrl = nextHref.Contains("client_id=", StringComparison.OrdinalIgnoreCase)
                    ? nextHref
                    : SetQuery(nextHref, ("client_id", clientId));
                continue;
            }

            offset += pageSize;
            if (expectedCount == 0 || tracks.Count >= expectedCount || pageSize == 0)
            {
                pageUrl = null;
                break;
            }
            pageUrl = SetQuery(selectedEndpoint!, ("client_id", clientId), ("limit", "200"),
                ("linked_partitioning", "true"), ("offset", offset.ToString()));
        }

        return (Str(root, "title"), Str(root, "description"),
            Str(root, "artwork_url").Replace("-large", "-t500x500"), tracks);
    }
    static async Task<string> GetPlaylistResolveResponseAsync(string url, CancellationToken ct)
    {
        try { return await Http.GetStringAsync(url, ct); }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.Forbidden)
        {
            throw new InvalidOperationException("SoundCloud denied access to this playlist link. Make sure it is public and shareable; private or restricted playlists need a shareable link.", ex);
        }
    }
    static TrackItem? ParsePlaylistTrack(JsonElement item)
    {
        var permalink = Str(item, "permalink_url");
        var title = Str(item, "title");
        if (permalink.Length == 0 || title.Length == 0) return null;
        var hasUser = item.TryGetProperty("user", out var user);
        var artist = hasUser ? Str(user, "username") : "SoundCloud";
        var durationMs = item.TryGetProperty("duration", out var duration) && duration.TryGetDouble(out var ms) ? ms : 0;
        return new TrackItem
        {
            Title = title, Subtitle = artist, ArtistName = artist,
            ArtistUrl = hasUser ? Str(user, "permalink_url") : "",
            Id = permalink, WebUrl = permalink, Source = "SOUNDCLOUD", IsOnline = true,
            DurationLabel = Format(TimeSpan.FromMilliseconds(durationMs)),
            ArtworkUrl = Str(item, "artwork_url").Replace("-large", "-t500x500")
        };
    }

    static string SetQuery(string url, params (string Key, string Value)[] values)
    {
        var builder = new UriBuilder(url);
        var query = builder.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Where(part => !values.Any(value => part.StartsWith(Uri.EscapeDataString(value.Key) + "=", StringComparison.OrdinalIgnoreCase)))
            .ToList();
        query.AddRange(values.Select(value => $"{Uri.EscapeDataString(value.Key)}={Uri.EscapeDataString(value.Value)}"));
        builder.Query = string.Join("&", query);
        return builder.Uri.ToString();
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
