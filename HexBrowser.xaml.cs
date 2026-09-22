using System;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using static QDTool.SharpMzEncoding;
using static QDTool.SharpQdCrc;

namespace QDTool
{
    /// <summary>
    /// Interaction logic for HexBrowser.xaml
    /// </summary>
    public partial class HexBrowser : Window
    {
        private const int BytesPerLine = 16;

        private static readonly Brush QdfPrefixBackground =
            new SolidColorBrush(Color.FromArgb(0x55, 0xff, 0x55, 0x55));

        private static readonly Brush QdfCrcBackground =
            new SolidColorBrush(Color.FromArgb(0x55, 0x42, 0x85, 0xf4));

        private static readonly Brush TrailingDataForeground =
            new SolidColorBrush(Color.FromRgb(0x99, 0x99, 0x99));

        private const string TrailingDataToolTip =
            "Data beyond the size declared in the MZF header. Clear Preserve trailing data before Save As to remove it.";

        public HexBrowser()
        {
            InitializeComponent();
        }

        private static TextBlock CreateDumpTextBlock(string text = "")
        {
            return new TextBlock
            {
                Text = text,
                FontFamily = new FontFamily("Consolas"),
                TextWrapping = TextWrapping.NoWrap
            };
        }

        private static string ConvertToHexDump(byte[] data)
        {
            StringBuilder hexDump = new StringBuilder();

            for (int offset = 0; offset < data.Length; offset += BytesPerLine)
            {
                hexDump.Append(CreateHexDumpLine(data, offset));
                hexDump.AppendLine();
            }

            return hexDump.ToString();
        }

        private static string CreateHexDumpLine(byte[] data, int offset)
        {
            StringBuilder line = new StringBuilder();
            line.AppendFormat("{0:X8}: ", offset);

            for (int column = 0; column < BytesPerLine; column++)
            {
                int index = offset + column;
                line.Append(index < data.Length ? $"{data[index]:X2} " : "   ");
                if (column % 4 == 3)
                {
                    line.Append(' ');
                }
            }

            for (int column = 0; column < BytesPerLine; column++)
            {
                int index = offset + column;
                line.Append(index < data.Length ? ToDisplayCharacter(data[index]) : ' ');
            }

            line.Append("  ");

            for (int column = 0; column < BytesPerLine; column++)
            {
                int index = offset + column;
                if (index < data.Length)
                {
                    line.Append(ToDisplayCharacter(FromSHASCII(data[index])));
                }
            }

            return line.ToString();
        }

        private static char ToDisplayCharacter(byte value)
        {
            return char.IsControl((char)value) ? '.' : (char)value;
        }

        private void AddSection(string heading, byte[] data)
        {
            TextBlock headingBlock = CreateDumpTextBlock(heading);
            headingBlock.FontWeight = FontWeights.SemiBold;
            headingBlock.Margin = new Thickness(0, contentPanel.Children.Count == 0 ? 0 : 10, 0, 0);
            contentPanel.Children.Add(headingBlock);

            TextBlock dumpBlock = CreateDumpTextBlock(ConvertToHexDump(data));
            contentPanel.Children.Add(dumpBlock);
        }

        private void AddFileDataSection(
            byte[] declaredData,
            byte[] trailingData,
            bool showTrailingSummary)
        {
            TextBlock headingBlock = CreateDumpTextBlock(
                "ADDRESS   FILE DATA                                           ASCII             SHASCII (EU)");
            headingBlock.FontWeight = FontWeights.SemiBold;
            headingBlock.Margin = new Thickness(0, 10, 0, 0);
            contentPanel.Children.Add(headingBlock);

            byte[] allData = new byte[declaredData.Length + trailingData.Length];
            Array.Copy(declaredData, allData, declaredData.Length);
            Array.Copy(trailingData, 0, allData, declaredData.Length, trailingData.Length);

            for (int offset = 0; offset < allData.Length; offset += BytesPerLine)
            {
                contentPanel.Children.Add(CreateFileDataLine(allData, offset, declaredData.Length));
            }

            if (showTrailingSummary)
            {
                TextBlock legend = CreateDumpTextBlock();
                legend.Margin = new Thickness(0, 4, 0, 0);
                legend.Inlines.Add(new Run($"Declared size: {declaredData.Length} bytes. "));
                Run trailingSummary = new Run($"Trailing data: {trailingData.Length} bytes.");
                if (trailingData.Length > 0)
                {
                    trailingSummary.Foreground = TrailingDataForeground;
                    trailingSummary.ToolTip = TrailingDataToolTip;
                }
                legend.Inlines.Add(trailingSummary);
                contentPanel.Children.Add(legend);
            }
        }

