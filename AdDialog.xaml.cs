using System.Windows;

namespace LastLogon;

public partial class AdDialog : Window
{
    public string Prefix => PrefixBox.Text.Trim();
    public string? Ou => string.IsNullOrWhiteSpace(OuBox.Text) ? null : OuBox.Text.Trim();

    public AdDialog() => InitializeComponent();

    private void Search_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
