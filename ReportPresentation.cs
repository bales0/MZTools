using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace MZTools;

internal sealed record ReportTable(string Title, IReadOnlyList<string> Columns, IReadOnlyList<string[]> Rows);
internal sealed record ReportPresentation(string Heading, string Summary, string ExportText, IReadOnlyList<ReportTable> Tables)
{
    // Compatibility adapter for existing backend reports. No operation or validation is run here.
    // Every nonempty line is retained, including unrecognized diagnostics, in a named table.
    internal static ReportPresentation FromText(string? text)
    {
        text ??= "";
        var properties=new List<string[]>(); var messages=new List<string[]>();
        var ranges=new List<string[]>(); var bytes=new List<string[]>(); var sections=new Dictionary<string,List<string[]>>();
        string section="", heading="Report";
        var lines=text.Replace("\r","").Split('\n').Select(s=>s.Trim()).Where(s=>s.Length>0).ToArray();
        if(lines.Length>0) heading=lines[0].Length<=130?lines[0]:"Operation details";
        foreach(string line in lines) {
            var changed=Regex.Match(line,@"^0x([0-9a-f]+):\s*([0-9a-f]{2})\s*[→]\s*([0-9a-f]{2})$",RegexOptions.IgnoreCase);
            if(changed.Success) { bytes.Add(new[] {"0x"+changed.Groups[1].Value,changed.Groups[2].Value,changed.Groups[3].Value}); continue; }
            var range=Regex.Match(line,@"^0x([0-9a-f]+)\.\.0x([0-9a-f]+)\s*\((\d+) B\)$",RegexOptions.IgnoreCase);
            if(range.Success) { ranges.Add(new[] {"0x"+range.Groups[1].Value,"0x"+range.Groups[2].Value,range.Groups[3].Value}); continue; }
            if(Regex.IsMatch(line,@"^(Before SHA-256|After SHA-256|Source SHA-256|Changed byte ranges):")) section="";
            if(line.EndsWith(':') && line.Length<140) { section=line[..^1]; continue; }
            string category=Regex.IsMatch(section+" "+line,@"\b(errors?|failed|failures?|rejected|refused|unsupported|invalid)\b",RegexOptions.IgnoreCase)?"Attention":
                Regex.IsMatch(section+" "+line,@"\b(warnings?|loss|losses|lost|caution|uncertain)\b",RegexOptions.IgnoreCase)?"Warning":"Information";
            if(section.Length>0) {
                if(!sections.TryGetValue(section,out var rows)) sections[section]=rows=new();
                rows.Add(new[] { category,line }); continue;
            }
            int colon=line.IndexOf(':');
            if(colon>0 && colon<100 && !line[..colon].Contains("->") && !line[..colon].Contains("http"))
                properties.Add(new[] {line[..colon].Trim(),line[(colon+1)..].Trim()});
            else messages.Add(new[] {category,line});
        }
        var tables=new List<ReportTable>();
        if(properties.Count>0) tables.Add(new("Properties",new[] {"Property","Value"},properties));
        if(messages.Count>0) tables.Add(new("Messages",new[] {"Category","Details"},messages));
        foreach(var pair in sections) tables.Add(new(pair.Key,new[] {"Category","Details"},pair.Value));
        if(ranges.Count>0) tables.Add(new("Changed ranges",new[] {"Start (inclusive)","End (inclusive)","Length (B)"},ranges));
        if(bytes.Count>0) tables.Add(new("Byte changes",new[] {"Buffer offset","Before (hex)","After (hex)"},bytes));
        if(tables.Count==0) tables.Add(new("Details",new[] {"Status","Details"},new[] {new[] {"Information","No report is available."}}));
        return new(heading,$"{lines.Length:N0} report entries · {properties.Count:N0} properties · {ranges.Count:N0} changed byte ranges",text,tables);
    }
}
