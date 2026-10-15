using Microsoft.UI.Xaml.Media;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Collections.Specialized;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace Vybe.App;

public sealed class RangeObservableCollection<T> : ObservableCollection<T>
{
    public void AddRange(IEnumerable<T> items)
    {
        var batch = items.ToList();
        if (batch.Count == 0) return;
        CheckReentrancy();
        foreach (var item in batch) Items.Add(item);
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}

public sealed class TrackItem : INotifyPropertyChanged
{
    ImageSource? _artwork;

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Title { get; set; } = "Unknown track";
    public string Subtitle { get; set; } = "";
    public string Path { get; set; } = "";          // local file path, or resolved stream URL
    public string Source { get; set; } = "LOCAL";   // LOCAL | YT MUSIC | SOUNDCLOUD
    public string DurationLabel { get; set; } = "";
    public string WebUrl { get; set; } = "";
    public string Id { get; set; } = "";            // YouTube video id, or SoundCloud permalink
    public string ArtistName { get; set; } = "";
    public string ArtistId { get; set; } = "";
    public string ArtistUrl { get; set; } = "";
    public bool IsOnline { get; set; }
    [JsonIgnore]
    public ImageSource? Artwork
    {
        get => _artwork;
        set
        {
            if (ReferenceEquals(_artwork, value)) return;
            _artwork = value;
            OnPropertyChanged();
        }
    }
    public string ArtworkUrl { get; set; } = "";
    public DateTimeOffset ResolvedAt { get; set; }

    public bool IsStreamFresh => ResolvedAt != default && DateTimeOffset.UtcNow - ResolvedAt < TimeSpan.FromHours(2);

    void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

public sealed class PlaylistItem : INotifyPropertyChanged
{
    string _name = "New playlist";
    string _description = "";
    string _coverPath = "";
    string _coverUrl = "";
    ImageSource? _cover;
    DateTimeOffset _updatedAt = DateTimeOffset.Now;
    public event PropertyChangedEventHandler? PropertyChanged;
    public string Name { get => _name; set { _name = value; OnPropertyChanged(); } }
    public string Description { get => _description; set { _description = value; OnPropertyChanged(); } }
    public DateTimeOffset UpdatedAt { get => _updatedAt; set { _updatedAt = value; OnPropertyChanged(); } }
    public string CoverPath { get => _coverPath; set { _coverPath = value; OnPropertyChanged(); } }
    public string CoverUrl { get => _coverUrl; set { _coverUrl = value; OnPropertyChanged(); } }
    public List<TrackItem> Tracks { get; set; } = new();
    [JsonIgnore]
    public ImageSource? Cover { get => _cover; set { _cover = value; OnPropertyChanged(); } }
    void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