        private static TextBlock CreateFileDataLine(byte[] data, int offset, int declaredLength)
        {
            TextBlock line = CreateDumpTextBlock();
            line.Inlines.Add(new Run($"{offset:X8}: "));

            for (int column = 0; column < BytesPerLine; column++)
            {
                int index = offset + column;
                string text = index < data.Length ? $"{data[index]:X2} " : "   ";
                AddFileDataRun(line, text, index < data.Length && index >= declaredLength);

                if (column % 4 == 3)
                {
                    line.Inlines.Add(new Run(" "));
                }
            }

            for (int column = 0; column < BytesPerLine; column++)
            {
                int index = offset + column;
                string text = index < data.Length ? ToDisplayCharacter(data[index]).ToString() : " ";
                AddFileDataRun(line, text, index < data.Length && index >= declaredLength);
            }

            line.Inlines.Add(new Run("  "));

            for (int column = 0; column < BytesPerLine; column++)
            {
                int index = offset + column;
                if (index < data.Length)
                {
                    AddFileDataRun(
                        line,
                        ToDisplayCharacter(FromSHASCII(data[index])).ToString(),
                        index >= declaredLength);
                }
            }

            return line;
        }

        private static void AddFileDataRun(TextBlock line, string text, bool isTrailing)
        {
            Run run = new Run(text);
            if (isTrailing)
            {
                run.Foreground = TrailingDataForeground;
                run.ToolTip = TrailingDataToolTip;
            }

            line.Inlines.Add(run);
        }

        private void AddQdfHeaderSection(byte[] data)
        {
            TextBlock headingBlock = CreateDumpTextBlock(
                "ADDRESS   QDF HEADER DATA                                     ASCII             SHASCII (EU)");
            headingBlock.FontWeight = FontWeights.SemiBold;
            headingBlock.Margin = new Thickness(0, 10, 0, 0);
            contentPanel.Children.Add(headingBlock);

            for (int offset = 0; offset < data.Length; offset += BytesPerLine)
            {
                contentPanel.Children.Add(CreateHighlightedQdfLine(data, offset));
            }

            TextBlock legend = CreateDumpTextBlock();
            legend.Margin = new Thickness(0, 4, 0, 0);
            legend.Inlines.Add(new Run("Legend: "));
            legend.Inlines.Add(CreateHighlightedRun(
                " QDF block prefix (A5, block type, data length) ",
                QdfPrefixBackground,
                "Bytes 0-3: synchronization marker, block type, and little-endian data length."));
            legend.Inlines.Add(new Run("  "));
            legend.Inlines.Add(CreateHighlightedRun(
                " CRC ",
                QdfCrcBackground,
                "Bytes 68-69: calculated two-byte CRC."));
            contentPanel.Children.Add(legend);
        }

