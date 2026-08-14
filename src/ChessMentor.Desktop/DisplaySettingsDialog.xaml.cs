using System.Windows;
using Microsoft.Win32;

namespace ChessMentor.Desktop;

public partial class DisplaySettingsDialog : Window
{
    private readonly Func<string, CancellationToken, Task> _selectCustomFont;
    private readonly Func<CancellationToken, Task> _resetCommentFont;

    public DisplaySettingsDialog(
        Window owner,
        object viewModel,
        bool showMoveListSettings,
        Func<string, CancellationToken, Task> selectCustomFont,
        Func<CancellationToken, Task> resetCommentFont)
    {
        InitializeComponent();
        Owner = owner;
        MoveListSection.Visibility = showMoveListSettings
            ? Visibility.Visible
            : Visibility.Collapsed;
        if (!showMoveListSettings)
        {
            // Prevent WPF from probing Viewer-only bindings against the Studio VM.
            MoveListSection.DataContext = null;
        }

        DataContext = viewModel;
        _selectCustomFont = selectCustomFont;
        _resetCommentFont = resetCommentFont;
    }

    private async void OnChooseCommentFontClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            AddExtension = true,
            CheckFileExists = true,
            DefaultExt = ".ttf",
            Filter = "Font files (*.ttf;*.otf)|*.ttf;*.otf",
            Multiselect = false,
            Title = "انتخاب فونت توضیحات فارسی",
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        await RunFontActionAsync(token => _selectCustomFont(dialog.FileName, token)).ConfigureAwait(true);
    }

    private async void OnResetCommentFontClick(object sender, RoutedEventArgs e) =>
        await RunFontActionAsync(_resetCommentFont).ConfigureAwait(true);

    private async Task RunFontActionAsync(Func<CancellationToken, Task> action)
    {
        try
        {
            await action(CancellationToken.None).ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                this,
                exception.Message,
                "تغییر فونت ناموفق بود",
                MessageBoxButton.OK,
                MessageBoxImage.Error,
                MessageBoxResult.OK,
                MessageBoxOptions.RightAlign | MessageBoxOptions.RtlReading);
        }
    }
}
