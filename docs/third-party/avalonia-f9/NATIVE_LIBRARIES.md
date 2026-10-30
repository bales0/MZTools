# Native audio libraries

PortAudio and libsndfile are dynamically loaded by `MZTools.Platform.Audio`.
The portable Capture pipeline does not depend on either native library.

| Library | Use / delivery | Original terms and source |
| --- | --- | --- |
| PortAudio v19 | Explicit Linux/macOS audio input; system package/Homebrew, not bundled here | [MIT text](PORTAUDIO_MIT.txt), [upstream source and builds](https://github.com/PortAudio/portaudio/tree/v19.7.0) |
| libsndfile 1.2.2 ABI | FLAC only; Windows uses the existing `org.sndfile.runtime.win-x64`/`win-arm64` 1.2.2 runtime packages; Linux/macOS use system libraries | [LGPL 2.1 text](LIBSNDFILE_COPYING.txt), [upstream corresponding release source](https://github.com/libsndfile/libsndfile/tree/1.2.2), [dynamic-library replacement guidance](https://libsndfile.github.io/libsndfile/FAQ.html) |
| libFLAC | May be included in the Windows libsndfile binary; system dependency on Unix | [Xiph BSD terms](FLAC_COPYING.txt), [source](https://github.com/xiph/flac) |
| libogg | May be included in the Windows libsndfile binary | [BSD terms](OGG_COPYING.txt), [source](https://github.com/xiph/ogg) |
| libvorbis | May be included in the Windows libsndfile binary | [BSD terms](VORBIS_COPYING.txt), [source](https://github.com/xiph/vorbis) |

The NuGet runtime wrapper's Apache-2.0 package declaration does not replace the
native library or codec licences. Original downloaded texts and SHA-256 values are
in [native-sources.json](native-sources.json); restored package hashes and repository
metadata are in [packages.json](packages.json). The LGPL v3 option also has its
[text](LGPL_3_0.txt) and the referenced [GPL v3 text](GPL_3_0.txt) alongside it.

These are replaceable shared libraries. The application is not trimmed or packed
into a single-file native executable. An appropriately ABI/architecture-compatible
library may be replaced by the user; no restriction on debugging such a modified
combination is imposed by this integration. Build instructions and source for the
upstream native libraries remain available from the linked upstream repositories.
No modified native library binary is produced by this repository.

On Debian install `libportaudio2 libsndfile1`; on macOS install matching Homebrew
`portaudio libsndfile`. The application checks the process architecture when loading
Windows runtime assets. macOS looks in Apple Silicon and Intel Homebrew locations;
incompatible native images are not treated as valid captures. The codec version
actually loaded can be inspected with `MZTools.Desktop --validate-runtime`, which
does not enumerate or start an input.

## Native security scope

NuGet vulnerability reports alone do not certify native code. libsndfile upstream
still labels 1.2.2 as its latest release; distribution builds can carry additional
patches. Track native issues separately using the
[Debian tracker](https://security-tracker.debian.org/tracker/source-package/libsndfile)
and [upstream issues](https://github.com/libsndfile/libsndfile/issues).
For example CVE-2026-88386 concerns native WAV fmt parsing; other reported issues
cover AIFF, MAT, MPEG and Vorbis. This adapter does **not** pass those input formats
to libsndfile: a managed native-FLAC signature/STREAMINFO validator runs before
native FLAC Open, and WAV input for FLAC encoding is parsed by the existing managed
WAV reader. This narrows exposure; it is not a claim that all native vulnerabilities
are absent. Review updated native packages before a signed public release.
