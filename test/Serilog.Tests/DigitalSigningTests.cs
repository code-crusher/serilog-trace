using System.Runtime.CompilerServices;
using File = System.IO.File;

namespace Serilog.Tests;

public class DigitalSigningTests
{
    [Fact]
    public void ReadmeDocumentsThePublicKeyTokenRetainedByReSigning()
    {
        // Re-signing an assembly with Authenticode leaves its strong name intact, so the
        // README tells consumers which public key token their signed copies will keep.
        // That documented value must match the assemblies actually built and shipped.
        var token = typeof(Log).Assembly.GetName().GetPublicKeyToken();
        Assert.NotNull(token);

        var readme = ReadReadme();
        Assert.Contains(string.Concat(token.Select(b => b.ToString("x2"))), readme);
    }

    [Fact]
    public void ReadmeDocumentsPermissionToSignAndRedistribute()
    {
        var readme = ReadReadme();
        Assert.Contains("Digitally signing Serilog assemblies", readme);
        Assert.Contains("may sign these assemblies with your own certificate and redistribute them", readme);
        Assert.Contains("https://apache.org/licenses/LICENSE-2.0.html", readme);
        Assert.Contains("https://github.com/serilog/serilog/issues/2225", readme);
    }

    static string ReadReadme([CallerFilePath] string file = "")
    {
        var solutionDirectory = GetSolutionDirectory(file);
        return File.ReadAllText(Path.Combine(solutionDirectory, "README.md"));
    }

    static string GetSolutionDirectory(string file)
    {
        var currentDirectory = new FileInfo(file).Directory!.FullName;
        do
        {
            if (Directory.GetFiles(currentDirectory, "*.sln").Any())
            {
                return currentDirectory;
            }

            var parent = Directory.GetParent(currentDirectory);
            if (parent is null)
            {
                break;
            }

            currentDirectory = parent.FullName;
        } while (true);

        throw new();
    }
}