        private static TextBlock CreateHighlightedQdfLine(byte[] data, int offset)
        {
            TextBlock line = CreateDumpTextBlock();
            line.Inlines.Add(new Run($"{offset:X8}: "));

            for (int column = 0; column < BytesPerLine; column++)
            {
                int index = offset + column;
                string text = index < data.Length ? $"{data[index]:X2} " : "   ";

                if (index is >= 0 and <= 3)
                {
                    line.Inlines.Add(CreateHighlightedRun(
                        text,
                        QdfPrefixBackground,
                        "QDF block prefix: A5 synchronization marker, block type, and data length."));
                }
                else if (index is >= 68 and <= 69)
                {
                    line.Inlines.Add(CreateHighlightedRun(
                        text,
                        QdfCrcBackground,
                        "Calculated QDF header CRC."));
                }
                else
                {
                    line.Inlines.Add(new Run(text));
                }

                if (column % 4 == 3)
                {
                    line.Inlines.Add(new Run(" "));
                }
            }

            StringBuilder characters = new StringBuilder();
            for (int column = 0; column < BytesPerLine; column++)
            {
                int index = offset + column;
                characters.Append(index < data.Length ? ToDisplayCharacter(data[index]) : ' ');
            }

            characters.Append("  ");

            for (int column = 0; column < BytesPerLine; column++)
            {
                int index = offset + column;
                if (index < data.Length)
                {
                    characters.Append(ToDisplayCharacter(FromSHASCII(data[index])));
                }
            }

            line.Inlines.Add(new Run(characters.ToString()));
            return line;
        }

        private static Run CreateHighlightedRun(string text, Brush background, string toolTip)
        {
            return new Run(text)
            {
                Background = background,
                ToolTip = toolTip
            };
        }

        internal void ShowHexDump(TapeRecord record, bool advancedFeaturesEnabled)
        {
            MZQFileHeader header = record.Header;
            MZQFileBody body = record.Body;
            byte[] mzfHeaderData = record.GetSerializedHeader();
            byte[] mzfSizeBytes = BitConverter.GetBytes(header.MzfSize);
            byte[] mzfStartBytes = BitConverter.GetBytes(header.MzfStart);
            byte[] mzfExecBytes = BitConverter.GetBytes(header.MzfExec);

            byte[] qdfHeaderData = new byte[70];
            qdfHeaderData[0] = 0xA5;
            CRC_check(0xA5, true);
            qdfHeaderData[1] = header.MzfHeaderSign;
            CRC_check(header.MzfHeaderSign);
            byte[] dataSizeBytes = BitConverter.GetBytes(header.DataSize);
            Array.Copy(dataSizeBytes, 0, qdfHeaderData, 2, dataSizeBytes.Length);
            CRC_check(dataSizeBytes, 0, dataSizeBytes.Length);
            qdfHeaderData[4] = header.MzfFtype;
            CRC_check(header.MzfFtype);
            Array.Copy(header.MzfFname, 0, qdfHeaderData, 5, header.MzfFname.Length);
            CRC_check(header.MzfFname, 0, header.MzfFname.Length);
            qdfHeaderData[21] = header.MzfFnameEnd;
            CRC_check(header.MzfFnameEnd);
            Array.Copy(header.Unused1, 0, qdfHeaderData, 22, header.Unused1.Length);
            CRC_check(header.Unused1, 0, header.Unused1.Length);
            Array.Copy(mzfSizeBytes, 0, qdfHeaderData, 24, mzfSizeBytes.Length);
            CRC_check(mzfSizeBytes, 0, mzfSizeBytes.Length);
            Array.Copy(mzfStartBytes, 0, qdfHeaderData, 26, mzfStartBytes.Length);
            CRC_check(mzfStartBytes, 0, mzfStartBytes.Length);
            Array.Copy(mzfExecBytes, 0, qdfHeaderData, 28, mzfExecBytes.Length);
            CRC_check(mzfExecBytes, 0, mzfExecBytes.Length);
            Array.Copy(header.MzfHeaderDescription, 0, qdfHeaderData, 30, 38);
            ushort crc = CRC_check(header.MzfHeaderDescription, 0, 38);
            qdfHeaderData[68] = ReverseBits((byte)(crc >> 8));
            qdfHeaderData[69] = ReverseBits((byte)(crc & 0xFF));

            contentPanel.Children.Clear();
            AddSection(
                "ADDRESS   MZF HEADER DATA                                     ASCII             SHASCII (EU)",
                mzfHeaderData);
            if (advancedFeaturesEnabled)
            {
                AddQdfHeaderSection(qdfHeaderData);
            }
            AddFileDataSection(
                body.MzfBody,
                advancedFeaturesEnabled ? body.TrailingData ?? Array.Empty<byte>() : Array.Empty<byte>(),
                showTrailingSummary: advancedFeaturesEnabled);
        }
    }
}
