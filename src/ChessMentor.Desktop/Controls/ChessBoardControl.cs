using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ChessMentor.Chess;

namespace ChessMentor.Desktop.Controls;

/// <summary>
/// The single native board renderer shared by every application surface.
/// It owns no chess rules and raises candidate moves for the active domain to validate.
/// </summary>
public sealed class ChessBoardControl : FrameworkElement
{
    private const double BorderThicknessRatio = 0.018;
    private static readonly IReadOnlyDictionary<char, string> PieceAssets = new Dictionary<char, string>
    {
        ['K'] = "Chess_klt60.png", ['Q'] = "Chess_qlt60.png", ['R'] = "Chess_rlt60.png",
        ['B'] = "Chess_blt60.png", ['N'] = "Chess_nlt60.png", ['P'] = "Chess_plt60.png",
        ['k'] = "Chess_kdt60.png", ['q'] = "Chess_qdt60.png", ['r'] = "Chess_rdt60.png",
        ['b'] = "Chess_bdt60.png", ['n'] = "Chess_ndt60.png", ['p'] = "Chess_pdt60.png",
    };
    private static readonly Dictionary<char, BitmapSource> PieceCache = [];
    private static readonly Lock PieceCacheLock = new();
    private static readonly Brush SelectionBrush = FrozenBrush(Color.FromArgb(110, 247, 210, 67));
    private static readonly Brush LastMoveBrush = FrozenBrush(Color.FromArgb(90, 234, 179, 8));
    private static readonly Brush HighlightBrush = FrozenBrush(Color.FromArgb(105, 76, 175, 80));
    private static readonly Brush LegalTargetBrush = FrozenBrush(Color.FromArgb(115, 17, 24, 39));
    private static readonly SkinPalette ChessmentorPalette = new("#F0D9B5", "#B58863", "#422006", "#422006");
    private static readonly SkinPalette RokhamoozPalette = new("#E8DDC5", "#6C968A", "#213B37", "#213B37");
    private static readonly SkinPalette MurphyPalette = new("#FFFFFF", "#686868", "#171717", "#171717");
    private readonly BoardInteractionState _interaction = new();
    private FenPosition _parsedPosition = FenPosition.Parse(FenPosition.Initial);
    private DrawingGroup? _backgroundCache;
    private BackgroundCacheKey? _backgroundCacheKey;
    private Point _pointer;

    public ChessBoardControl()
    {
        // The application shell is RTL, but a chessboard is a physical coordinate surface.
        // Inheriting RTL makes WPF mirror both the files and the piece bitmaps.
        FlowDirection = System.Windows.FlowDirection.LeftToRight;
        Focusable = true;
        SnapsToDevicePixels = true;
        ClipToBounds = true;
        MinWidth = 240;
        MinHeight = 240;
    }

    public static readonly DependencyProperty PositionProperty = DependencyProperty.Register(
        nameof(Position),
        typeof(string),
        typeof(ChessBoardControl),
        new FrameworkPropertyMetadata(FenPosition.Initial, FrameworkPropertyMetadataOptions.AffectsRender, OnPositionChanged));

