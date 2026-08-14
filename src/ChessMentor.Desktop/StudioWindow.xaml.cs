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

public partial class StudioWindow : Window
{
    private readonly StudioWindowViewModel _viewModel;

    public StudioWindow(StudioWindowViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
        Closed += (_, _) => _viewModel.Dispose();
    }

    private async void OnOpenFilesClick(object sender, RoutedEventArgs e) =>
        await PickFilesAsync(append: false).ConfigureAwait(true);

    private async void OnAppendFilesClick(object sender, RoutedEventArgs e) =>
        await PickFilesAsync(append: true).ConfigureAwait(true);

    private void OnOpenSettingsClick(object sender, RoutedEventArgs e)
    {
        var dialog = new DisplaySettingsDialog(
            this,
            _viewModel,
            showMoveListSettings: false,
            _viewModel.SelectCustomCommentFontAsync,
            _viewModel.ResetCommentFontAsync);
        _ = dialog.ShowDialog();
    }

    private async void OnChooseFeaturedImageClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            AddExtension = true,
            CheckFileExists = true,
            Filter = "Course image (*.jpg;*.jpeg;*.png;*.webp)|*.jpg;*.jpeg;*.png;*.webp",
            Multiselect = false,
            Title = "انتخاب تصویر شاخص Draft یا دوره",
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            await _viewModel.SelectFeaturedImageAsync(dialog.FileName).ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                this,
                exception.Message,
                "تصویر شاخص نامعتبر است",
                MessageBoxButton.OK,
                MessageBoxImage.Error,
                MessageBoxResult.OK,
                MessageBoxOptions.RightAlign | MessageBoxOptions.RtlReading);
        }
    }

    private void OnClearFeaturedImageClick(object sender, RoutedEventArgs e) =>
        _viewModel.ClearFeaturedImage();

    private async Task PickFilesAsync(bool append)
    {
        var dialog = new OpenFileDialog
        {
            AddExtension = true,
            CheckFileExists = true,
            DefaultExt = ".pgn",
            Filter = "PGN files (*.pgn;*.txt)|*.pgn;*.txt|All files (*.*)|*.*",
            Multiselect = true,
            Title = append ? "افزودن فایل‌های PGN به Studio" : "باز کردن فایل‌های PGN در Studio",
        };
        if (dialog.ShowDialog(this) == true)
        {
            await _viewModel.LoadFilesAsync(dialog.FileNames, append).ConfigureAwait(true);
            FocusBoard();
        }
    }

    private async void OnExportPgnClick(object sender, RoutedEventArgs e)
    {
        if (!_viewModel.Session.HasGames)
        {
            return;
        }

        var dialog = new SaveFileDialog
        {
            AddExtension = true,
            DefaultExt = ".pgn",
            Filter = "PGN file (*.pgn)|*.pgn|Text file (*.txt)|*.txt",
            FileName = $"chessmentor-studio-{_viewModel.GameCount}-games.pgn",
            Title = "ذخیره PGN ویرایش‌شده",
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            await _viewModel.SavePgnAsync(dialog.FileName).ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                this,
                exception.Message,
                "ذخیره PGN ناموفق بود",
                MessageBoxButton.OK,
                MessageBoxImage.Error,
                MessageBoxResult.OK,
                MessageBoxOptions.RightAlign | MessageBoxOptions.RtlReading);
        }
    }

    private async void OnMoveRequested(object sender, BoardMoveRequestedEventArgs e) =>
        await _viewModel.HandleCandidateMoveAsync(e.From, e.To, e.Promotion).ConfigureAwait(true);

    private void OnRenderCompleted(object sender, double elapsedMilliseconds) =>
        _viewModel.RecordRender(elapsedMilliseconds);

    private void OnPositionRejected(object sender, FenErrorEventArgs e) =>
        _viewModel.ReportPositionError(e.Message);

    private void OnMoveSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ListBox { SelectedItem: not null } list)
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

    private void OnSplitterDragCompleted(object sender, DragCompletedEventArgs e)
    {
        _viewModel.UpdatePanelWidths(StudioMovesColumn.ActualWidth, StudioGamesColumn.ActualWidth);
        BindingOperations.SetBinding(
            StudioMovesColumn,
            ColumnDefinition.WidthProperty,
            new Binding(nameof(StudioWindowViewModel.StudioMovesColumnWidth)) { Source = _viewModel });
        BindingOperations.SetBinding(
            StudioGamesColumn,
            ColumnDefinition.WidthProperty,
            new Binding(nameof(StudioWindowViewModel.StudioGamesColumnWidth)) { Source = _viewModel });
    }

    private void OnEditMoveCommentClick(object sender, RoutedEventArgs e)
    {
        if (NodeIdFromMenu(sender) is not { } nodeId)
        {
            return;
        }

        _viewModel.SelectNode(nodeId);
        Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() => CommentEditorBox.Focus()));
    }

    private void OnPromoteMoveClick(object sender, RoutedEventArgs e)
    {
        if (NodeIdFromMenu(sender) is not { } nodeId)
        {
            return;
        }

        _viewModel.SelectNode(nodeId);
        if (_viewModel.PromoteBranchCommand.CanExecute(null))
        {
            _viewModel.PromoteBranchCommand.Execute(null);
        }
    }

    private void OnDeleteBranchClick(object sender, RoutedEventArgs e)
    {
        if (NodeIdFromMenu(sender) is not { } nodeId)
        {
            return;
        }

        _viewModel.SelectNode(nodeId);
        ConfirmAndDeleteCurrentBranch();
    }

    private void OnDeleteCurrentBranchClick(object sender, RoutedEventArgs e) =>
        ConfirmAndDeleteCurrentBranch();

    private async void OnDeleteAudioClick(object sender, RoutedEventArgs e)
    {
        if (_viewModel.SelectedAudio is null)
        {
            return;
        }

        var answer = MessageBox.Show(
            this,
            "این صدای متصل به حرکت حذف شود؟ نسخه Server نیز در اولین Sync حذف خواهد شد.",
            "حذف صدای حرکت",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No,
            MessageBoxOptions.RightAlign | MessageBoxOptions.RtlReading);
        if (answer == MessageBoxResult.Yes)
        {
            await _viewModel.DeleteSelectedAudioAsync().ConfigureAwait(true);
        }
    }

    private void ConfirmAndDeleteCurrentBranch()
    {
        if (_viewModel.Session.CurrentNode?.Parent is null)
        {
            return;
        }

        var answer = MessageBox.Show(
            this,
            "این حرکت و تمام ادامه‌های زیر آن از PGN حذف شوند؟",
            "حذف شاخه",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No,
            MessageBoxOptions.RightAlign | MessageBoxOptions.RtlReading);
        if (answer == MessageBoxResult.Yes)
        {
            _viewModel.DeleteCurrentBranch();
        }
    }

    private void OnRemoveGameClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: ViewerGameItem game })
        {
            return;
        }

        var answer = MessageBox.Show(
            this,
            $"بازی «{game.White} – {game.Black}» از Studio حذف شود؟",
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
            $"{count} بازی انتخاب‌شده از Studio حذف شوند؟",
            "حذف گروهی بازی‌ها",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question,
            MessageBoxResult.No,
            MessageBoxOptions.RightAlign | MessageBoxOptions.RtlReading);
        if (answer == MessageBoxResult.Yes)
        {
            _viewModel.RemoveMarkedGames();
        }
    }

    private async void OnLoginClick(object sender, RoutedEventArgs e)
    {
        var dialog = new StudioLoginDialog { Owner = this };
        if (dialog.ShowDialog() == true)
        {
            await _viewModel.LoginAsync(dialog.Identifier, dialog.Password).ConfigureAwait(true);
        }
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.FocusedElement is TextBoxBase or ComboBox or PasswordBox)
        {
            return;
        }

        if (_viewModel.IsBranchChooserOpen && e.Key is Key.Up or Key.Down or Key.Enter)
        {
            if (e.Key == Key.Enter)
            {
                if (_viewModel.ChooseBranchCommand.CanExecute(null))
                {
                    _viewModel.ChooseBranchCommand.Execute(null);
                }
            }
            else if (StudioBranchList.Items.Count > 0)
            {
                var delta = e.Key == Key.Down ? 1 : -1;
                var current = StudioBranchList.SelectedIndex < 0 ? 0 : StudioBranchList.SelectedIndex;
                StudioBranchList.SelectedIndex =
                    (current + delta + StudioBranchList.Items.Count) % StudioBranchList.Items.Count;
                StudioBranchList.ScrollIntoView(StudioBranchList.SelectedItem);
            }

            e.Handled = true;
            return;
        }

        if (e.Key is Key.Left or Key.Right or Key.Home or Key.End or Key.Escape)
        {
            _viewModel.HandleKeyboardNavigation(e.Key);
            e.Handled = true;
            FocusBoard();
        }
    }

    private static string? NodeIdFromMenu(object sender)
    {
        if (sender is not MenuItem item)
        {
            return null;
        }

        if (item.CommandParameter is string { Length: > 0 } nodeId)
        {
            return nodeId;
        }

        var contextMenu = ItemsControl.ItemsControlFromItemContainer(item) as ContextMenu;
        return contextMenu?.PlacementTarget is FrameworkElement { DataContext: ViewerMoveItem move }
            ? move.NodeId
            : null;
    }

    private void FocusBoard() => Dispatcher.BeginInvoke(
        DispatcherPriority.Input,
        new Action(() => StudioBoard.Focus()));
}
