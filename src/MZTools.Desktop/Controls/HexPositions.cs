namespace MZTools.Desktop.Controls;

// One byte coordinate shared by HEX, ASCII and MZASCII. No source bytes are changed.
internal static class HexPositions
{
    internal static string Format(byte[] bytes)=>string.Join(Environment.NewLine,bytes.Chunk(16).Select(row=>string.Join(" ",row.Select((b,i)=>$"{b:X2}"+(i%4==3&&i<row.Length-1?" ":"")))));
    internal static char Character(byte value,bool mz)=>Display(mz?SharpMzEncoding.FromSHASCII(value):value);
    private static char Display(byte value)=>char.IsControl((char)value)?'.':(char)value;
    internal static int CharacterIndex(string text,int position,bool hex)
    {
        position=Math.Clamp(position,0,text.Length);int count=0;
        for(int i=0;i<position;i++)if(hex?FixedHexInput.IsDigit(text[i]):text[i] is not ('\r' or '\n'))count++;
        return hex?count/2:count;
    }
    internal static int TextIndex(string text,int index,bool hex)
    {
        int remaining=Math.Max(0,index)*(hex?2:1);
        for(int i=0;i<text.Length;i++)if(hex?FixedHexInput.IsDigit(text[i]):text[i] is not ('\r' or '\n')){if(remaining--==0)return i;}
        return text.Length;
    }
    internal static string Ram(ushort? load,int offset)=>load is{} address?$"0x{(address+offset)&0xffff:X4}":"—";
}
