using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using LastLogon.Core;

namespace LastLogon;

public partial class AdDialog : Window
{
    public string Prefix => PrefixBox.Text.Trim();
    public string? Ou => string.IsNullOrWhiteSpace(OuBox.Text) ? null : OuBox.Text.Trim();

    public AdDialog() => InitializeComponent();

    private void Load_Click(object sender, RoutedEventArgs e)
    {
        // The form of the name is checked here, beside the box, instead of after the dialog has
        // closed. Whether the name lies in this computer's domain only the search can tell.
        if (AdComputerSearch.DescribeDistinguishedNameProblem(OuBox.Text) is { } problem)
        {
            OuErrorLabel.Text = problem;
            OuErrorLabel.Visibility = Visibility.Visible;
            OuHint.Visibility = Visibility.Hidden;
            OuBox.Focus();
            Keyboard.Focus(OuBox);
            return;
        }

        DialogResult = true;
    }

    // Editing the name takes the message away: it described the text that was there before.
    private void OuBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (OuErrorLabel is null || OuHint is null) return;

        OuErrorLabel.Visibility = Visibility.Hidden;
        OuHint.Visibility = Visibility.Visible;
    }
}
