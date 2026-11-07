using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace CharacterPlanner.Desktop.Views;

/// <summary>
/// The app's small dialogs, as the Go app's: a question, a form, or information with Close. Each is modal, centred on
/// the window that holds <c>anchor</c>, and does nothing when there is no such window.
/// </summary>
internal static class Dialogs
{
    /// <summary>A yes/no question, as Fyne's confirm dialog: true only for <paramref name="confirm"/>; the other button is the default.</summary>
    public static async Task<bool> ConfirmAsync(Control anchor, string title, string question, string confirm = "Yes", string keep = "No")
    {
        if (TopLevel.GetTopLevel(anchor) is not Window owner) { return false; }
        var yes = new Button { Name = "ConfirmYes", Content = confirm, Classes = { "accent" } };
        var no = new Button { Content = keep, IsDefault = true, IsCancel = true };
        var dialog = Make(title, Panel(new TextBlock { Text = question, TextWrapping = TextWrapping.Wrap }, Buttons(no, yes)), 420);
        yes.Click += (_, _) => dialog.Close(true);
        no.Click += (_, _) => dialog.Close(false);
        return await dialog.ShowDialog<bool>(owner);
    }

    /// <summary>
    /// A form: its fields, an inline error, the action button and a dismiss button. The action returns null when done
    /// (the dialog closes) or why not (shown, and the dialog stays open).
    /// </summary>
    public static async Task FormAsync(Control anchor, string title, string action, IReadOnlyList<Control> fields, Func<Task<string?>> run)
    {
        if (TopLevel.GetTopLevel(anchor) is not Window owner) { return; }
        var error = new TextBlock { Name = "FormError", Classes = { "error" }, TextWrapping = TextWrapping.Wrap, IsVisible = false };
        var go = new Button { Name = "FormAction", Content = action, IsDefault = true, Classes = { "accent" } };
        var cancel = new Button { Content = "Cancel", IsCancel = true };
        var dialog = Make(title, Panel([.. fields, error, Buttons(cancel, go)]), 480);
        go.Click += async (_, _) =>
        {
            go.IsEnabled = false;
            try
            {
                if (await run() is { } problem)
                {
                    error.Text = problem;
                    error.IsVisible = true;
                    return;
                }
                dialog.Close();
            }
            finally { go.IsEnabled = true; }
        };
        cancel.Click += (_, _) => dialog.Close();
        await dialog.ShowDialog(owner);
    }

    /// <summary>Information to read, with Close (the default) after any <paramref name="actions"/>.</summary>
    public static async Task InfoAsync(Control anchor, string title, IReadOnlyList<Control> content, params Button[] actions)
    {
        if (TopLevel.GetTopLevel(anchor) is not Window owner) { return; }
        var close = new Button { Content = "Close", IsDefault = true, IsCancel = true };
        var dialog = Make(title, Panel([.. content, Buttons([.. actions, close])]), 560);
        close.Click += (_, _) => dialog.Close();
        await dialog.ShowDialog(owner);
    }

    private static Window Make(string title, Control content, double width) => new()
    {
        Title = title,
        Width = width,
        SizeToContent = SizeToContent.Height,
        CanResize = false,
        WindowStartupLocation = WindowStartupLocation.CenterOwner,
        Content = content
    };

    private static StackPanel Panel(params Control[] children)
    {
        var panel = new StackPanel { Margin = new(24), Spacing = 12 };
        panel.Children.AddRange(children);
        return panel;
    }

    private static StackPanel Buttons(params Control[] buttons)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        row.Children.AddRange(buttons);
        return row;
    }
}