    public static readonly DependencyProperty SkinProperty = DependencyProperty.Register(
        nameof(Skin),
        typeof(BoardSkin),
        typeof(ChessBoardControl),
        new FrameworkPropertyMetadata(BoardSkin.Chessmentor, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty OrientationProperty = DependencyProperty.Register(
        nameof(Orientation),
        typeof(BoardOrientation),
        typeof(ChessBoardControl),
        new FrameworkPropertyMetadata(BoardOrientation.White, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ShowCoordinatesProperty = DependencyProperty.Register(
        nameof(ShowCoordinates),
        typeof(bool),
        typeof(ChessBoardControl),
        new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty OverlayProperty = DependencyProperty.Register(
        nameof(Overlay),
        typeof(BoardOverlay),
        typeof(ChessBoardControl),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty LegalMovesProperty = DependencyProperty.Register(
        nameof(LegalMoves),
        typeof(IReadOnlyList<LegalMove>),
        typeof(ChessBoardControl),
        new FrameworkPropertyMetadata(Array.Empty<LegalMove>(), FrameworkPropertyMetadataOptions.AffectsRender));

    public string Position
    {
        get => (string)GetValue(PositionProperty);
        set => SetValue(PositionProperty, value);
    }

    public BoardSkin Skin
    {
        get => (BoardSkin)GetValue(SkinProperty);
        set => SetValue(SkinProperty, value);
    }

    public BoardOrientation Orientation
    {
        get => (BoardOrientation)GetValue(OrientationProperty);
        set => SetValue(OrientationProperty, value);
    }

    public bool ShowCoordinates
    {
        get => (bool)GetValue(ShowCoordinatesProperty);
        set => SetValue(ShowCoordinatesProperty, value);
    }

    public BoardOverlay? Overlay
    {
        get => (BoardOverlay?)GetValue(OverlayProperty);
        set => SetValue(OverlayProperty, value);
    }

    public IReadOnlyList<LegalMove> LegalMoves
    {
        get => (IReadOnlyList<LegalMove>)GetValue(LegalMovesProperty);
        set => SetValue(LegalMovesProperty, value);
    }

    public event EventHandler<BoardMoveRequestedEventArgs>? MoveRequested;
    public event BoardRenderCompletedEventHandler? RenderCompleted;
    public event EventHandler<FenErrorEventArgs>? PositionRejected;

    protected override void OnRender(DrawingContext drawingContext)
    {
        var renderStarted = Stopwatch.GetTimestamp();
        base.OnRender(drawingContext);
        drawingContext.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));

        var outer = BoardGeometry.Calculate(ActualWidth, ActualHeight);
        if (outer.Size <= 0)
        {
            return;
        }

        var border = Math.Max(2, outer.Size * BorderThicknessRatio);
        var geometry = new BoardGeometry(
            outer.Left + border,
            outer.Top + border,
            Math.Max(0, outer.Size - (border * 2)),
            Math.Max(0, outer.Size - (border * 2)) / 8d);
        drawingContext.DrawDrawing(GetBackground(outer, geometry));
        DrawSquareOverlays(drawingContext, geometry);
        DrawLegalTargets(drawingContext, geometry);
        DrawPieces(drawingContext, geometry);
        DrawVectorOverlays(drawingContext, geometry);

        RenderCompleted?.Invoke(this, Stopwatch.GetElapsedTime(renderStarted).TotalMilliseconds);
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        Focus();
        _pointer = e.GetPosition(this);
        var geometry = GetInnerGeometry();
        var square = geometry.HitTest(_pointer.X, _pointer.Y, Orientation);
        if (square is null)
        {
            return;
        }

        _interaction.PointerDown(square.Value, _parsedPosition[square.Value].HasValue, _pointer.X, _pointer.Y);
        CaptureMouse();
        e.Handled = true;
        InvalidateVisual();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!IsMouseCaptured || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        _pointer = e.GetPosition(this);
        if (_interaction.PointerMove(_pointer.X, _pointer.Y))
        {
            InvalidateVisual();
        }
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (!IsMouseCaptured)
        {
            return;
        }

        _pointer = e.GetPosition(this);
        var target = GetInnerGeometry().HitTest(_pointer.X, _pointer.Y, Orientation);
        var result = _interaction.PointerUp(target);
        ReleaseMouseCapture();
        if (result is { HasMove: true, From: { } from, To: { } to } && _parsedPosition[from] is { } piece)
        {
            if (PromotionPolicy.IsRequired(piece, to))
            {
                ShowPromotionMenu(from, to, result.WasDrag, char.IsUpper(piece));
            }
            else
            {
                MoveRequested?.Invoke(this, new BoardMoveRequestedEventArgs(from, to, result.WasDrag));
            }
        }

        e.Handled = true;
        InvalidateVisual();
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        if (_interaction.IsDragging)
        {
            _interaction.Cancel();
            InvalidateVisual();
        }
    }

    private static void OnPositionChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs eventArgs)
    {
        var board = (ChessBoardControl)dependencyObject;
        try
        {
            board._parsedPosition = FenPosition.Parse(eventArgs.NewValue as string);
        }
        catch (FormatException exception)
        {
            board.PositionRejected?.Invoke(board, new FenErrorEventArgs(exception.Message));
        }

        board._interaction.Cancel();
    }

    private BoardGeometry GetInnerGeometry()
    {
        var outer = BoardGeometry.Calculate(ActualWidth, ActualHeight);
        var border = Math.Max(2, outer.Size * BorderThicknessRatio);
        var size = Math.Max(0, outer.Size - (border * 2));
        return new BoardGeometry(outer.Left + border, outer.Top + border, size, size / 8d);
    }

    private void DrawSquares(DrawingContext context, BoardGeometry geometry)
    {
        var palette = GetSkinPalette(Skin);
        var murphyPen = Skin == BoardSkin.Murphy
            ? CreatePen(0x2AFFFFFF, Math.Max(1, geometry.SquareSize * 0.018))
            : null;
        for (var rank = 0; rank < 8; rank++)
        {
            for (var file = 0; file < 8; file++)
            {
                var square = new Square(file, rank);
                var (x, y) = geometry.TopLeft(square, Orientation);
                var rect = new Rect(x, y, geometry.SquareSize, geometry.SquareSize);
                var dark = (file + rank) % 2 == 0;
                context.DrawRectangle(dark ? palette.Dark : palette.Light, null, rect);
                if (murphyPen is not null && dark)
                {
                    DrawMurphyStripes(context, rect, geometry.SquareSize, murphyPen);
                }
            }
        }
    }

    private void DrawSquareOverlays(DrawingContext context, BoardGeometry geometry)
    {
        if (Overlay?.LastMoveFrom is { } lastFrom)
        {
            DrawSquareOverlay(context, geometry, lastFrom, LastMoveBrush);
        }

        if (Overlay?.LastMoveTo is { } lastTo)
        {
            DrawSquareOverlay(context, geometry, lastTo, LastMoveBrush);
        }

        foreach (var square in Overlay?.HighlightedSquares ?? [])
        {
            DrawSquareOverlay(context, geometry, square, HighlightBrush);
        }

        if (_interaction.SelectedSquare is { } selected)
        {
            DrawSquareOverlay(context, geometry, selected, SelectionBrush);
        }
    }

    private void DrawSquareOverlay(DrawingContext context, BoardGeometry geometry, Square square, Brush brush)
    {
        var (x, y) = geometry.TopLeft(square, Orientation);
        context.DrawRectangle(brush, null, new Rect(x, y, geometry.SquareSize, geometry.SquareSize));
    }

    private void DrawLegalTargets(DrawingContext context, BoardGeometry geometry)
    {
        var source = _interaction.DragSource ?? _interaction.SelectedSquare;
        if (source is null)
        {
            return;
        }

        Pen? capturePen = null;
        Span<bool> drawnTargets = stackalloc bool[BoardGeometry.SquareCount];
        foreach (var move in LegalMoves)
        {
            if (move.From != source.Value || drawnTargets[move.To.Index])
            {
                continue;
            }

            var target = move.To;
            drawnTargets[target.Index] = true;
            var center = Center(geometry, target);
            if (_parsedPosition[target] is null)
            {
                context.DrawEllipse(
                    LegalTargetBrush,
                    null,
                    center,
                    geometry.SquareSize * 0.115,
                    geometry.SquareSize * 0.115);
            }
            else
            {
                context.DrawEllipse(
                    null,
                    capturePen ??= CreatePen(0x73111827, geometry.SquareSize * 0.065),
                    center,
                    geometry.SquareSize * 0.405,
                    geometry.SquareSize * 0.405);
            }
        }
    }

    private void DrawPieces(DrawingContext context, BoardGeometry geometry)
    {
        foreach (var (square, piece) in _parsedPosition.Pieces())
        {
            if (_interaction.DragSource == square)
            {
                continue;
            }

            var (x, y) = geometry.TopLeft(square, Orientation);
            context.DrawImage(GetPiece(piece), new Rect(x, y, geometry.SquareSize, geometry.SquareSize));
        }

        if (_interaction.DragSource is { } source && _parsedPosition[source] is { } draggedPiece)
        {
            var size = geometry.SquareSize;
            context.DrawImage(GetPiece(draggedPiece), new Rect(_pointer.X - size / 2, _pointer.Y - size / 2, size, size));
        }
    }

    private void DrawVectorOverlays(DrawingContext context, BoardGeometry geometry)
    {
        foreach (var circle in Overlay?.Circles ?? [])
        {
            var center = Center(geometry, circle.Square);
            var pen = CreatePen(circle.Argb, geometry.SquareSize * circle.Thickness);
            context.DrawEllipse(null, pen, center, geometry.SquareSize * 0.37, geometry.SquareSize * 0.37);
        }

        foreach (var arrow in Overlay?.Arrows ?? [])
        {
            var start = Center(geometry, arrow.From);
            var end = Center(geometry, arrow.To);
            var pen = CreatePen(arrow.Argb, geometry.SquareSize * arrow.Thickness);
            context.DrawLine(pen, start, end);

            var direction = start - end;
            direction.Normalize();
            var perpendicular = new Vector(-direction.Y, direction.X);
            var length = geometry.SquareSize * 0.30;
            var width = geometry.SquareSize * 0.18;
            var head = new StreamGeometry();
            using (var geometryContext = head.Open())
            {
                geometryContext.BeginFigure(end, true, true);
                geometryContext.LineTo(end + direction * length + perpendicular * width, true, false);
                geometryContext.LineTo(end + direction * length - perpendicular * width, true, false);
            }

            head.Freeze();
            context.DrawGeometry(pen.Brush, null, head);
        }
    }

    private void DrawCoordinates(DrawingContext context, BoardGeometry geometry, double pixelsPerDip)
    {
        var palette = GetSkinPalette(Skin);
        var culture = CultureInfo.GetCultureInfo("en-US");
        var typeface = new Typeface("Segoe UI Semibold");
        var fontSize = Math.Clamp(geometry.SquareSize * 0.17, 8, 16);
        for (var visualFile = 0; visualFile < 8; visualFile++)
        {
            var file = Orientation == BoardOrientation.White ? visualFile : 7 - visualFile;
            var text = new FormattedText(((char)('a' + file)).ToString(), culture, FlowDirection.LeftToRight, typeface, fontSize, palette.Coordinate, pixelsPerDip);
            context.DrawText(text, new Point(geometry.Left + visualFile * geometry.SquareSize + 3, geometry.Top + geometry.Size - text.Height - 1));
        }

        for (var visualRank = 0; visualRank < 8; visualRank++)
        {
            var rank = Orientation == BoardOrientation.White ? 8 - visualRank : visualRank + 1;
            var text = new FormattedText(rank.ToString(CultureInfo.InvariantCulture), culture, FlowDirection.LeftToRight, typeface, fontSize, palette.Coordinate, pixelsPerDip);
            context.DrawText(text, new Point(geometry.Left + geometry.Size - text.Width - 3, geometry.Top + visualRank * geometry.SquareSize + 1));
        }
    }

    private Point Center(BoardGeometry geometry, Square square)
    {
        var (x, y) = geometry.TopLeft(square, Orientation);
        return new Point(x + geometry.SquareSize / 2, y + geometry.SquareSize / 2);
    }

    private static BitmapSource GetPiece(char piece)
    {
        lock (PieceCacheLock)
        {
            if (PieceCache.TryGetValue(piece, out var cached))
            {
                return cached;
            }

            var asset = PieceAssets[piece];
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.UriSource = new Uri($"pack://application:,,,/ChessMentor.Desktop;component/Assets/Pieces/{asset}", UriKind.Absolute);
            image.EndInit();
            image.Freeze();
            PieceCache[piece] = image;
            return image;
        }
    }

    private static Pen CreatePen(uint argb, double thickness)
    {
        var color = Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);
        var pen = new Pen(FrozenBrush(color), Math.Max(1, thickness))
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
            LineJoin = PenLineJoin.Round,
        };
        pen.Freeze();
        return pen;
    }

    private DrawingGroup GetBackground(BoardGeometry outer, BoardGeometry geometry)
    {
        var pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var key = new BackgroundCacheKey(ActualWidth, ActualHeight, Skin, Orientation, ShowCoordinates, pixelsPerDip);
        if (_backgroundCache is not null && _backgroundCacheKey == key)
        {
            return _backgroundCache;
        }

        var background = new DrawingGroup();
        using (var context = background.Open())
        {
            context.DrawRectangle(GetSkinPalette(Skin).Border, null, new Rect(outer.Left, outer.Top, outer.Size, outer.Size));
            DrawSquares(context, geometry);
            if (ShowCoordinates)
            {
                DrawCoordinates(context, geometry, pixelsPerDip);
            }
        }

        background.Freeze();
        _backgroundCache = background;
        _backgroundCacheKey = key;
        return background;
    }

    private void ShowPromotionMenu(Square from, Square to, bool wasDrag, bool white)
    {
        var menu = new ContextMenu
        {
            PlacementTarget = this,
            Placement = PlacementMode.MousePoint,
            FlowDirection = FlowDirection.LeftToRight,
        };
        foreach (var choice in PromotionPolicy.Choices)
        {
            var item = new MenuItem
            {
                Header = choice switch
                {
                    'q' => white ? "♕  Queen" : "♛  Queen",
                    'r' => white ? "♖  Rook" : "♜  Rook",
                    'b' => white ? "♗  Bishop" : "♝  Bishop",
                    _ => white ? "♘  Knight" : "♞  Knight",
                },
                Tag = choice,
            };
            item.Click += (_, _) =>
                MoveRequested?.Invoke(this, new BoardMoveRequestedEventArgs(from, to, wasDrag, choice));
            menu.Items.Add(item);
        }

        menu.IsOpen = true;
    }

    private static void DrawMurphyStripes(DrawingContext context, Rect rect, double squareSize, Pen pen)
    {
        var step = Math.Max(5, squareSize * 0.13);
        for (var offset = -rect.Height; offset < rect.Width; offset += step)
        {
            var start = new Point(rect.Left + Math.Max(0, offset), rect.Bottom - Math.Max(0, -offset));
            var end = new Point(rect.Left + Math.Min(rect.Width, offset + rect.Height), rect.Top + Math.Max(0, -offset));
            context.DrawLine(pen, start, end);
        }
    }

    private static SkinPalette GetSkinPalette(BoardSkin skin) => skin switch
    {
        BoardSkin.Rokhamooz => RokhamoozPalette,
        BoardSkin.Murphy => MurphyPalette,
        _ => ChessmentorPalette,
    };

    private static Brush FrozenBrush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private static Brush FrozenBrush(string color) => FrozenBrush((Color)ColorConverter.ConvertFromString(color)!);

    private sealed record SkinPalette(Brush Light, Brush Dark, Brush Border, Brush Coordinate)
    {
        public SkinPalette(string light, string dark, string border, string coordinate)
            : this(FrozenBrush(light), FrozenBrush(dark), FrozenBrush(border), FrozenBrush(coordinate))
        {
        }
    }

    private readonly record struct BackgroundCacheKey(
        double Width,
        double Height,
        BoardSkin Skin,
        BoardOrientation Orientation,
        bool ShowCoordinates,
        double PixelsPerDip);
}

public sealed class BoardMoveRequestedEventArgs(
    Square from,
    Square to,
    bool wasDrag,
    char? promotion = null) : EventArgs
{
    public Square From { get; } = from;
    public Square To { get; } = to;
    public bool WasDrag { get; } = wasDrag;
    public char? Promotion { get; } = promotion;
    public bool IsPromotion => Promotion.HasValue;
}

public delegate void BoardRenderCompletedEventHandler(object sender, double elapsedMilliseconds);

public sealed class FenErrorEventArgs(string message) : EventArgs
{
    public string Message { get; } = message;
}
