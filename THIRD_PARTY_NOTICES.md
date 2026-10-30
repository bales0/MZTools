# Third-party notices

## Avalonia desktop shell (F3)

The new `MZTools.Desktop` host uses Avalonia 12.1.4 and Fluent templates (MIT),
SkiaSharp 3.119.4, HarfBuzzSharp 8.3.1.3, and their platform native assets.
ANGLE for Windows includes BSD terms. Native assets retain additional third-party
licences; the wrapper package's MIT designation does not replace those terms.
The exact restored package graph, source commits, content hashes and unmodified
licence/notice texts are archived in
[the F3 package inventory](docs/third-party/avalonia-f3/README.md).
These files accompany desktop build and publish outputs.

The new headless test project uses Avalonia.Headless.XUnit 12.1.4 and xUnit v3
3.2.2 (Apache-2.0). The Inter font brought in by the headless platform is under
SIL OFL 1.1; it is not a new production font dependency. Existing test projects
continue using their original xUnit versions.

F5 adds no NuGet packages, bundled native runtimes or fonts. Repair source identity
uses existing Windows system APIs and Linux libc `statx`; these system libraries
are not redistributed by this change. Existing package locks and notices remain
applicable. See the [F5 report](docs/F5_DSK_HFE_2026-10-30.md).

F6 adds no packages, fonts or external FFT/codec implementation. The shared periodic-Hann
FFT service is extracted from the existing Capture implementation. FLAC codec integration
remains deferred to F8. Existing package locks and notices apply.

## HFE / HFEv2 / HFEv3

