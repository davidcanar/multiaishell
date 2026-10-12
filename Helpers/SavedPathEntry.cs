using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace MultiAI.Helpers;

public class SavedPathEntry : INotifyPropertyChanged
{
    private string _path = "";
    private string _name = "";

    public string Path { get => _path; set { _path = value; OnPropertyChanged(); } }
    public string Name { get => _name; set { _name = value; OnPropertyChanged(); } }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
