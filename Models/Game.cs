using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace GameLibrary.Models;

public sealed class Game : INotifyPropertyChanged
{
    private string _name = "";
    private string _executablePath = "";
    private string? _coverPath;
    private double _hoursPlayed;

    public Guid Id { get; set; } = Guid.NewGuid();
    public DateTime? LastPlayedUtc { get; set; }
    public string Name { get => _name; set { _name = value; OnPropertyChanged(); } }
    public string ExecutablePath { get => _executablePath; set { _executablePath = value; OnPropertyChanged(); } }
    public string? CoverPath { get => _coverPath; set { _coverPath = value; OnPropertyChanged(); } }
    public double HoursPlayed
    {
        get => _hoursPlayed;
        set { _hoursPlayed = Math.Max(0, value); OnPropertyChanged(); OnPropertyChanged(nameof(PlaytimeText)); }
    }
    public string PlaytimeText => HoursPlayed < 1 ? $"{HoursPlayed * 60:F0} MIN JOGADOS" : $"{HoursPlayed:F1} H JOGADAS";
    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
