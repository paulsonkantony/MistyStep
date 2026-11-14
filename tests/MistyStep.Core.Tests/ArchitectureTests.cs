using MistyStep.Core.Models;

namespace MistyStep.Core.Tests;

public class ArchitectureTests
{
    [Fact]
    public void Core_references_no_windows_specific_assemblies()
    {
        string[] forbidden = ["System.Windows", "Microsoft.Win32", "System.Drawing", "Microsoft.WindowsDesktop", "Microsoft.AspNetCore"];
        var references = typeof(ClipItem).Assembly.GetReferencedAssemblies().Select(a => a.Name ?? "").ToList();

        foreach (var name in references)
        {
            Assert.DoesNotContain(forbidden, prefix => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public void ClipItem_ToString_never_prints_content()
    {
        var item = ClipItem.CreateText("super secret sentinel", 0);
        Assert.DoesNotContain("sentinel", item.ToString(), StringComparison.Ordinal);
    }
}
