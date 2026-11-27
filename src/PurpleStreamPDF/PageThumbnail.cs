using System.ComponentModel;
using System.Windows.Media;

namespace PurpleStreamPDF;

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

    /// <summary>The drawing number written on the sheet, shown under the page number; null when there is none.</summary>
    public string? DrawingNumber
    {
        get => _drawingNumber;
        set
        {
            _drawingNumber = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DrawingNumber)));
        }
    }
    string? _drawingNumber;

    public event PropertyChangedEventHandler? PropertyChanged;
}
