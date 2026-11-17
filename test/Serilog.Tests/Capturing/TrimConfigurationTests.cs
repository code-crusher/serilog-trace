using System.Diagnostics;
using System.Runtime.CompilerServices;
using File = System.IO.File;

namespace Serilog.Tests.Capturing;

public class TrimConfigurationTests
{
    const string SwitchName = "Serilog.Capturing.IsStructureValueSupported";

    [Theory]
    // Untrimmed builds keep destructuring enabled; the switch is left unset so that the runtime default applies.
    [InlineData(false, null, null)]
    // Trimmed builds disable structure-value destructuring, because the trimmer cannot preserve the metadata
    // of the arbitrary types that are destructured; see https://github.com/serilog/serilog/issues/2235.
    [InlineData(true, null, "false")]
    [InlineData(true, "false", "false")]
    // Applications that do preserve the metadata they destructure can opt back in.
    [InlineData(true, "true", "true")]
    public void StructureValueSupportIsConfiguredForTrimming(bool publishTrimmed, string? optIn, string? expectedValue)
    {
        var value = EvaluateSwitch(publishTrimmed, optIn, setTrimmedAfterImport: false);
        Assert.Equal(expectedValue, value);
    }

    [Fact]
    public void PublishTrimmedIsHonouredWhenSetAfterTheTargetsAreImported()
    {
        // `PublishAot` implies `PublishTrimmed`, and SDK targets imported after Serilog's own targets assign
        // it. Item conditions are evaluated after all properties, so the switch must still be emitted.
        var value = EvaluateSwitch(publishTrimmed: false, optIn: null, setTrimmedAfterImport: true);
        Assert.Equal("false", value);
    }

    /// <summary>
    /// Evaluates <c>Serilog.targets</c> with MSBuild and returns the value assigned to the structure-value
    /// feature switch, or <c>null</c> when the switch is not configured.
    /// </summary>
    static string? EvaluateSwitch(bool publishTrimmed, string? optIn, bool setTrimmedAfterImport)
    {
        var solutionDirectory = GetSolutionDirectory();
        var targetsPath = Path.Combine(solutionDirectory, "src", "Serilog", "Serilog.targets");
        Assert.True(File.Exists(targetsPath), $"Serilog targets file not found at {targetsPath}.");

        var workingDirectory = Path.Combine(Path.GetTempPath(), "serilog-trim-config-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(workingDirectory);

        try
        {
            // Stands in for SDK targets that assign `PublishTrimmed` after `Serilog.targets` has been imported.
            var latePropsPath = Path.Combine(workingDirectory, "late.props");
            File.WriteAllText(latePropsPath, "<Project><PropertyGroup><PublishTrimmed>true</PublishTrimmed></PropertyGroup></Project>");

            var outputPath = Path.Combine(workingDirectory, "switch.txt");
            var projectPath = Path.Combine(workingDirectory, "evaluate.proj");
            File.WriteAllText(projectPath, string.Join(Environment.NewLine,
                "<Project>",
                $"  <Import Project=\"{targetsPath}\" />",
                $"  <Import Project=\"{latePropsPath}\" Condition=\"'$(SetTrimmedAfterImport)' == 'true'\" />",
                "  <Target Name=\"Emit\">",
                "    <WriteLinesToFile File=\"$(SwitchOutputPath)\" Overwrite=\"true\"",
                $"      Lines=\"@(RuntimeHostConfigurationOption->WithMetadataValue('Identity', '{SwitchName}')->'%(Value)')\" />",
                "  </Target>",
                "</Project>"));

            var arguments = new StringBuilder();
            arguments.Append("msbuild \"").Append(projectPath).Append("\" -nologo -v:q -t:Emit");
            arguments.Append(" -p:SwitchOutputPath=\"").Append(outputPath).Append('"');
            arguments.Append(" -p:SetTrimmedAfterImport=").Append(setTrimmedAfterImport ? "true" : "false");

            if (publishTrimmed)
                arguments.Append(" -p:PublishTrimmed=true");

            if (optIn != null)
                arguments.Append(" -p:SerilogIsStructureValueSupported=").Append(optIn);

            var startInfo = new ProcessStartInfo(GetDotNetPath(), arguments.ToString())
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };

            using var process = Process.Start(startInfo)!;

            // Read both streams concurrently, so that a full buffer on either cannot deadlock the child.
            var standardOutput = process.StandardOutput.ReadToEndAsync();
            var standardError = process.StandardError.ReadToEndAsync();

            if (!process.WaitForExit(120_000))
            {
                process.Kill();
                throw new TimeoutException("MSBuild evaluation timed out.");
            }

            Assert.True(process.ExitCode == 0,
                $"MSBuild evaluation failed ({process.ExitCode}):{Environment.NewLine}{standardOutput.Result}{standardError.Result}");

            var value = File.ReadAllText(outputPath).Trim();
            return value.Length == 0 ? null : value;
        }
        finally
        {
            try
            {
                Directory.Delete(workingDirectory, recursive: true);
            }
            catch (IOException)
            {
                // Best-effort cleanup of the temporary evaluation project.
            }
        }
    }

    static string GetDotNetPath() =>
        Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } path ? path : "dotnet";

    static string GetSolutionDirectory([CallerFilePath] string file = "")
    {
        var currentDirectory = new FileInfo(file).Directory!.FullName;
        do
        {
            if (Directory.GetFiles(currentDirectory, "*.sln").Any())
                return currentDirectory;

            var parent = Directory.GetParent(currentDirectory);
            if (parent is null)
                break;

            currentDirectory = parent.FullName;
        } while (true);

        throw new InvalidOperationException("Could not locate the solution directory.");
    }
}
