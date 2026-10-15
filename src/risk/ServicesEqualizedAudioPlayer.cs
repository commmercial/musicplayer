using Windows.Media.Audio;
using Windows.Media.Core;
using Windows.Media.Render;

namespace Vybe.App.Services;

public sealed class EqualizedAudioPlayer : IDisposable
{
    AudioGraph? _graph;
    Task? _initializationTask;
    AudioDeviceOutputNode? _output;
    MediaSourceAudioInputNode? _input;
    MediaSourceAudioInputNode? _fadingInput;
    EqualizerEffectDefinition? _equalizer;
    CancellationTokenSource? _fadeCancellation;
    TimeSpan _pausedPosition;
    double _volume = 0.56;
    double _currentGain = 1;
    double _fadingGain = 1;
    double _currentNormalizationGain = 1;
    double _fadingNormalizationGain = 1;
    bool _playing;
    int _playGeneration;

    public event Action? MediaEnded;
    public event Action<string>? PlaybackFailed;

    public bool IsPlaying => _playing;
    public TimeSpan Position => _input?.Position ?? _pausedPosition;
    public TimeSpan Duration => _input?.Duration ?? TimeSpan.Zero;
    public int BandCount => _equalizer?.Bands.Count ?? 0;
    public double GetBandGain(int index)
    {
        if (_equalizer is null || index < 0 || index >= _equalizer.Bands.Count) return 0;
        var amplitude = Math.Clamp(_equalizer.Bands[index].Gain, 0.126, 7.94);
        return Math.Clamp(20 * Math.Log10(amplitude), -12, 12);
    }
    public double GetBandFrequency(int index) => _equalizer!.Bands[index].FrequencyCenter;

    public double Volume
    {
        get => _volume;
        set
        {
            _volume = Math.Clamp(value, 0, 1);
            if (_input is not null) _input.OutgoingGain = _volume * _currentGain * _currentNormalizationGain;
            if (_fadingInput is not null) _fadingInput.OutgoingGain = _volume * _fadingGain * _fadingNormalizationGain;
        }
    }

    public Task InitializeAsync() => _initializationTask ??= InitializeCoreAsync();

    async Task InitializeCoreAsync()
    {
        var settings = new AudioGraphSettings(AudioRenderCategory.Media);
        var graphResult = await AudioGraph.CreateAsync(settings);
        if (graphResult.Status.ToString() != "Success")
            throw new InvalidOperationException($"Windows could not create an audio graph ({graphResult.Status}).");

        _graph = graphResult.Graph;
        var outputResult = await _graph.CreateDeviceOutputNodeAsync();
        if (outputResult.Status.ToString() != "Success")
            throw new InvalidOperationException($"Windows could not open the audio output ({outputResult.Status}).");

        _output = outputResult.DeviceOutputNode;
        _equalizer = new EqualizerEffectDefinition(_graph);
    }

    public async Task PlayAsync(string sourceUri, bool crossfade = false, TimeSpan? fadeDuration = null,
        double normalizationGain = 1)
    {
        var generation = Interlocked.Increment(ref _playGeneration);
        normalizationGain = Math.Clamp(normalizationGain, 0.4, 1.75);
        CancelFade();
        await InitializeAsync();
        if (generation != Volatile.Read(ref _playGeneration)) return;

        var previous = _input;
        DisposeNode(_fadingInput);
        _fadingInput = null;
        if (!crossfade || previous is null || !_playing)
        {
            DisposeNode(previous);
            _input = null;
            previous = null;
            _currentGain = 1;
            _currentNormalizationGain = normalizationGain;
            _fadingNormalizationGain = 1;
            _pausedPosition = TimeSpan.Zero;
            _playing = false;
        }

        try
        {
            var source = MediaSource.CreateFromUri(new Uri(sourceUri));
            var result = await _graph!.CreateMediaSourceAudioInputNodeAsync(source);
            if (result.Status.ToString() != "Success")
                throw new InvalidOperationException($"Windows could not decode this track ({result.Status}).");

            var next = result.Node;
            if (generation != Volatile.Read(ref _playGeneration))
            {
                DisposeNode(next);
                return;
            }

            next.EffectDefinitions.Add(_equalizer!);
            next.AddOutgoingConnection(_output!);
            next.MediaSourceCompleted += Input_MediaSourceCompleted;
            _input = next;
            _pausedPosition = TimeSpan.Zero;
            _playing = true;
            _graph.Start();

            if (crossfade && previous is not null && previous != next)
            {
                _fadingInput = previous;
                _fadingGain = _currentGain;
                _fadingNormalizationGain = _currentNormalizationGain;
                _currentNormalizationGain = normalizationGain;
                _currentGain = 0;
                _input.OutgoingGain = 0;
                previous.OutgoingGain = _volume * _fadingGain * _fadingNormalizationGain;
                var duration = fadeDuration ?? TimeSpan.FromSeconds(3);
                if (duration < TimeSpan.FromMilliseconds(100))
                {
                    DisposeNode(_fadingInput);
                    _fadingInput = null;
                    _currentGain = 1;
                    _input.OutgoingGain = _volume * _currentNormalizationGain;
                }
                else
                {
                    var cancellation = new CancellationTokenSource();
                    _fadeCancellation = cancellation;
                    _ = RunCrossfadeAsync(previous, next, generation, duration, _fadingGain,
                        _fadingNormalizationGain, cancellation);
                }
            }
            else
            {
                _currentGain = 1;
                next.OutgoingGain = _volume * _currentNormalizationGain;
            }
            next.Start();
        }
        catch (Exception ex)
        {
            if (generation == Volatile.Read(ref _playGeneration))
            {
                if (_input is null && previous is not null)
                {
                    _input = previous;
                    _playing = true;
                }
                else if (_input is null) _playing = false;
                PlaybackFailed?.Invoke(ex.Message);
            }
            throw;
        }
    }

