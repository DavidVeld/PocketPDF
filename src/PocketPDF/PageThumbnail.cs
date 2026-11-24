using System.ComponentModel;
using System.Windows.Media;

namespace PocketPDF;

/// <summary>A page in the page list. The picture is rendered only once the item scrolls into view.</summary>
public sealed class PageThumbnail(int number) : INotifyPropertyChanged
{
    ImageSource? _image;

    public int Number { get; } = number;

    /// <summary>True once rendering was asked for, so scrolling back and forth doesn't render twice.</summary>
    public bool IsRequested { get; set; }

    public ImageSource? Image
    {
        get => _image;
        set
        {
            _image = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Image)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
