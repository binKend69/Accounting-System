using System.Xml.Linq;

namespace HisabTak.Application.Tests;

public sealed class ArchitectureTests
{
    [Theory]
    [InlineData("Domain", new string[] { })]
    [InlineData("Application", new[] { "Domain" })]
    [InlineData("Infrastructure", new[] { "Application", "Domain" })]
    [InlineData("WPF", new[] { "Application", "Infrastructure" })]
    public void Project_dependencies_follow_the_architecture(string layer, string[] expected)
    {
        var root = RepositoryRoot();
        var project = XDocument.Load(Path.Combine(root, "src", $"HisabTak.{layer}", $"HisabTak.{layer}.csproj"));
        var actual = project.Descendants("ProjectReference")
            .Select(reference => Path.GetFileNameWithoutExtension(reference.Attribute("Include")!.Value)
                .Replace("HisabTak.", string.Empty, StringComparison.Ordinal))
            .Order(StringComparer.Ordinal).ToArray();

        Assert.Equal(expected.Order(StringComparer.Ordinal), actual);
    }

    [Fact]
    public void Application_has_no_runtime_dependency_on_persistence_or_presentation()
    {
        var references = typeof(ApplicationIdentity).Assembly.GetReferencedAssemblies();
        var forbidden = new[] { "HisabTak.Infrastructure", "HisabTak.WPF", "Microsoft.EntityFrameworkCore",
            "Microsoft.Data.SqlClient", "Dapper", "PresentationFramework", "WindowsBase" };
        Assert.DoesNotContain(references, reference => forbidden.Contains(reference.Name, StringComparer.Ordinal));
    }

    [Fact]
    public void ViewModels_do_not_import_infrastructure_or_database_apis()
    {
        var files = Directory.GetFiles(Path.Combine(RepositoryRoot(), "src", "HisabTak.WPF", "ViewModels"),
            "*.cs", SearchOption.AllDirectories);
        Assert.NotEmpty(files);
        foreach (var file in files)
        {
            var source = File.ReadAllText(file);
            foreach (var forbidden in new[] { "HisabTak.Infrastructure", "Microsoft.EntityFrameworkCore",
                "Microsoft.Data.SqlClient", "SqlConnection", "Dapper" })
            {
                Assert.DoesNotContain(forbidden, source, StringComparison.Ordinal);
            }
        }
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "HisabTak.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new DirectoryNotFoundException("HisabTak.slnx was not found.");
    }
}
