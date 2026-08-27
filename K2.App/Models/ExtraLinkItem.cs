using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media.Imaging;

namespace K2.App.Models;

/// <summary>
/// One card in the Settings tab's "Extra" link list (see MainWindow.Settings.cs's
/// InitExtraLinksPanel). Title/Image start at the fallback values and are replaced
/// once LinkPreviewService resolves the page's Open Graph metadata.
/// </summary>
public sealed class ExtraLinkItem : INotifyPropertyChanged
{
    public string Url { get; }

    public ExtraLinkItem(string url, string fallbackTitle)
    {
        Url = url;
        _title = fallbackTitle;
    }

    private string _title;
    public string Title
    {
        get => _title;
        set { if (_title == value) return; _title = value; OnChanged(); }
    }

    private BitmapImage? _image;
    public BitmapImage? Image
    {
        get => _image;
        set { if (_image == value) return; _image = value; OnChanged(); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