The HFE header, interleaved side storage, HFEv3 opcode and 36 MHz timing
rules in `Media/HfeImage.cs` are C# adaptations of
[bales0/HxCFloppyEmulator](https://github.com/bales0/HxCFloppyEmulator),
`libhxcfe/sources/loaders/hfe_loader/hfe_format.h`, `hfe_writer.c`,
`hfev3_format.h`, `hfev3_loader.c`, `hfev3_trackgen.h` and `hfev3_writer.c`.
Copyright (C) 2006–2026 Jean-François DEL NERO.

The original copyright statement and associated disclaimer must be retained
in derivative works. These sources are licensed under GNU GPL version 2 or,
at your option, any later version. This adaptation is distributed under
GPL-3.0-or-later, without any warranty, including merchantability or fitness
for a particular purpose. See <https://www.gnu.org/licenses/> or the Free
Software Foundation, 51 Franklin St, Fifth Floor, Boston, MA 02110-1301 USA.
No other HxC floppy loaders or runtime library are included.

## HXCFE QuickDisk Toolkit

The non-SHARP QuickDisk bitstream, Roland/Akai block and CRC rules, and
Thomson MO5 record/checksum and physical-to-logical sector mapping are C#
adaptations of [HXCFE_QuickDisk_Toolkit](https://github.com/jfdelnero/HXCFE_QuickDisk_Toolkit),
particularly `src/qd_roland.c`, `src/qd_akai.c`, `src/qd_mo5.c` and
`src/trk_utils.c`. Copyright (C) 2006–2022 Jean-François DEL NERO.
MO5's mapping table credits Daniel Coulon in the original source.

HxCFloppyEmulator may be used and distributed without restriction provided
that its copyright statement is not removed and any derivative work contains
the original copyright notice and associated disclaimer.

HxCFloppyEmulator is free software; you can redistribute it and/or modify it
under the terms of the GNU General Public License as published by the Free
Software Foundation; either version 2 of the License, or (at your option)
any later version. MZTools distributes this adaptation under GPL-3.0-or-later.

HxCFloppyEmulator is distributed in the hope that it will be useful, but
WITHOUT ANY WARRANTY; without even the implied warranty of MERCHANTABILITY
or FITNESS FOR A PARTICULAR PURPOSE. See the GNU General Public License for
more details. A copy of the license is available from
<https://www.gnu.org/licenses/> or the Free Software Foundation,
51 Franklin St, Fifth Floor, Boston, MA 02110-1301 USA.

Only format inspection algorithms were adapted. The toolkit is not a
runtime dependency, and non-SHARP media are not rewritten by MZTools.

## mzdisk

The Extended CPC DSK container, filesystem detection, FSMZ/IPLDISK, CP/M 2.x
and MRS implementations contain C# ports and adaptations based on
[mzdisk](https://github.com/bales0/mzdisk), copyright Michal Hucik and its
contributors. mzdisk is licensed under the GNU General Public License version
3 or (at your option) any later version. MZTools is distributed under a
GPL-compatible license; the ported source retains origin comments.

The port is an in-process C# implementation. The mzdisk command-line tools are
used only as a development reference and are not a runtime dependency.

MZTools contains C# ports of the ZX0 and ZX7 compressors and Z80 decoder/loader
byte sequences used by these MZF tools:

- https://github.com/bales0/mz0
- https://github.com/bales0/mz7

## ZX0

Copyright (c) 2021 Einar Saukas. All rights reserved.

Redistribution and use in source and binary forms, with or without
modification, are permitted provided that the following conditions are met:

1. Redistributions of source code must retain the above copyright notice,
   this list of conditions and the following disclaimer.
2. Redistributions in binary form must reproduce the above copyright notice,
   this list of conditions and the following disclaimer in the documentation
   and/or other materials provided with the distribution.
3. The name of its author may not be used to endorse or promote products
   derived from this software without specific prior written permission.

THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS"
AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE
IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT HOLDER OR CONTRIBUTORS BE LIABLE FOR
ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES
(INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES;
LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND ON
ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
(INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS
SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.

## ZX7

Copyright (c) 2012-2016 Einar Saukas. All rights reserved.

Redistribution and use in source and binary forms, with or without
modification, are permitted provided that the following conditions are met:

1. Redistributions of source code must retain the above copyright notice,
   this list of conditions and the following disclaimer.
2. Redistributions in binary form must reproduce the above copyright notice,
   this list of conditions and the following disclaimer in the documentation
   and/or other materials provided with the distribution.
3. The name of its author may not be used to endorse or promote products
   derived from this software without specific prior written permission.

THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS"
AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE
IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT HOLDER OR CONTRIBUTORS BE LIABLE FOR
ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES
(INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES;
LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND ON
ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
(INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS
SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.

The ZX7 decoder variants embedded in the referenced MZF tool also credit
Antonio Villena, Metalbrain and Urusergi in the upstream source comments. Those
credits are retained here.

HFEv2 revision and four-byte instruction semantics additionally follow the
[official HxC HFE specification](https://hxc2001.com/floppy_drive_emulator/HFE-file-format.html)
and the maintained [jfdelnero/HxCFloppyEmulator](https://github.com/jfdelnero/HxCFloppyEmulator)
`exthfe_writer.c`. The same author, license and warranty notice above apply.
## NAudio audio components

The Windows capture adapter uses NAudio.Wasapi 3.3.0 and NAudio.Core 3.3.0.
The existing FLAC reader continues to use NAudio.SoundFile 3.1.0.
These NAudio components are MIT licensed:
https://github.com/naudio/NAudio/blob/v3.3.0/LICENSE

Copyright 2008-2026 Mark Heath

Permission is hereby granted, free of charge, to any person obtaining a copy of this
software and associated documentation files (the "Software"), to deal in the Software
without restriction, including without limitation the rights to use, copy, modify,
merge, publish, distribute, sublicense, and/or sell copies of the Software, and to
permit persons to whom the Software is furnished to do so, subject to the following
conditions:

The above copyright notice and this permission notice shall be included in all copies
or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED,
INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A
PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT
HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF
CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE
OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.

## Multiplatform audio and distribution (F7–F9)

Windows uses the existing NAudio WASAPI adapter. Linux/macOS use dynamically loaded
PortAudio (MIT); FLAC uses dynamically loaded libsndfile (LGPL 2.1 or optionally 3).
The native runtime wrapper licence does not replace upstream library/codec terms.
See [the production package inventory](docs/third-party/avalonia-f9/README.md) and
[native licences, source/replacement information and security scope](docs/third-party/avalonia-f9/NATIVE_LIBRARIES.md).
