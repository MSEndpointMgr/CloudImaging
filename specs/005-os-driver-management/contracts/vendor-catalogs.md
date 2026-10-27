# Contract: Vendor catalogs and classification

**Feature**: [../spec.md](../spec.md) | **Research**: [../research.md](../research.md) R2 to R4, R18

Each vendor sits behind `IDriverVendorAdapter` in `ImagingCoreApi/Services/Drivers/`
(`HpDriverVendorAdapter`, `DellDriverVendorAdapter`): model catalog URL and parser, component
catalog URL and parser, match key, exclusion, classification, lineage, extract kind and install
command. Policy, sync, storage, matching and the Client phases do not depend on the vendor
(FR-001). All catalog files are downloaded by Core with the host allowlist (R2) and read with
`CabinetReader` (R3) and
`XmlReader` (R4).

## 1. HP

| Item | Source |
|---|---|
| Model list | `https://hpia.hpcloud.hp.com/ref/platformList.cab`. Per `Platform`: `SystemID`, `ProductName` entries (display name from the first), and `OS` entries with `OSDescription` (Windows 10 or 11), `OSReleaseIdFilename` (`24H2`) and `OSArchitecture` (x64 or ARM64). One catalog model per system ID and architecture. The driver pack catalog is not used (x64 only). |
| Match key | `Win32_BaseBoard.Product` (Client `MotherboardModel`), compared case-insensitively. |
| Component catalog | `https://hpia.hpcloud.hp.com/ref/<id>/<id>_<arch>_<os>.<release>.cab` (LZX), `<arch>` `64` or `arm64`, `<os>` `11.0` or `10.0`, `<release>` lower case (`24h2`). |
| Component fields | `ImagePal/Solutions/UpdateInfo`: `Id`, `Name`, `Category`, `Version`, `SSMCompliant`, `DPBCompliant`, `SilentInstall`, `Url`, `SHA256`, `Size`, `DateReleased`, `ContentTypes`, `Supersedes`, `ReleaseType`, release notes URL. |
| Exclusion | Category starts with `BIOS`, equals `Firmware`, dock firmware, or name contains `Firmware` (case-insensitive). Excluded entries are never stored. |
| Installable | `SSMCompliant = true` and `SilentInstall` not empty, `None`, `NA`, or containing "not support". |
| Lineage | Follow `Supersedes`; key is the lineage's first known ID. |
| Extract | `Hp` (client contract section 2). |
| Install command | `SilentInstall`, first token (quoted or not) the executable, rest arguments. |

Classification (FR-016):

| Vendor data | Default | Allowed |
|---|---|---|
| `DPBCompliant = true`, no `Uwp` | Offline | Offline, Skip |
| `DPBCompliant = true`, `ContentTypes` has `Uwp` | Offline + Specialize | Offline + Specialize, Offline, Skip |
| `DPBCompliant = true`, `ContentTypes` has `Uwp`, not installable | Offline (drivers only; the app is not installed) | Offline, Skip |
| `Software - *`, `DPBCompliant = false`, installable | Skip | Skip, Specialize |
| `Driver - *`, `DPBCompliant = false`, installable | Specialize | Specialize, Skip |
| Not installable | Skip | Skip (locked) |
| `Diagnostic`, `Manageability*`, `OS`, `Utility`, `DPBCompliant = false` | Skip | Skip (locked) |

Rows are evaluated top to bottom; the first match wins. Offline + Specialize needs an installable
package, so a non-installable package with `Uwp` content gets its own row: its drivers are injected
and its app is not installed.

**HP ARM64 models** (research R24): the SoftPaq executables are x64 and cannot run in ARM64 WinPE.
Offline and Offline + Specialize are removed from the allowed actions; a default of either becomes
Specialize (if installable, else Skip, locked). The install command is the SoftPaq itself,
`"<sp>.exe" /s`, staged unextracted (extract kind `None`).

## 2. Dell

| Item | Source |
|---|---|
| Model list | `https://downloads.dell.com/catalog/CatalogIndexPC.cab` (MSZIP, UTF-16 XML, namespace `openmanage/cm/dm`): `GroupManifest` entries with model name, system ID (SKU), `ManifestInformation@path` and SHA-256 of the per-SKU catalog. Entries are grouped by model name into one catalog model. |
| Match key | `Win32_ComputerSystem.SystemSKUNumber` (Client `SystemSku`). |
| Component catalog | `https://downloads.dell.com/<path>` per SKU (MSZIP), verified against the index's SHA-256. Components of all SKUs of a model are merged by lineage. |
| Component fields | `SoftwareComponent`: `packageID`, `path`, `dellVersion`, `vendorVersion`, `releaseDate`, `size`, `hashMD5`/`Cryptography` SHA-256, `ComponentType@value`, `packageType`, `Name`, `Description`, `ImportantInfo@URL`, `Criticality`, `SupportedOperatingSystems`, `SupportedDevices` and `SupportedDCHDevices` (`PCIInfo`, `@componentID`, `@infType`). |
| Exclusion | `ComponentType` `FRMW` or `BIOS`, or name contains `Firmware`. |
| Windows filter | Drop components whose supported operating systems do not list Windows 11 (or Windows 10 for a Windows 10 release). Each operating system entry's `osArch` (`x64`, `ARM64`) sets the model's architecture; Snapdragon models (Latitude 7455, Latitude 5455, XPS 13 9345) are ARM64 and their packages are native ARM64. |
| Lineage | `SupportedDCHDevices/Device@componentID` (or `SupportedDevices`), else the normalised name; keep the newest `releaseDate` per lineage. |
| Extract | `Dell64` for `LW64`; `None` for `LWXP`. |
| Install command | `"<file>" /s` (Specialize actions). |

Classification (FR-017):

| Vendor data | Default | Allowed |
|---|---|---|
| `DRVR`, `LW64`, bundles an app ("Driver and Application" in the name or an `infType="app"` device) | Offline + Specialize | Offline + Specialize, Offline, Skip |
| `DRVR`, `LW64` | Offline | Offline, Skip |
| `DRVR`, `LWXP` | Specialize | Specialize, Skip |
| `APAC` | Skip | Skip, Specialize |

## 3. Lenovo

Listed in vendor settings as unsupported with a disabled switch; no adapter (FR-002).

## 4. Fixtures

Real catalogs (HP platform list, `8c26_64_11.0.24h2.cab`, `8cbe_arm64_11.0.24h2.cab`, Dell index,
Latitude 0CB2, 0CB4 and 0C85 catalogs) are captured once into `tests/fixtures/drivers/` and drive
the parser, classification and lineage tests; unit tests never call vendor hosts.
