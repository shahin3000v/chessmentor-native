using System.Windows;

namespace ChessMentor.Desktop;

public partial class StudioLoginDialog : Window
{
    public StudioLoginDialog()
    {
        InitializeComponent();
        Loaded += (_, _) => IdentifierBox.Focus();
    }

    public string Identifier => IdentifierBox.Text;
    public string Password => PasswordBox.Password;

    private void OnLoginClick(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }
}
