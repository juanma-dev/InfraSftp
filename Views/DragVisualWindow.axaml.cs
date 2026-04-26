using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace InfraSftp.Views;

public partial class DragVisualWindow : Window
{
    public DragVisualWindow()
    {
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public void SetContent(string icon, string text)
    {
        var iconBlock = this.FindControl<TextBlock>("IconBlock");
        var textBlock = this.FindControl<TextBlock>("TextBlock");
        if (iconBlock != null) iconBlock.Text = icon;
        if (textBlock != null) textBlock.Text = text;
    }
}
