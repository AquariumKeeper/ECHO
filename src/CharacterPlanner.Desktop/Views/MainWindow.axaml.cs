using Avalonia.Controls;
using CharacterPlanner.Desktop.Services;

namespace CharacterPlanner.Desktop.Views;

public partial class MainWindow : Window
{
    /// <summary>Where the window's size is kept between runs, or null to keep none (as in tests).</summary>
    private readonly string? sizeFile;

    public MainWindow() : this(null) { }

    public MainWindow(string? sizeFile)
    {
        InitializeComponent();
        this.sizeFile = sizeFile;
        // The last size, as the Go app restored it; the default otherwise.
        if (sizeFile is not null && WindowSize.Load(sizeFile) is { } size) { (Width, Height) = size; }
        Closing += (_, _) =>
        {
            if (this.sizeFile is not null && WindowState == WindowState.Normal) { WindowSize.Save(this.sizeFile, Width, Height); }
        };
    }
}
