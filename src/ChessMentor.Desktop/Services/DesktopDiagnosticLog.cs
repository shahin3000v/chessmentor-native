using System.IO;
using System.Text;

namespace ChessMentor.Desktop.Services;

public static class DesktopDiagnosticLog
{
    private static readonly object Gate = new();

    public static string FilePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ChessMentor",
        "Logs",
        "desktop.log");

    public static void Write(string area, Exception exception)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(area);
        ArgumentNullException.ThrowIfNull(exception);
        try
        {
            var builder = new StringBuilder()
                .AppendLine("------------------------------------------------------------")
                .Append(DateTimeOffset.Now.ToString("O"))
                .Append("  ")
                .AppendLine(area);
            AppendException(builder, exception, 0);

            lock (Gate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                File.AppendAllText(FilePath, builder.ToString(), new UTF8Encoding(false));
            }
        }
        catch
        {
            // Diagnostics must never become a second application failure.
        }
    }

    private static void AppendException(StringBuilder builder, Exception exception, int depth)
    {
        builder.Append(' ', depth * 2)
            .Append(exception.GetType().FullName)
            .Append(": ")
            .AppendLine(exception.Message)
            .AppendLine(exception.StackTrace ?? "<no stack trace>");
        if (exception.InnerException is not null)
        {
            builder.AppendLine("Inner exception:");
            AppendException(builder, exception.InnerException, depth + 1);
        }
    }
}
