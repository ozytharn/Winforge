using System.Xml.Linq;
using WinForge.ActionBroker.V2.Requests;

namespace WinForge.UnitTests.ActionBrokerV2;

public sealed class ActionBrokerV2ArchitectureTests
{
    [Fact]
    public void V2_library_has_no_project_or_package_dependencies_and_targets_non_windows_dotnet()
    {
        var root = FindRepositoryRoot();
        var projectPath = Path.Combine(root, "src", "WinForge.ActionBroker.V2", "WinForge.ActionBroker.V2.csproj");
        var project = XDocument.Load(projectPath);
        var references = project.Descendants()
            .Where(static element => element.Name.LocalName is "ProjectReference" or "PackageReference")
            .ToArray();
        var target = project.Descendants().Single(element => element.Name.LocalName == "TargetFramework").Value;

        Assert.Empty(references);
        Assert.Equal("net10.0", target);
    }

    [Fact]
    public void V2_assembly_does_not_reference_WinForge_or_Windows_hosting_APIs()
    {
        var forbiddenReferencePrefixes = new[]
        {
            "WinForge.",
            "System.IO.Pipes",
            "System.ServiceProcess",
            "Microsoft.Win32",
            "Windows."
        };
        var references = typeof(ActionRequest).Assembly.GetReferencedAssemblies()
            .Select(static assembly => assembly.Name ?? string.Empty)
            .ToArray();

        Assert.DoesNotContain(references, reference =>
            forbiddenReferencePrefixes.Any(prefix => reference.StartsWith(prefix, StringComparison.Ordinal)));
    }

    [Fact]
    public void V2_source_contains_no_service_IPC_elevation_or_real_executor_APIs()
    {
        var root = FindRepositoryRoot();
        var sourceRoot = Path.Combine(root, "src", "WinForge.ActionBroker.V2");
        var source = string.Join(
            Environment.NewLine,
            Directory.EnumerateFiles(sourceRoot, "*.cs", SearchOption.AllDirectories)
                .Select(File.ReadAllText));
        var prohibited = new[]
        {
            "System.IO.Pipes",
            "System.ServiceProcess",
            "WindowsIdentity",
            "Process.Start(",
            "RegistryKey",
            "RunAs"
        };

        Assert.DoesNotContain(prohibited, token => source.Contains(token, StringComparison.Ordinal));
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
            directory is not null;
            directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "WinForge.sln")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException("Could not locate WinForge.sln for architecture checks.");
    }
}
