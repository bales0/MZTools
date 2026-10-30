namespace MZTools.Tests;

public sealed class SaveAsDestinationTests
{
    [Theory]
    [InlineData(1)] [InlineData(2)] [InlineData(3)]
    public void PhysicalSaveAsThenFormatAndSaveKeepsOriginal(int version)
    {
        string directory=TapeTestData.CreateTempDirectory();
        try {
            string original=Path.Combine(directory,"original.hfe"),copy=Path.Combine(directory,"copy.hfe");
            var raw=DskDocumentFactory.CreateRaw(4,2,9,512,1,0x4E,0xE5,"raw");
            byte[] bytes=HfeImage.FromDsk(raw).Serialize((HfeVersion)version);File.WriteAllBytes(original,bytes);
            var session=new HfeDocumentSession(HfeImage.Parse(bytes),original);
            session.SaveAs(copy);
            Assert.Equal(copy,session.FilePath);Assert.Equal(bytes,File.ReadAllBytes(copy));
            var replacement=DskDocumentFactory.CreatePersonalCpm80(false);
            var formattedHfe=HfeImage.Parse(HfeImage.FromDsk(replacement,version!=3).Serialize(session.Image.Version));
            var document=DskDocument.FromFormattedHfe(replacement,formattedHfe,session.FilePath);
            document.Save();
            Assert.Equal(bytes,File.ReadAllBytes(original));Assert.NotEqual(bytes,File.ReadAllBytes(copy));
            var reopened=DskDocument.Open(copy);Assert.IsType<CpmFileSystem>(reopened.FileSystem);
            Assert.Equal((HfeVersion)version,reopened.SourceHfe!.Version);
        } finally {Directory.Delete(directory,true);}
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void DiskSaveAsThenEditAndSaveUsesNewDestination(bool hfe)
    {
        string directory=TapeTestData.CreateTempDirectory();
        try {
            string extension=hfe?".hfe":".dsk";
            string original=Path.Combine(directory,"original"+extension),copy=Path.Combine(directory,"copy"+extension);
            var initial=DskDocumentFactory.CreateFsmz(tracks:4);
            byte[] bytes=hfe?HfeImage.FromDsk(initial).Serialize(HfeVersion.V3):initial.Serialize();
            File.WriteAllBytes(original,bytes);var document=DskDocument.Open(original);document.Save(copy);
            document.FileSystem.Insert("NEW",new byte[] {1,2,3});document.MarkModified();document.Save();
            Assert.Equal(copy,document.FilePath);Assert.Equal(bytes,File.ReadAllBytes(original));
            Assert.Single(DskDocument.Open(copy).FileSystem.ReadDirectory());
            string active=document.FilePath!;
            document.MarkModified();
            Assert.ThrowsAny<IOException>(()=>document.Save(Path.Combine(directory,"missing","bad"+extension)));
            Assert.Equal(active,document.FilePath);Assert.True(document.IsModified);
        } finally {Directory.Delete(directory,true);}
    }

    [Fact]
    public void FailedPhysicalSaveAsKeepsActiveImageAndPath()
    {
        string directory=TapeTestData.CreateTempDirectory();
        try {
            var image=HfeImage.FromDsk(DskDocumentFactory.CreateFsmz(tracks:4));
            var session=new HfeDocumentSession(image,Path.Combine(directory,"original.hfe"));
            string original=session.FilePath!;
            Assert.Throws<InvalidDataException>(()=>session.SaveAs(Path.Combine(directory,"wrong.dsk")));
            Assert.ThrowsAny<IOException>(()=>session.SaveAs(Path.Combine(directory,"missing","copy.hfe")));
            Assert.Same(image,session.Image);Assert.Equal(original,session.FilePath);
        } finally {Directory.Delete(directory,true);}
    }

    [Fact]
    public void TapeSaveAsOffersOnlyFormatsThatCanBecomeTheActiveDocument()
    {
        string[] filter=FeatureModePolicy.GetSaveFilter().Split('|');
        string[] extensions=filter.Where((_,index)=>index%2==1).ToArray();
        Assert.DoesNotContain("*.wav",extensions);Assert.DoesNotContain("*.lep",extensions);
        Assert.DoesNotContain("*.l16",extensions);Assert.DoesNotContain("*.dsk",extensions);
        foreach(var format in new[] {TapeDocumentFormat.Qdf,TapeDocumentFormat.Mzq,TapeDocumentFormat.QdHxc,TapeDocumentFormat.QdFlashFloppy,TapeDocumentFormat.QdSharpLegacy,TapeDocumentFormat.Mzt,TapeDocumentFormat.Mzf,TapeDocumentFormat.M12}) {
            string extension=extensions[MainWindow.GetSaveFilterIndex(format)-1][1..];
            Assert.Equal(format,MainWindow.ResolveSaveFormat(extension,MainWindow.GetSaveFilterIndex(format)));
        }
        Assert.Contains("*.wav",FeatureModePolicy.GetExportFilter());Assert.Contains("*.dsk",FeatureModePolicy.GetExportFilter());
    }

    [Fact]
    public void CleanIplDocumentKeepsSaveAsButDisablesSave()
    {
        Assert.Equal((false,true),DskEditorControl.GetIplSaveAvailability(false,true,true,2,false,true,modified:false));
        Assert.Equal((true,true),DskEditorControl.GetIplSaveAvailability(false,true,true,2,false,true,modified:true));
        Assert.Equal((false,false),DskEditorControl.GetIplSaveAvailability(true,true,true,2,false,true,modified:true));
    }
}
