using System;
using System.Linq;
using static MZTools.SharpMzEncoding;
namespace MZTools {
internal static class TapeFileNameEncoding {
        internal static byte[] Encode(string value)
        {
            string name = value.Trim();
            if (name.Length is < 1 or > 16)
            {
                throw new InvalidOperationException("The MZF file name must contain 1 to 16 characters.");
            }
            byte[] encoded = ConvertASCIIStringToSHASCIIBytes(name);
            for (int index = 0; index < name.Length; index++)
            {
                if (name[index] > 0x7F || FromSHASCII(encoded[index]) != (byte)name[index])
                {
                    throw new InvalidOperationException($"The character '{name[index]}' cannot be represented in a Sharp MZ file name.");
                }
            }
            var result = Enumerable.Repeat((byte)0x0D, 16).ToArray();
            encoded.CopyTo(result, 0);
            return result;
        }

}}

