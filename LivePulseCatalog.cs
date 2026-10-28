using System;
using System.Linq;
using MZTools.Capture;

namespace MZTools;

// Projection only. No historical pulse numbers live in the portable capture module.
internal static class LivePulseCatalog
{
    internal static LivePulseReference From(AudioPulseReference reference)=>new(reference.CatalogId??"USER:"+reference.Name,reference.Name,
        reference.CatalogId==null?"Session-local user values":PulseReferenceCatalog.Version,reference.Source,reference.Evidence+" • "+reference.Derivation,reference.Kind.ToString(),
        Array.AsReadOnly(reference.Guides.Select(g=>new LivePulseGuide((LivePulseGroup)Array.IndexOf(AudioPulseReference.Groups,g.Group),g.Microseconds,g.Context)).ToArray()));
}
