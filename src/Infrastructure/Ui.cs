using Spectre.Console;

namespace Zinc.Cli;

/// <summary>Console output in one voice: steps, the commands zinc runs for you, and results.</summary>
static class Ui
{
    static readonly Lock Gate = new();

    public static void Step(string text) => Write($"[bold blue]==>[/] [bold]{Markup.Escape(text)}[/]");
    public static void Command(string text) => Write($"[grey]$ {Markup.Escape(text)}[/]");
    public static void Output(string line) => Write($"[grey]  {Markup.Escape(line)}[/]");
    public static void Info(string text) => Write($"    {Markup.Escape(text)}");
    public static void Success(string text) => Write($"[green]✓[/] {Markup.Escape(text)}");
    public static void Warn(string text) => Write($"[yellow]![/] {Markup.Escape(text)}");
    public static void Error(string text) => Write($"[red]✗ {Markup.Escape(text)}[/]");
    public static void Hint(string text) => Write($"  [grey]{Markup.Escape(text)}[/]");
    public static void Blank() => Write("");

    /// <summary>A path as the user would type it from where they are: relative when it's below the working directory.</summary>
    public static string Path(string path)
    {
        var relative = System.IO.Path.GetRelativePath(Environment.CurrentDirectory, path);
        return relative.StartsWith("..") ? path : relative;
    }

    // Process output arrives on CliWrap's pipe threads.
    static void Write(string markup)
    {
        lock (Gate) AnsiConsole.MarkupLine(markup);
    }
}

/// <summary>A failure the user can act on. Shown without a stack trace, with the hint underneath.</summary>
sealed class ZincException(string message, string? hint = null) : Exception(message)
{
    public string? Hint { get; } = hint;
}
