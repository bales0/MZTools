# MZF / CP/M interoperability research — 2026-10-08

This report precedes the Phase E compatibility analyzer. No general MZF-to-COM
converter or launcher is enabled. Filename, LOAD=0100 and EXEC=0100 do not prove
CP/M compatibility. No historical executable is added to the repository.

## Sources and acquisition

- [SCAV software index](https://mz-800.scav.cz/download/MZ-800/download_MZ-800_software.html)
- [SCAV DSK index](https://mz-800.scav.cz/sharp_mz-800/sharp_mz-800_w_Download_DSK.htm)
- [ORDOZ author archive](https://www.ordoz.com/sharp/)

SCAV direct requests returned HTTP 502 during this investigation. Its searchable
index lists MZRUN.COM, MZX.COM, RUNMZFN.COM, MLOAD.COM and MSAVE.COM. Presence in
an index is acquisition evidence, not evidence of conversion behavior. ORDOZ
publishes `mgr.mzf`, `mgr.com.mzf` and a CP/M manager-loader disk; a filename pair
does not establish that the bodies are interchangeable.

Existing local, optional corpus was opened through MZTools' CP/M reader without
changing disks. Extracted bytes remained in the OS temporary directory. CP/M
extraction includes record padding; fingerprints below cover those exact bytes.

| Utility | Local acquisition | Bytes | SHA-256 |
|---|---|---:|---|
| MZX.COM | `MZTool.Tests/DSK/CPMv41 Programy Demo 1 MZF.dsk`, user 15 | 3584 | `590E9E69248CD4046AC0D997A47546291984FFA381293ED1731CC9CCBA271FC7` |
| LOAD.COM | `MZTool.Tests/DSK/mz-2z047v10a.DSK` and `sds400.dsk` | 1792 | `C885D061A5DCB3830AB1C29A13E34D3C0560CC3FAF569BC55B195C0330A9CBD6` |

## MZX.COM: embedded help and targeted Z80 disassembly

Embedded identification: mZx's disc manager release 4.2 CS, copyright 1991 mZx;
Autospeed routines attributed to MiKrSoft. Embedded help lists cassette load/save,
CP/M cassette transfer, verification, MZF execution (`CMT R name`) and Sharp
monitor boot (`CMT M`). The executable is MZX.COM while the embedded help uses
the command name CMT; this discrepancy is retained, not silently resolved.
No separate MAN/TXT/HLP matching this binary was found in the local corpus.

Addresses below assume COM load at 0100. This is targeted manual disassembly of
reachable entry/dispatch code, not a full control-flow reconstruction.

```text
0100  11 FB 0A      LD DE,0AFB
0103  CD 17 08      CALL 0817       ; output helper
0106  3E 01         LD A,01
0108  32 16 0E      LD (0E16),A
... command byte read from 005D ...
0136  3A 5D 00      LD A,(005D)
0139  FE 52         CP 'R'
013B  CA E2 01      JP Z,01E2

01E2  CD 95 06      CALL 0695
01E5  CD 7E 07      CALL 077E
01E8  CD 73 05      CALL 0573
01EB  11 06 0D      LD DE,0D06
01EE  CD 17 08      CALL 0817
01F1  0E 01         LD C,01
01F3  CD 11 08      CALL 0811
01F6  FE 4E         CP 'N'
01F8  CA 04 08      JP Z,0804
01FB  3E 00         LD A,00
01FD  32 13 0E      LD (0E13),A
0200  31 F0 10      LD SP,10F0
0203  21 33 02      LD HL,0233
0206  11 00 10      LD DE,1000
0209  01 82 00      LD BC,0082
020C  ED B0         LDIR
020E  3E 07         LD A,07
0210  D3 FC         OUT (FC),A
0212  D3 FD         OUT (FD),A
0214  F3            DI
0215  ED 56         IM 1
0217  3E 08         LD A,08
0219  D3 CE         OUT (CE),A
021B  3E 01         LD A,01
021D  D3 CD         OUT (CD),A
021F  D3 CC         OUT (CC),A
0221  DB E0         IN A,(E0)
0223  21 00 10      LD HL,1000
0226  11 00 C0      LD DE,C000
0229  01 00 10      LD BC,1000
022C  ED B0         LDIR
022E  DB E1         IN A,(E1)
0230  C3 07 10      JP 1007

0817  0E 09         LD C,09
0819  CD 11 08      CALL 0811
081C  C9            RET
```

The R path explicitly relocates helper code, changes interrupts/mode and touches
multiple hardware ports. Combined with embedded help, this is strong evidence
of **Run MZF under CP/M**, not evidence of creation of standalone COM files.
The exact paging effects, restoration path, supported LOAD/EXEC ranges and
external ROM/runtime assumptions have not been established. Do not copy this
stub or assert a compatible launcher from this partial analysis.

## LOAD.COM

Entry `C3 40 02` is `JP 0240`. Embedded copyright identifies Digital Research
(1978), with HEX, checksum, invalid-hex, load-address and COM output strings.
This suggests the standard HEX-to-COM loader, not an MZF converter. Classification
remains strong evidence; full instruction/data-flow analysis was not completed.

## Unsupported and unresolved cases

MZXCONV.COM, MZXBACK.COM, MZRUN.COM, RUNMZFN.COM, MLOAD.COM, MSAVE.COM,
REN-MZF.COM and DEBOMZF.COM were not available as identified binaries in the
local corpus. Command syntax, fingerprints and mechanisms remain unknown.
No historical original-MZF / standalone-COM pair was validated. No emulator was
introduced and no real-hardware behavior is claimed.

Case A requires independent provenance of an already native CP/M body, bounded
TPA and entry-point/runtime validation. Case B requires a fully understood
historical runner environment and tests. Neither gate is satisfied here.
All current inputs therefore remain **Unsupported / conversion not proven safe**;
the analyzer exposes structural facts and explicitly heuristic indicators only.

## Licensing and implementation

Archive availability does not grant redistribution rights. Copyright/licensing
for these binaries is unconfirmed; no binary or stub is shipped. No historical
algorithm was copied. The original analyzer performs header/range inspection and
conservative byte-pattern diagnostics; no relocation or ROM emulation is used.
Future runner profiles must carry source evidence, exact fingerprints, CP/M
generation, memory/LOAD bounds, EXEC behavior, runtime requirements and tests
before any Convert action can be enabled.