    async Task RunCrossfadeAsync(MediaSourceAudioInputNode outgoing, MediaSourceAudioInputNode incoming,
        int generation, TimeSpan duration, double outgoingStartGain, double outgoingNormalizationGain,
        CancellationTokenSource cancellation)
    {
        var started = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            while (started.Elapsed < duration && generation == Volatile.Read(ref _playGeneration))
            {
                var progress = Math.Clamp(started.Elapsed.TotalMilliseconds / duration.TotalMilliseconds, 0, 1);
                _currentGain = progress;
                incoming.OutgoingGain = _volume * progress * _currentNormalizationGain;
                _fadingGain = 1 - progress;
                outgoing.OutgoingGain = _volume * (outgoingStartGain * (1 - progress) * outgoingNormalizationGain);
                await Task.Delay(25, cancellation.Token);
            }
            if (generation == Volatile.Read(ref _playGeneration) && _input == incoming)
            {
                _currentGain = 1;
                _fadingGain = 1;
                incoming.OutgoingGain = _volume * _currentNormalizationGain;
                if (_fadingInput == outgoing) _fadingInput = null;
                _fadingNormalizationGain = 1;
                DisposeNode(outgoing);
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        finally
        {
            if (ReferenceEquals(_fadeCancellation, cancellation)) _fadeCancellation = null;
            cancellation.Dispose();
        }
    }

    public void Pause()
    {
        if (_input is null || !_playing) return;
        Interlocked.Increment(ref _playGeneration);
        CancelFade();
        _pausedPosition = _input.Position;
        _input.Stop();
        DisposeNode(_fadingInput);
        _fadingInput = null;
        _currentGain = 1;
        _playing = false;
    }

    public void Resume()
    {
        if (_input is null || _playing) return;
        if (_pausedPosition > TimeSpan.Zero) _input.Seek(_pausedPosition);
        _input.OutgoingGain = _volume * _currentNormalizationGain;
        _input.Start();
        _currentGain = 1;
        _playing = true;
    }

    public void Seek(TimeSpan position)
    {
        if (_input is null) return;
        var end = _input.Duration;
        var safePosition = end > TimeSpan.Zero && position > end ? end : position;
        _input.Seek(safePosition < TimeSpan.Zero ? TimeSpan.Zero : safePosition);
        _pausedPosition = safePosition;
    }

    public void SetBandGain(int index, double gain)
    {
        if (_equalizer is null || index < 0 || index >= _equalizer.Bands.Count || !double.IsFinite(gain)) return;
        var safeDb = Math.Clamp(gain, -12, 12);
        var linearGain = Math.Clamp(Math.Pow(10, safeDb / 20), 0.126, 7.94);
        try { _equalizer.Bands[index].Gain = linearGain; }
        catch (Exception ex) { PlaybackFailed?.Invoke("The equalizer rejected this band value: " + ex.Message); }
    }

    public void SetBandGains(IReadOnlyList<double> gains)
    {
        if (_equalizer is null) return;
        for (var i = 0; i < Math.Min(gains.Count, _equalizer.Bands.Count); i++)
            SetBandGain(i, gains[i]);
    }

    void Input_MediaSourceCompleted(MediaSourceAudioInputNode sender, object args)
    {
        if (sender != _input)
        {
            if (sender == _fadingInput)
            {
                _fadingInput = null;
                _fadingGain = 1;
                if (_input is not null)
                {
                    _currentGain = 1;
                    _input.OutgoingGain = _volume * _currentNormalizationGain;
                }
                CancelFade();
            }
            DisposeNode(sender);
            return;
        }
        _playing = false;
        _pausedPosition = TimeSpan.Zero;
        DisposeNode(_fadingInput);
        _fadingInput = null;
        MediaEnded?.Invoke();
    }

    void CancelFade()
    {
        var cts = _fadeCancellation;
        _fadeCancellation = null;
        if (cts is not null)
        {
            try { cts.Cancel(); } catch (ObjectDisposedException) { }
        }
    }

    void DisposeNode(MediaSourceAudioInputNode? node)
    {
        if (node is null) return;
        try { node.MediaSourceCompleted -= Input_MediaSourceCompleted; } catch { }
        try { node.Stop(); } catch { }
        try { node.Dispose(); } catch { }
    }

    public void Dispose()
    {
        Interlocked.Increment(ref _playGeneration);
        CancelFade();
        DisposeNode(_input);
        DisposeNode(_fadingInput);
        _input = _fadingInput = null;
        _output?.Dispose();
        _graph?.Dispose();
    }
}

