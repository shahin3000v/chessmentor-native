using System.Windows;
using System.Windows.Controls;
using ChessMentor.Desktop.Controls;
using ChessMentor.Desktop.ViewModels;
using Microsoft.Win32;

namespace ChessMentor.Desktop;

public partial class MoveTrainerWindow : Window
{
    private readonly MoveTrainerWindowViewModel _viewModel;

    public MoveTrainerWindow(MoveTrainerWindowViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
        Closed += (_, _) => _viewModel.Dispose();
    }

    private async void OnOpenPgnClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            AddExtension = true,
            CheckFileExists = true,
            Filter = "PGN files (*.pgn)|*.pgn|Text files (*.txt)|*.txt|All files (*.*)|*.*",
            Multiselect = true,
            Title = "انتخاب فایل‌های PGN برای MoveTrainer",
        };
        if (dialog.ShowDialog(this) == true)
        {
            await _viewModel.LoadPgnFilesAsync(dialog.FileNames).ConfigureAwait(true);
            TrainerBoard.Focus();
        }
    }

    private async void OnCourseSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.AddedItems.Count > 0 && e.AddedItems[0] is MoveTrainerCourseListItem entry)
        {
            await _viewModel.LoadCourseAsync(entry).ConfigureAwait(true);
            TrainerBoard.Focus();
        }
    }

    private async void OnMoveRequested(object sender, BoardMoveRequestedEventArgs e)
    {
        await _viewModel.HandleMoveAsync(e).ConfigureAwait(true);
        TrainerBoard.Focus();
    }

    private void OnPositionRejected(object sender, FenErrorEventArgs e) =>
        _viewModel.ReportBoardError(e.Message);
}
