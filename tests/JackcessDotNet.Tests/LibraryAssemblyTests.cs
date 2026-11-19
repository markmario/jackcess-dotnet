using System.Reflection;
using System.Runtime.Versioning;
using Xunit;

namespace JackcessDotNet.Tests;

/// <summary>
/// The library has an assembly for each of its target frameworks, and the tests run against each: the one for the
/// tests' own framework, or, built with <c>-p:NetStandardAsset=true</c>, the netstandard2.0 one, which is what an
/// application on .NET 8 or 9, or on .NET Framework 4.7.2 or earlier, loads. A run against the wrong assembly
/// passes every other test and tests nothing it was for, so this one says which assembly is loaded.
/// </summary>
public sealed class LibraryAssemblyTests
{
    private static string TargetOf(Assembly assembly)
        => assembly.GetCustomAttribute<TargetFrameworkAttribute>()!.FrameworkName;

    [Fact]
    public void The_tests_run_against_the_library_assembly_their_build_is_for()
    {
        string library = TargetOf(typeof(Database).Assembly);
#if NETSTANDARD_ASSET
        Assert.Equal(".NETStandard,Version=v2.0", library);
#else
        Assert.Equal(TargetOf(typeof(LibraryAssemblyTests).Assembly), library);
#endif
    }

#if NETSTANDARD_ASSET
    /// <summary>
    /// The netstandard2.0 assembly has the legacy code pages from a package, where .NET Framework has them itself
    /// and .NET 10 has the provider in the framework: without it no database opens on .NET.
    /// </summary>
    [Fact]
    public void The_netstandard_assembly_has_the_code_page_of_an_Access_97_file()
    {
        Assert.Equal(1252, JetFormat.Jet3.TextEncoding.CodePage);
    }
#endif
}
