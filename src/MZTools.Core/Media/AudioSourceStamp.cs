using System;
using System.IO;

namespace MZTools;

// Fast preview guard only. Creation time helps detect ordinary replacement; these
// metadata fields are deliberately not described as a content hash or stable file ID.
internal readonly record struct AudioSourceStamp(long Length,DateTime ModifiedUtc,DateTime CreatedUtc)
{
    internal static AudioSourceStamp Capture(string path)
    {
        var file=new FileInfo(path);if(!file.Exists)throw new FileNotFoundException("Audio source is missing.",path);
        return new(file.Length,file.LastWriteTimeUtc,file.CreationTimeUtc);
    }
    internal void Verify(string path)
    {if(Capture(path)!=this)throw new IOException("An audio source or segment changed since analysis. Reopen it: "+path);}
}
