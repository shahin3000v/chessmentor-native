using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Data;
using System.Windows.Threading;
using ChessMentor.Desktop.Controls;
using ChessMentor.Desktop.ViewModels;
using ChessMentor.Viewer;
using Microsoft.Win32;

namespace ChessMentor.Desktop;

public partial class MainWindow : Window
{
    private readonly MainWindowViewModel _viewModel;
    private readonly Action _openStudio;

    public MainWindow(MainWindowViewModel viewModel, Action openStudio)
    {
        InitializeComponent();
        _viewModel = viewModel;
        _openStudio = openStudio;
        DataContext = viewModel;
        Closed += (_, _) => _viewModel.Dispose();
    }

    private void OnOpenStudioClick(object sender, RoutedEventArgs e) => _openStudio();

    private void OnOpenSettingsClick(object sender, RoutedEventArgs e)
    {
        var dialog = new DisplaySettingsDialog(
            this,
            _viewModel,
            showMoveListSettings: true,
            _viewModel.SelectCustomCommentFontAsync,
            _viewModel.ResetCommentFontAsync);
        _ = dialog.ShowDialog();
    }

    private async void OnUpgradeDatabaseClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            AddExtension = true,
            CheckFileExists = true,
            DefaultExt = ".db",
            Filter = "SQLite databases (*.db;*.sqlite;*.sqlite3)|*.db;*.sqlite;*.sqlite3|All files (*.*)|*.*",
            Multiselect = false,
            Title = "انتخاب دیتابیس ChessMentor برای ارتقا",
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            var result = await _viewModel.UpgradeDatabaseAsync(dialog.FileName).ConfigureAwait(true);
            MessageBox.Show(
                this,
                $"ساختار دیتابیس منطبق بود و ادغام با موفقیت انجام شد.\n\n" +
                $"نسخه Schema مبدأ: {result.SourceSchemaVersion}\n" +
                $"رکوردهای مبدأ: {result.SourceRows:N0}\n" +
                $"وارد یا به‌روز شده: {result.ImportedOrUpdatedRows:N0}\n" +
                $"بدون تغییر: {result.UnchangedRows:N0}",
                "ارتقای دیتابیس موفق بود",
                MessageBoxButton.OK,
                MessageBoxImage.Information,
                MessageBoxResult.OK,
                MessageBoxOptions.RightAlign | MessageBoxOptions.RtlReading);
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                this,
                exception.Message,
                "ارتقای دیتابیس ناموفق بود",
                MessageBoxButton.OK,
                MessageBoxImage.Error,
                MessageBoxResult.OK,
                MessageBoxOptions.RightAlign | MessageBoxOptions.RtlReading);
        }
    }

    private async void OnOpenFilesClick(object sender, RoutedEventArgs e) =>
        await PickFilesAsync(append: false).ConfigureAwait(true);

    private async void OnAppendFilesClick(object sender, RoutedEventArgs e) =>
        await PickFilesAsync(append: true).ConfigureAwait(true);

    private async Task PickFilesAsync(bool append)
    {
        var dialog = new OpenFileDialog
        {
            AddExtension = true,
            CheckFileExists = true,
            DefaultExt = ".pgn",
            Filter = "PGN files (*.pgn;*.txt)|*.pgn;*.txt|All files (*.*)|*.*",
            Multiselect = true,
            Title = append ? "افزودن فایل‌های PGN" : "باز کردن فایل‌های PGN",
        };
        if (dialog.ShowDialog(this) == true)
        {
            await _viewModel.LoadFilesAsync(dialog.FileNames, append).ConfigureAwait(true);
            FocusBoard();
        }
    }

    private async void OnMoveRequested(object sender, BoardMoveRequestedEventArgs e) =>
        await _viewModel.HandleCandidateMoveAsync(e).ConfigureAwait(true);

    private void OnRenderCompleted(object sender, double elapsedMilliseconds) =>
        _viewModel.RecordRender(elapsedMilliseconds);

    private void OnPositionRejected(object sender, FenErrorEventArgs e) =>
        _viewModel.ReportPositionError(e.Message);

    private void OnMoveSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ListBox list && list.SelectedItem is not null)
        {
            list.ScrollIntoView(list.SelectedItem);
            FocusBoard();
        }
    }

    private void OnMoveCellClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: ViewerMoveItem move })
        {
            _viewModel.SelectedMoveItem = move;
            FocusBoard();
        }
    }

    private void OnGameSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.AddedItems.Count > 0)
        {
            FocusBoard();
        }
    }

    private void OnNavigationButtonClick(object sender, RoutedEventArgs e) => FocusBoard();

    private void OnSplitterDragCompleted(object sender, DragCompletedEventArgs e)
    {
        var gamesWidth = _viewModel.GamesPanelCollapsed
            ? _viewModel.GamesPanelWidth
            : GamesColumn.ActualWidth;
        _viewModel.UpdatePanelWidths(MovesColumn.ActualWidth, gamesWidth);
        BindingOperations.SetBinding(
            MovesColumn,
            ColumnDefinition.WidthProperty,
            new Binding(nameof(MainWindowViewModel.MovesColumnWidth)) { Source = _viewModel });
        BindingOperations.SetBinding(
            GamesColumn,
            ColumnDefinition.WidthProperty,
            new Binding(nameof(MainWindowViewModel.GamesColumnWidth)) { Source = _viewModel });
        BindingOperations.SetBinding(
            GamesSplitterColumn,
            ColumnDefinition.WidthProperty,
            new Binding(nameof(MainWindowViewModel.GamesSplitterWidth)) { Source = _viewModel });
    }

    private void OnRemoveGameClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: ViewerGameItem game })
        {
            return;
        }

        var answer = MessageBox.Show(
            this,
            $"بازی «{game.White} – {game.Black}» از Workspace حذف شود؟",
            "حذف بازی",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question,
            MessageBoxResult.No,
            MessageBoxOptions.RightAlign | MessageBoxOptions.RtlReading);
        if (answer == MessageBoxResult.Yes)
        {
            _viewModel.RemoveGame(game);
        }
    }

    private void OnRemoveMarkedGamesClick(object sender, RoutedEventArgs e)
    {
        var count = _viewModel.Games.Count(static game => game.IsMarked);
        if (count == 0)
        {
            return;
        }

        var answer = MessageBox.Show(
            this,
            $"{count} بازی انتخاب‌شده از Workspace حذف شوند؟",
            "حذف گروهی بازی‌ها",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question,
            MessageBoxResult.No,
            MessageBoxOptions.RightAlign | MessageBoxOptions.RtlReading);
        if (answer == MessageBoxResult.Yes)
        {
            _viewModel.RemoveMarkedGames();
            SelectAllGames.IsChecked = false;
        }
    }

    private void OnSelectAllGamesClick(object sender, RoutedEventArgs e) =>
        _viewModel.MarkAllGames(SelectAllGames.IsChecked == true);

    private void OnBranchMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (BranchList.SelectedItem is not null)
        {
            ChooseCurrentBranch();
            e.Handled = true;
        }
    }

    private void OnBranchPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        MoveBranchSelection(e.Delta < 0 ? 1 : -1);
        e.Handled = true;
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.FocusedElement is TextBoxBase or ComboBox or PasswordBox)
        {
            return;
        }

        if (_viewModel.IsBranchChooserOpen)
        {
            switch (e.Key)
            {
                case Key.Up:
                    MoveBranchSelection(-1);
                    e.Handled = true;
                    return;
                case Key.Down:
                    MoveBranchSelection(1);
                    e.Handled = true;
                    return;
                case Key.Right:
                    BranchList.SelectedIndex = 0;
                    ChooseCurrentBranch();
                    e.Handled = true;
                    return;
                case Key.Enter:
                    ChooseCurrentBranch();
                    e.Handled = true;
                    return;
                case Key.Left:
                    _viewModel.HandleKeyboardNavigation(Key.Left);
                    e.Handled = true;
                    return;
                case Key.Escape:
                    _viewModel.HandleKeyboardNavigation(Key.Escape);
                    e.Handled = true;
                    return;
            }
        }

        if (e.Key is Key.Left or Key.Right or Key.Home or Key.End or Key.Escape)
        {
            _viewModel.HandleKeyboardNavigation(e.Key);
            e.Handled = true;
            FocusBoard();
        }
    }

    private void MoveBranchSelection(int delta)
    {
        if (BranchList.Items.Count == 0)
        {
            return;
        }

        var current = BranchList.SelectedIndex < 0 ? 0 : BranchList.SelectedIndex;
        BranchList.SelectedIndex = (current + delta + BranchList.Items.Count) % BranchList.Items.Count;
        BranchList.ScrollIntoView(BranchList.SelectedItem);
    }

    private void ChooseCurrentBranch()
    {
        if (_viewModel.ChooseBranchCommand.CanExecute(null))
        {
            _viewModel.ChooseBranchCommand.Execute(null);
            FocusBoard();
        }
    }

    private void FocusBoard() => Dispatcher.BeginInvoke(
        DispatcherPriority.Input,
        new Action(() => NativeBoard.Focus()));
}
