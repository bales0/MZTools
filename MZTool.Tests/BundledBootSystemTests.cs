namespace MZTools.Tests;

public sealed class BundledBootSystemTests
{
    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)]
    public void IncludedSystemsInstallWithoutExternalFilesAndPreserveTargetContents(int index)
    {
        var item = BundledBootSystems.All[index];
        var source = item.Open(); var before = source.Serialize();
        var profile = CpmSystemProfileRegistry.ResolveVerifiedSource(source);
        var target = profile.Layout == CpmLayoutId.PersonalCpm80 ? DskDocumentFactory.CreatePersonalCpm80(false)
            : DskDocumentFactory.CreateCpm(profile.Layout == CpmLayoutId.LecHd1440);
        target.FileSystem.Insert("KEEP.COM", [0xC9, 1, 2, 3], user: 7); target.MarkModified();
        var keep = target.FileSystem.Extract(target.FileSystem.ReadDirectory().Single());
        var report = CpmSystemBuilder.Apply(target, source, profile);
        Assert.Equal(keep, target.FileSystem.Extract(target.FileSystem.ReadDirectory().Single(e => e.Name == "KEEP")));
        Assert.Equal(profile.Id, CpmSystemProfileRegistry.ResolveVerifiedSource(target).Id);
        Assert.Equal(0, DskAnalyzer.Analyze(target).Errors);
        Assert.Equal(before, source.Serialize());
        Assert.True(target.IsModified); Assert.NotEmpty(report.ChangedRanges);
        Assert.Equal(profile.Storage == CpmSystemStorageKind.BootTrackPlusSystemFile ? 2 : 1, target.FileSystem.ReadDirectory().Count);
        Assert.NotNull(BundledBootSystems.Recommended(target));
    }

    [Fact]
    public void StandaloneIplSourceAndMismatchedLayoutsAreNotInstalled()
    {
        var target = DskDocumentFactory.CreateCpm(false); byte[] before = target.Serialize();
        var standalone = BundledBootSystems.All[6].Open();
        Assert.IsNotType<CpmFileSystem>(standalone.FileSystem);
        Assert.False(DskCapabilityService.CanInstallBootSystem(target, standalone).IsCompatible);
        var hd = BundledBootSystems.All[2].Open();
        Assert.False(CpmSystemBuilder.Preflight(target, hd, CpmSystemProfileRegistry.ResolveVerifiedSource(hd)).CanBuild);
        Assert.Equal(before, target.Serialize());
    }
}
