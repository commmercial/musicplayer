using System.Net.Http;
using System.Diagnostics;

namespace Risk.App.Services;

public static class MediaDownloader
{
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(10) };

    /// <summary>Downloads a direct stream URL to disk with progress reporting.</summary>
    public static async Task<string> DownloadUrlAsync(string url, string folder, string baseName, string extension,
        IProgress<double>? progress = null, CancellationToken ct = default)
    {
        Directory.CreateDirectory(folder);

        var safe = baseName;
        foreach (var c in Path.GetInvalidFileNameChars()) safe = safe.Replace(c, '_');
        var target = Path.Combine(folder, $"{safe}.{extension}");

        var attempt = 1;
        while (File.Exists(target))
            target = Path.Combine(folder, $"{safe} ({++attempt}).{extension}");

        using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        var length = response.Content.Headers.ContentLength ?? 0;
        await using var source = await response.Content.ReadAsStreamAsync(ct);
        await using var file = File.Create(target);

        var buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, ct)) > 0)
        {
            await file.WriteAsync(buffer.AsMemory(0, read), ct);
            total += read;
            if (length > 0) progress?.Report((double)total / length);
        }
        return target;
    }

    public static async Task<string> ConvertAudioAsync(string sourcePath, string folder, string baseName,
        string format, CancellationToken ct = default, int mp3Bitrate = 320)
    {
        format = format.ToLowerInvariant();
        if (format is not ("mp3" or "wav")) throw new ArgumentOutOfRangeException(nameof(format));
        var isRemoteStream = Uri.TryCreate(sourcePath, UriKind.Absolute, out var sourceUri) &&
            (sourceUri.Scheme == Uri.UriSchemeHttp || sourceUri.Scheme == Uri.UriSchemeHttps);
        if (!isRemoteStream && !File.Exists(sourcePath))
            throw new FileNotFoundException("The downloaded audio file was not found.", sourcePath);

        var ffmpeg = FindFfmpeg();
        if (ffmpeg is null)
            throw new FileNotFoundException("FFmpeg is required to convert audio. Install it with `winget install Gyan.FFmpeg`, then reopen Risk.");

        Directory.CreateDirectory(folder);
        var target = UniqueTarget(folder, baseName, format);
        var start = new ProcessStartInfo
        {
            FileName = ffmpeg,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true
        };
        foreach (var argument in new[] { "-hide_banner", "-loglevel", "error", "-y", "-i", sourcePath, "-vn" })
            start.ArgumentList.Add(argument);
        if (format == "mp3")
        {
            start.ArgumentList.Add("-codec:a");
            start.ArgumentList.Add("libmp3lame");
            start.ArgumentList.Add("-b:a");
            start.ArgumentList.Add(Math.Clamp(mp3Bitrate, 128, 320) + "k");
        }
        else
        {
            start.ArgumentList.Add("-codec:a");
            start.ArgumentList.Add("pcm_s16le");
        }
        start.ArgumentList.Add(target);

        using var process = new Process { StartInfo = start };
        try
        {
            if (!process.Start()) throw new IOException("FFmpeg could not be started.");
        }
        catch (Exception ex) when (ex is not FileNotFoundException)
        {
            throw new IOException("FFmpeg could not be started. Install it with `winget install Gyan.FFmpeg`.", ex);
        }

        using var registration = ct.Register(() =>
        {
            try { if (!process.HasExited) process.Kill(true); } catch { }
        });
        var errorTask = process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);
        var error = await errorTask;
        if (process.ExitCode != 0)
        {
            TryDelete(target);
            throw new IOException(string.IsNullOrWhiteSpace(error)
                ? "FFmpeg could not convert this audio stream."
                : error.Trim());
        }
        return target;
    }

    static string? FindFfmpeg()
    {
        var bundled = Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe");
        if (File.Exists(bundled)) return bundled;
        var paths = (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return paths.Select(path => Path.Combine(path, OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg"))
            .FirstOrDefault(File.Exists);
    }

    public static string UniqueTarget(string folder, string baseName, string extension)
    {
        Directory.CreateDirectory(folder);
        var safe = baseName;
        foreach (var c in Path.GetInvalidFileNameChars()) safe = safe.Replace(c, '_');
        safe = safe.Trim().TrimEnd('.');
        if (string.IsNullOrWhiteSpace(safe)) safe = "audio";
        var target = Path.Combine(folder, $"{safe}.{extension}");
        var attempt = 1;
        while (File.Exists(target)) target = Path.Combine(folder, $"{safe} ({++attempt}).{extension}");
        return target;
    }

    static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}

