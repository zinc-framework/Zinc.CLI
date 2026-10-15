using System.Reflection;
using Spectre.Console;
using Spectre.Console.Cli;
using Zinc.Cli;
using Zinc.Cli.Commands;

var app = new CommandApp();
app.Configure(config =>
{
    config.SetApplicationName("zinc");
    config.SetApplicationVersion(ToolInfo.Version);
    config.PropagateExceptions();

    config.AddCommand<NewCommand>("new")
        .WithExample("new", "mygame");

    config.AddBranch("package", package =>
    {
        package.SetDescription("Build a release package of the game.");
        package.AddCommand<PackageMacCommand>("mac")
            .WithExample("package", "mac")
            .WithExample("package", "mac", "--sign", "--notarize", "my-profile")
            .WithExample("package", "mac", "--", "-p:DefineConstants=DEMO");
        package.AddCommand<PackageWindowsCommand>("windows")
            .WithExample("package", "windows");
    });

    config.AddCommand<DoctorCommand>("doctor");
});

using var cancel = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    // Let CliWrap stop the running tool instead of the process dying mid-step.
    e.Cancel = true;
    cancel.Cancel();
};

try
{
    return await app.RunAsync(args, cancel.Token);
}
catch (ZincException e)
{
    Ui.Error(e.Message);
    if (e.Hint != null)
        foreach (var line in e.Hint.Split('\n')) Ui.Hint(line);
    return 1;
}
catch (OperationCanceledException)
{
    Ui.Warn("Cancelled.");
    return 130;
}
catch (CommandAppException e)
{
    if (e.Pretty != null) AnsiConsole.Write(e.Pretty);
    else Ui.Error(e.Message);
    return 1;
}
catch (Exception e)
{
    AnsiConsole.WriteException(e, ExceptionFormats.ShortenEverything);
    return 1;
}

namespace Zinc.Cli
{
    static class ToolInfo
    {
        /// <summary>The package version, without the +commit suffix the SDK appends.</summary>
        public static string Version { get; } =
            (typeof(ToolInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0")
            .Split('+')[0];
    }
}
