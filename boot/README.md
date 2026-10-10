# Included boot/system sources

These supplied DSK images are embedded in MZTools for **Make Bootable / Install
CP/M System**. The application verifies each original image against the fixed
SHA-256 catalog in `Dsk/BundledBootSystems.cs` before parsing it. Installer
geometry, DPB, system-area fingerprints and file-preservation checks still apply.

| Source | Installation target |
| --- | --- |
| P-CPM80.dsk | Native P-CP/M80 320 KiB; IPL and allocated PCPM.SYS |
| CPMv23System.dsk | LEC DD 720 KiB |
| CPMv41SystemDD720kB_IRQ.dsk | LEC DD 720 KiB |
| CPMv41SystemHD1440K_IRQ.dsk | LEC HD 1.44 MiB |
| CPMv42SystemDD720kB_POLL.dsk | LEC DD 720 KiB |
| CPMv42SystemHD1440kB_POLL.dsk | LEC HD 1.44 MiB |
| CPMv23System_320K.dsk | Standalone IPL/custom layout; unavailable for system-area installation |

Only boot/system data is installed, not the ordinary files from the source.
Native P-CP/M80 additionally installs PCPM.SYS into user 0 and directory slot 0.
Changes remain in the open document until Save. Sources are retained unchanged.
