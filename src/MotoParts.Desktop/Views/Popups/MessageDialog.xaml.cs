using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace MotoParts.Desktop.Views.Popups;

public enum MessageKind
{
    Info,
    Error,
    Question,
    Danger,
}

/// <summary>An always-Arabic message, confirmation or one-line prompt.</summary>
public partial class MessageDialog : Window
{
    public MessageDialog()
    {
        InitializeComponent();
    }

    public string? InputText => Input.Text;

    public static MessageDialog Create(MessageKind kind, string title, string message, string yes, string? no)
    {
        var dialog = new MessageDialog
        {
            Title = title,
        };

        dialog.Heading.Text = title;
        dialog.Body.Text = message;
        dialog.Yes.Content = yes;

        if (no is null)
        {
            dialog.No.Visibility = Visibility.Collapsed;
        }
        else
        {
            dialog.No.Content = no;
        }

        (dialog.Glyph.Text, dialog.Glyph.Foreground) = kind switch
        {
            MessageKind.Error => ("", (Brush)Application.Current.FindResource("Danger")),
            MessageKind.Danger => ("", (Brush)Application.Current.FindResource("Danger")),
            MessageKind.Question => ("", (Brush)Application.Current.FindResource("Primary")),
            _ => ("", (Brush)Application.Current.FindResource("Primary")),
        };

        if (kind == MessageKind.Danger)
        {
            dialog.Yes.Style = (Style)Application.Current.FindResource("DangerButton");
        }

        return dialog;
    }

    /// <summary>Turns the dialog into a prompt with a text box under the message.</summary>
    public MessageDialog WithInput(string? initial, bool multiline)
    {
        Input.Visibility = Visibility.Visible;
        Input.Text = initial ?? "";

        if (multiline)
        {
            Input.Style = (Style)Application.Current.FindResource("MultiLineBox");
            Input.MinHeight = 80;
            Yes.IsDefault = false;
        }

        Loaded += (_, _) =>
        {
            Input.Focus();
            Input.SelectAll();
        };

        return this;
    }

    private void OnYes(object sender, RoutedEventArgs e) => DialogResult = true;

    private void OnNo(object sender, RoutedEventArgs e) => DialogResult = false;
}
