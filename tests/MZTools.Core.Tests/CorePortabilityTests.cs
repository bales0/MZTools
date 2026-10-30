using System.Reflection;
namespace MZTools.Tests;

public sealed class CorePortabilityTests
{
    [Fact]
    public void CoreAndCaptureHaveNoFrontendOrNativeAdapterAssemblyReferences()
    {
        foreach (var assembly in new[] { typeof(DskDocument).Assembly, typeof(MZTools.Capture.CaptureSession).Assembly })
        {
            var names = assembly.GetReferencedAssemblies().Select(r => r.Name!).ToArray();
            Assert.DoesNotContain(names, n => n == "MZTools" || n.Contains("Windows", StringComparison.OrdinalIgnoreCase) ||
                n.StartsWith("Presentation", StringComparison.Ordinal) || n.StartsWith("Avalonia", StringComparison.Ordinal) ||
                n.StartsWith("NAudio", StringComparison.Ordinal));
        }
        Assert.Equal(".NETCoreApp,Version=v10.0", typeof(DskDocument).Assembly
            .GetCustomAttribute<System.Runtime.Versioning.TargetFrameworkAttribute>()!.FrameworkName);
    }

    [Fact]
    public void BootResourcesKeepTheirOriginalLogicalNamesInCore()
    {
        var names = typeof(BundledBootSystems).Assembly.GetManifestResourceNames();
        Assert.Equal(7, names.Length);
        Assert.All(names, name => Assert.StartsWith("MZTools.Boot.", name));
        Assert.Contains("MZTools.Boot.P-CPM80.dsk", names);
        Assert.NotEqual("MZTools", typeof(BundledBootSystems).Assembly.GetName().Name);
    }

    [Fact]
    public void FileBackedRepairCannotBypassMissingPlatformProtection()
    {
        var source = DskImage.CreateUniform(3, 1, 2, 512, 1, 0x4E, 0xE5, "source").Serialize();
        source[0x30] = 2;
        var before = (byte[])source.Clone();
        var inspection = DskOpenCoordinator.Inspect(source, "unconfigured-source.dsk");
        var plan = Assert.Single(inspection.Plans);
        Assert.Throws<PlatformNotSupportedException>(() => inspection.Apply(plan, source));
        Assert.Equal(before, source);
    }

    [Fact]
    public void MemoryOnlyRepairStillRequiresExplicitSaveAs()
    {
        var source = DskImage.CreateUniform(3, 1, 2, 512, 1, 0x4E, 0xE5, "source").Serialize();
        source[0x30] = 2;
        var inspection = DskOpenCoordinator.Inspect(source);
        var repaired = inspection.Apply(Assert.Single(inspection.Plans), source);
        Assert.True(repaired.IsModified);
        Assert.True(repaired.RequiresRepairSaveAs);
        Assert.Throws<InvalidOperationException>(() => repaired.Save());
        Assert.Equal(2, source[0x30]);
        Assert.Equal(3, DskImage.Parse(repaired.Serialize()).TrackCount);
    }
}
