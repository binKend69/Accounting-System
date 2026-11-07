using System.Xml.Linq;
using HisabTak.Domain;

namespace HisabTak.Domain.Tests;

public sealed class DomainBoundaryTests
{
    [Fact]
    public void Domain_assembly_references_only_framework_assemblies()
    {
        var references = DomainAssembly.Reference.GetReferencedAssemblies();
        Assert.NotEmpty(references);
        Assert.All(references, reference => Assert.StartsWith("System.", reference.Name));
    }

    [Fact]
    public void Domain_project_has_no_project_or_package_dependencies()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "HisabTak.slnx")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        var project = XDocument.Load(Path.Combine(directory.FullName,
            "src", "HisabTak.Domain", "HisabTak.Domain.csproj"));
        Assert.Empty(project.Descendants("ProjectReference"));
        Assert.Empty(project.Descendants("PackageReference"));
    }
}
