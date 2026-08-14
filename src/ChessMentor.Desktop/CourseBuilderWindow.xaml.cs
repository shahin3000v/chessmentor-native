using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using ChessMentor.CourseBuilder;
using ChessMentor.Desktop.Controls;
using ChessMentor.Desktop.ViewModels;
using Microsoft.Win32;

namespace ChessMentor.Desktop;

public partial class CourseBuilderWindow : Window
{
    private readonly CourseBuilderWindowViewModel _viewModel;
    private Point _sourceDragStart;
    private bool _allowClose;

    public CourseBuilderWindow(CourseBuilderWindowViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
        Closing += OnClosing;
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
            Title = "انتخاب Sourceهای PGN برای Course Builder",
        };
        if (dialog.ShowDialog(this) == true)
        {
            await _viewModel.LoadPgnFilesAsync(dialog.FileNames).ConfigureAwait(true);
        }
    }

    private async void OnDocumentSelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (e.AddedItems.Count > 0 && e.AddedItems[0] is CourseBuilderDocumentSummary summary)
        {
            await _viewModel.LoadDocumentAsync(summary).ConfigureAwait(true);
        }
    }

    private void OnSourceMouseLeftButtonDown(object sender, MouseButtonEventArgs e) =>
        _sourceDragStart = e.GetPosition(SourcesList);

    private void OnSourceMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || SourcesList.SelectedItem is not CourseSourceItem source)
        {
            return;
        }

        var current = e.GetPosition(SourcesList);
        if (Math.Abs(current.X - _sourceDragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(current.Y - _sourceDragStart.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        _ = DragDrop.DoDragDrop(SourcesList, source, DragDropEffects.Copy);
    }

    private void OnSourceDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (SourcesList.SelectedItem is CourseSourceItem source)
        {
            _viewModel.AddSource(source);
        }
    }

    private void OnCanvasDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(typeof(CourseSourceItem)) && e.Data.GetData(typeof(CourseSourceItem)) is CourseSourceItem source)
        {
            _viewModel.AddSource(source);
            e.Handled = true;
        }
    }

    private void OnDetachClick(object sender, RoutedEventArgs e) => _viewModel.AttachmentTargetId = null;

    private void OnPositionRejected(object sender, FenErrorEventArgs e) =>
        MessageBox.Show(this, e.Message, "FEN نامعتبر", MessageBoxButton.OK, MessageBoxImage.Warning);

    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_allowClose)
        {
            return;
        }

        e.Cancel = true;
        if (!await _viewModel.FlushAsync().ConfigureAwait(true) &&
            MessageBox.Show(
                this,
                "ذخیره خودکار ناموفق بود. بدون ذخیره بسته شود؟",
                "Course Builder",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No,
                MessageBoxOptions.RightAlign | MessageBoxOptions.RtlReading) != MessageBoxResult.Yes)
        {
            return;
        }

        _allowClose = true;
        Close();
    }
}
