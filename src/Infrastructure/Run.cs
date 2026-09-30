using System.ComponentModel;
using System.Text;
using CliWrap;
using Spectre.Console;

namespace Zinc.Cli;

sealed record RunResult(int ExitCode, string StdOut, string StdErr, string Combined)
{
    public bool Success => ExitCode == 0;
}

/// <summary>
/// Runs the external tools zinc wraps (dotnet, git, codesign, notarytool...).
/// </summary>
static class Run
{
    /// <summary>Stream tool output as it arrives instead of hiding it behind a spinner.</summary>
    public static bool Verbose { get; set; }

    /// <summary>
    /// Runs a step of the user's build. The command line is printed first so nothing zinc does is
    /// hidden and any step can be rerun by hand. Output is captured behind a spinner (streamed with
    /// --verbose) and printed if the command fails.
    /// </summary>
    public static async Task<RunResult> Tool(string exe, IEnumerable<string> args, string status, CancellationToken ct,
        string? workingDirectory = null, bool allowFailure = false)
    {
        var argList = args.ToList();
        Ui.Command(Display(exe, argList));

        var result = Verbose
            ? await Execute(exe, argList, workingDirectory, stream: true, ct)
            : await AnsiConsole.Status().StartAsync(status, _ => Execute(exe, argList, workingDirectory, stream: false, ct));

        if (!result.Success && !allowFailure)
        {
            if (!Verbose) PrintTail(result.Combined);
            throw new ZincException($"{exe} {argList.FirstOrDefault()} failed (exit code {result.ExitCode}).",
                Verbose ? null : "Rerun with --verbose to see all of its output.");
        }
        return result;
    }

    /// <summary>Runs a command to read something (a version, a setting). Not printed; never throws for a failing exit code.</summary>
    public static async Task<RunResult> Query(string exe, IEnumerable<string> args, CancellationToken ct, string? workingDirectory = null)
    {
        try
        {
            return await Execute(exe, args.ToList(), workingDirectory, stream: false, ct);
        }
        catch (ZincException)
        {
            return new RunResult(-1, "", "", "");
        }
    }

    static async Task<RunResult> Execute(string exe, List<string> args, string? workingDirectory, bool stream, CancellationToken ct)
    {
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        var combined = new StringBuilder();
        var gate = new Lock();

        PipeTarget Capture(StringBuilder own) => PipeTarget.ToDelegate(line =>
        {
            lock (gate)
            {
                own.AppendLine(line);
                combined.AppendLine(line);
            }
            if (stream) Ui.Output(line);
        });

        var command = CliWrap.Cli.Wrap(exe)
            .WithArguments(args)
            .WithValidation(CommandResultValidation.None)
            .WithStandardOutputPipe(Capture(stdout))
            .WithStandardErrorPipe(Capture(stderr));
        if (workingDirectory != null) command = command.WithWorkingDirectory(workingDirectory);

        try
        {
            var result = await command.ExecuteAsync(ct);
            return new RunResult(result.ExitCode, stdout.ToString(), stderr.ToString(), combined.ToString());
        }
        catch (Win32Exception)
        {
            throw new ZincException($"Couldn't run '{exe}': it isn't installed or isn't on PATH.", "Run 'zinc doctor' to check what's missing.");
        }
    }

    static void PrintTail(string output)
    {
        var lines = output.TrimEnd().Split('\n');
        const int max = 40;
        if (lines.Length > max) Ui.Output($"... ({lines.Length - max} earlier lines hidden)");
        foreach (var line in lines.TakeLast(max)) Ui.Output(line.TrimEnd('\r'));
    }

    // Paths are shown relative to the working directory, which keeps the line short and still runnable from here.
    static string Display(string exe, IEnumerable<string> args) =>
        string.Join(' ', args.Prepend(exe).Select(a => Path.IsPathFullyQualified(a) ? Ui.Path(a) : a).Select(Quote));

    public static string Quote(string arg) =>
        arg.Length == 0 || arg.Any(c => char.IsWhiteSpace(c) || c is '"' or '\'') ? $"\"{arg.Replace("\"", "\\\"")}\"" : arg;
}
