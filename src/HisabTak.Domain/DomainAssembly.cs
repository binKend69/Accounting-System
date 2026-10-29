using System.Reflection;

namespace HisabTak.Domain;

/// <summary>Identifies the domain module for dependency verification and discovery.</summary>
public static class DomainAssembly
{
    public static Assembly Reference => typeof(DomainAssembly).Assembly;
}
