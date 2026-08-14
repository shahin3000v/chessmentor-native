using ChessMentor.Chess;

namespace ChessMentor.Persistence;

public sealed record AppSettings(
    BoardSkin BoardSkin = ChessMentor.Chess.BoardSkin.Chessmentor,
    bool ShowCoordinates = true,
    bool HeaderCollapsed = false,
    bool GamesPanelCollapsed = false,
    string ViewerMoveDisplayMode = "All",
    string ViewerNotationMode = "Letters",
    bool MoveSoundEnabled = true,
    double GamesPanelWidth = 280,
    double MovesPanelWidth = 340,
    int CommentFontSize = 14,
    string CommentFontFamilyName = "IRANYekanWeb",
    string CustomCommentFontPath = "",
    string UiCulture = "fa-IR",
    int TranslationConcurrency = 3,
    string ServerBaseUrl = "https://chessfa.liara.run/",
    string LocalInstallationId = "",
    double StudioMovesPanelWidth = 390,
    double StudioGamesPanelWidth = 300,
    double? TextWindowLeft = null,
    double? TextWindowTop = null,
    double TextWindowWidth = 420,
    double TextWindowHeight = 300);
