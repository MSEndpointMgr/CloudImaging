# TODO: Firmware (BIOS) updates during imaging

Status: **version 1 implemented (2026-11-15), not yet tried on hardware.** Spec:
`specs/006-firmware-updates/spec.md`. HP x64 only, no Sure Admin signing. Still open: the hardware
test on the EliteBook 8 G1a (open questions 2 to 4 below) and Sure Admin payload signing (later
phase). The payload format is reproduced in .NET and proven byte identical to CMSL offline (see
"Desk spike results"). **A Sure Admin test device showed that signing is not needed when the BIOS
policy lets upgrades through** (see "Test device findings").

## Why

- Imaging is the best moment to update firmware: the disk was just wiped, BitLocker is not on yet,
  there is no user data and an operator is at the device.
- Firmware updates carry the Secure Boot 2023 certificates (the 2011 Microsoft certificates expired
  in June 2026), TPM fixes and fixes newer drivers depend on.
- Spec 005 (OS driver management) deliberately excludes all firmware (FR-003: HP `BIOS*`,
  `Firmware`, dock firmware; Dell `FRMW`, `BIOS`; names containing "Firmware") and calls firmware
  management "a separate feature". This is that feature.

## How firmware differs from drivers

| | Drivers | Firmware |
|---|---|---|
| Scope | Per model and Windows release | Per model only, release independent |
| Unit | A set of many components | Usually one item (system BIOS) |
| Applies | Offline or during Specialize | At a restart, before the OS: the tool stages, the firmware flashes |
| Depends on the device | Hardware match | Current BIOS version (upgrade only), AC power, Sure Admin or BIOS password state |
| Failure | One component | Usually keeps the old firmware (HP Sure Start, Dell recovery), but higher risk |

## Agreed scope for version 1

- **What updates:** HP (first) and Dell system BIOS only. Upgrade only, never downgrade (vendor
  security floors block downgrades anyway). Approval on by default.
- **When:** staged in WinPE as the last imaging step, applied by the restart that already ends
  imaging. No extra restart, no return to WinPE.
- **Skipped, imaging continues:** device on battery, BIOS password set without support for it,
  legacy BIOS boot mode, tool failure. Each skip has a clear outcome on the timeline.
- **Must work with HP Sure Admin** (certificate based, no password).
- **Later:** BIOS password support for non Sure Admin devices, confirming the result in WinPE,
  TPM, Thunderbolt, SSD and dock firmware, Lenovo.

## Placement in the deployment timeline

```
Prepare disk > Download image > Apply Windows image > Install drivers > Prepare setup >
Configure boot > Configure recovery > Update firmware (stage only) > restart (flash, then setup)
```

- Last WinPE step, after Configure boot, so bcdboot cannot touch what was staged.
- Optional step (like Install drivers): a failure gives "Completed with warnings", never a failed
  session, because Windows is already applied.
- The portal can report "Staged, applies at restart", not the flash result (same limit as the
  Specialize phase: nothing is reported after the reboot). The setup runner could log the BIOS
  version during Specialize on the device.
- The Client's completion screen must say: "Firmware updates during the restart. Keep the device
  on AC power and don't turn it off."

Rejected or deferred placements:

- **Flash before Prepare disk, restart back into WinPE:** would allow confirming the result and
  keeps the old OS on failure, but needs session resume across restarts and forcing the next boot
  onto the boot media (UEFI BootNext). Devices booted from USB via a one time boot menu normally
  come back on the internal disk. Possible later phase.
- **Vendor installer in Specialize via the setup runner:** reuses existing code, but the flash
  lands during the restart into OOBE/Autopilot and nothing is reported.
- **Offline injected UEFI capsule INF:** Windows applies it at a later restart without vendor
  tools; depends on HP and Dell publishing capsule packages per model, timing outside our control.
  **Reopened (2026-10-30):** HP publishes them per BIOS family (the same CABs Windows Update
  installs, see "Test device findings"), no CMSL and no Sure Admin payload needed. Lags behind
  the BIN catalog.

## HP research findings (sources: HP CMSL docs on developers.hp.com, BCU release notes)

### Flashing

- HP Client Management Script Library (CMSL) `Update-HPFirmware -File <bios.bin> [-Offline]
  [-BitLocker ignore] [-Quiet]`. `-Offline` stages the update; the flash happens at the next
  reboot in the pre-OS environment. **This matches the planned flow.**
- Requirements: HP BIOS, 64-bit PowerShell, **UEFI boot mode (legacy not supported)**, elevated.
- In WinPE: use `-BitLocker ignore`. HP's WinPE guide lists these optional components: WinPE-WMI,
  WinPE-NetFX, WinPE-Scripting, WinPE-PowerShell, WinPE-StorageWMI, WinPE-DismCmdlets,
  **WinPE-SecureBootCmdlets**, plus the HP WinPE driver pack. 32-bit WinPE not supported. SoftPaq
  functions of CMSL do not work in WinPE (flashing does).
- Our boot image today has WMI, NetFx, Scripting, PowerShell
  (`src/CloudImaging.MediaBuilder/Services/WinPeOptionalComponents.cs`), plus SecureStartup and
  PlatformId (`AutopilotTooling.cs`). **Missing: SecureBootCmdlets and StorageWMI** (DismCmdlets
  is in HP's guide but the flash path does not use it, see "CMSL implementation reference").

### HP Sure Admin (Enhanced BIOS Authentication Mode)

- Built on HP Secure Platform Management (SPM): an Endorsement Key (EK) protects the Signing Key
  (SK); the SK public key is provisioned in the BIOS; commands must be signed with the SK private
  key. Every request carries an anti-replay nonce.
- With Sure Admin the BIOS password is replaced by a **signed payload**:
  `Update-HPFirmware -File bios.bin -PayloadFile payload.dat` (or `-Payload <string>`).
- The payload comes from `New-HPSureAdminFirmwareUpdatePayload -File <bios.bin>` with one of:
  `-SigningKeyFile <pfx> [-SigningKeyPassword]`, `-SigningKeyCertificate <X509Certificate2>`, or
  `-RemoteSigningServiceKeyID` + `-RemoteSigningServiceURL` (HP Sure Admin KMS, interactive MSAL
  sign-in with optional `-CacheAccessToken`).
  - The payload authorizes the update **only for that exact file**.
  - `-TargetUUID <guid>` locks it to one device (otherwise any device).
  - `-SingleUse` makes it non replayable (BIOS ActionsCounter is incremented).
  - `-Nonce <uint32>`: defaults to current time; must be >= the last nonce the BIOS accepted.
  - `-Bitlocker`, `-Force` (flash even if the same version is installed).
- HP's security note: create payloads only on secure servers, send only the payload to clients,
  never the keys.
- `Get-HPSureAdminState` and `Get-HPSecurePlatformState` report the state (Sure Admin needs
  "HP P21 enabled" and a BIOS with Sure Admin support).

### BCU (HP BIOS Configuration Utility)

- Latest 4.0.33.1 (sp143621, 2022-12-08). **4.0.30.1 added HP Sure Admin support.** 4.0.32.1
  mentions `/addauthstr` on a repset file (adds authorization strings for signed setting changes)
  and PFX files with password protected private keys. `/cpwdfile` and HPQPswd for password mode.
- BCU sets BIOS options (boot order, TPM, Secure Boot and so on). **It does not flash firmware.**
  It belongs to a related "BIOS settings during imaging" feature that would reuse the same signing
  setup. The user wants this to work with BCU.

## Desk spike results (2026-10-29, CMSL 1.9.0 source)

### Sure Admin firmware update payload format

Read from `HP.Firmware.SureAdmin.psm1` (`New-HPPrivateSureAdminFirmwareUpdateAuthorization`,
`Invoke-HPPrivateConstructHeader`) and the `HP.CMSLHelper.dll` types.

- Header `SureAdminSignatureBlockHeader`, 27 bytes, packed, little endian: `Version` byte = 1,
  `NameLength` u16, `ValueLength` u16, `OneTimeUse` byte (1 = single use), `Nonce` u32,
  `Reserved` byte = 1, `Target` 16 bytes = `Guid.ToByteArray()` of the device UUID
  (`ffffffff-...` = any device).
- Name = UTF-16LE `"Allowed BIOS Update Hash"` (48 bytes). Value = raw SHA-256 of the BIN (32 bytes).
- Signed data = header + name + value. Signature = RSA PKCS#1 v1.5 over SHA-256, then the
  signature bytes are **reversed** (little endian).
- Authorization = name + value + UTF-16LE `"<BEAM/>"` + header + reversed signature (377 bytes
  with a 2048-bit key).
- Wrapper (what `-PayloadFile` reads): JSON `{"timestamp","purpose":"hp:sureadmin:firmwareupdate",
  "Data":[bytes],"Meta1..4":null}`; `Data` is UTF-8 of the compact JSON
  `{"Quiet","Force","FileName","Authorization":[bytes],"bitlocker"}`. Byte arrays are JSON number
  arrays.
- **Proven offline:** with a throwaway 2048-bit key, a dummy BIN, a fixed nonce and UUID, the .NET
  rebuild (signing a precomputed SHA-256 digest, which is exactly Key Vault's `RS256` sign
  operation) produced an authorization **byte identical** to `New-HPSureAdminFirmwareUpdatePayload`.
  So Core can sign with a non exportable Key Vault RSA key: `CryptographyClient.SignAsync(RS256,
  digest)`, reverse the bytes. Still to prove: a real BIOS accepts it.

### HP BIOS catalog

- Per platform: `https://ftp.hp.com/pub/pcbios/<SysId>/<SysId>.xml` (SysId = BaseBoard Product,
  which the Client already reports). BIN at `https://ftp.hp.com/pub/pcbios/<SysId>/<Bin>`.
- `<Rel Ver="01.25.01" Date="2026-06-17" Bin="T74_01250100.bin" RB="1" L="0" Md5 Sha256 Sha384/>`.
  `RB` = rollback allowed, `L` = criticality (`BiosUpdateCriticality` enum), `DP` = dependency
  (optional). Older entries can lack `Sha256` (Sha384 only); CMSL verifies with SHA-384.
- CMSL sorts versions as strings; we must compare versions numerically per segment.
- Separate `https://hpia.hpcloud.hp.com/downloads/capsule/<family>.json` feed exists for UEFI
  capsules (relevant to the rejected "offline capsule INF" placement).

### Flash preconditions found in `Update-HPFirmware`

- Refuses unless elevated, UEFI mode, and BIOS setting **"Lock BIOS Version"** is Disable.
- Asks the BIOS (`auth_required_for_upgrade`) whether an upgrade needs authorization at all. With
  Sure Admin on but upgrades policy-allowed, **no payload is needed**. The device report should
  carry this, so Core only signs when required.
- With Sure Admin and auth required it forces offline (staged) mode.
- Stages to the ESP from `Get-HPEFIPartitionPath -FixedOnly`. In WinPE it does not narrow by boot
  disk, so **two fixed disks with an ESP each throws "too many candidates"** (needs a skip outcome).
- Same version without `-Force` returns silently (Write-Host only). The Client must compare
  versions itself and report "Up to date" instead of relying on the cmdlet.
- The actual flash is native: `dfmbios64.dll` `flash_hp_device64`.

### CMSL licence

- HP EULA (`license.txt` in every module): "You do not have the right to distribute the Software
  Product." **We cannot bundle CMSL in our release or boot image download.** The customer must
  obtain it themselves (for example Media Builder runs `Save-Module HPCMSL` from the PowerShell
  Gallery on the operator's machine at build time, with the operator accepting HP's licence).
- **Decision (2026-11-14): CMSL in WinPE for version 1.** An acceptance check box cannot give
  us a distribution right. It is the acceptance half of the "customer downloads it" model:
  - Media Builder shows "Include HP firmware tools (HP Client Management Script Library)" with a
    link to HP's licence (PowerShell Gallery `LicenseUri`) and an "I accept HP's licence terms"
    check box. Unchecked by default; the build cannot include CMSL without it.
  - When accepted, Media Builder downloads `HPCMSL` and its dependency modules from the
    PowerShell Gallery (publisher HP; 1.9.1 at the time of writing) straight onto the operator's
    machine, verifies HP's Authenticode signature on every module, and copies them into the boot
    image. CMSL never passes through our release, Core or storage.
  - Boot media without CMSL: the Client reports "Skipped: HP firmware tools not included in the
    boot media".
  - One boot image flashing many HP devices is the intended use of CMSL; not pursued further.

## Test device findings (2026-10-30, HP EliteBook 8 G1a 14 inch)

Board ID `8E17`, BIOS family `X84`, BIOS 01.06.01, AMD. Sure Admin **On** (Enhanced BIOS
Authentication Mode 1.01), SPM **Provisioned**, signing key held in an **HP Sure Admin KMS hosted in
Azure** (Local Access Key enrolment data points at an `azurewebsites.net` KMS).

**Why Windows Update flashes this device without Sure Admin authorization:** two signatures are in
play. HP's signature on the firmware image is always verified by the BIOS (Sure Start). The
customer's Sure Admin authorization is only required when the BIOS policy demands it, and that
policy is a BIOS setting:

| BIOS setting | Value on device | Meaning |
|---|---|---|
| BIOS Update Credential Policy | **Never Require Credentials** | No authorization for upgrades or downgrades. Other values: Require Credentials on Downgrade Only, Always Require Credentials |
| Lock BIOS Version | Disable | `Update-HPFirmware` refuses to flash when enabled |
| Native OS Firmware Update Service | Enable | UEFI capsules from Windows Update are allowed |
| BIOS Rollback Policy | Unrestricted Rollback to older BIOS | |
| Minimum BIOS Version | 00.00.00 | No floor set |
| Automatic BIOS Update Setting | Disable | The BIOS does not update itself from HP |
| Windows UEFI CA 2023 / Microsoft UEFI CA 2023 / Option ROM UEFI CA 2023 | Enable | 2023 Secure Boot certificates already in the BIOS |

CMSL confirms this logic: `Test-HPAuthRequired` (exported from `HP.Private`) asks the BIOS before
`Update-HPFirmware` requests a payload, and `Get-HPBIOSWindowsUpdate -Flash` reads "BIOS Update
Credential Policy" and only refuses on "Always Require Credentials".

**Consequences:**

- Version 1 needs no signing. The device report carries "BIOS Update Credential Policy", "Lock
  BIOS Version" and "Native OS Firmware Update Service", read from `root\HP\InstrumentedBIOS`
  (`HP_BIOSEnumeration`, no CMSL needed). Outcomes: "Skipped: BIOS requires credentials for
  updates" and "Skipped: BIOS version is locked".
- Sure Admin payload signing (Key Vault or the customer's HP Sure Admin KMS) becomes a later phase
  for customers with "Always Require Credentials". The desk spike work stays valid for it and for
  the BIOS settings feature.

**Catalogs for the test device:**

- BIN catalog `ftp.hp.com/pub/pcbios/8E17/8E17.xml`: latest 01.06.01 (2026-08-14), every entry
  `RB=1`, `L=1`.
- Capsule catalog `hpia.hpcloud.hp.com/downloads/capsule/X84/X84.json` (what
  `Get-HPBIOSWindowsUpdate` reads; family from `Get-HPBIOSFamily`): entries `biosFamily`,
  `biosVersion`, `severity`, `isLatest`, `isLatestCritical`, `url`, `digest` (Base64 SHA-1 of the
  CAB). The `url` is the Windows Update catalog CAB, the same package Windows Update installs.
  **Latest capsule is 01.05.04 while the BIN catalog has 01.06.01**: the capsule path lags.
- `Get-HPBIOSWindowsUpdate -Flash` installs the CAB through `Add-HPBIOSWindowsUpdateScripts`
  (group policy scripts) and refuses downgrades.

## CMSL implementation reference (verified 2026-11-14)

Sources: HP docs ([Notes on WinPE usage](https://developers.hp.com/hp-client-management/doc/notes-winpe-usage),
[Update-HPFirmware](https://developers.hp.com/hp-client-management/doc/Update-HPFirmware),
[Get-HPBIOSUpdates](https://developers.hp.com/hp-client-management/doc/Get-HPBiosUpdates),
[FAQ](https://developers.hp.com/hp-client-management/doc/faq),
[changelog](https://developers.hp.com/hp-client-management/doc/changelog)), the CMSL 1.9.0 source
(`HP.Firmware.psm1` = FW, `HP.Private.psm1` = PV, `HP.ClientManagement.psm1` = CM, line numbers)
and Microsoft's WinPE and PowerShellGet documentation.

### Boot image (Media Builder)

- Add **WinPE-StorageWMI** and **WinPE-SecureBootCmdlets** (with language packs where they
  exist), after WMI, NetFX, Scripting, PowerShell (Microsoft's install order).
  - SecureBootCmdlets: UEFI detection is `try { Confirm-SecureBootUEFI } catch { Legacy }` (CM
    1480-1507); without the cmdlet every flash throws "This cmdlet only supported Windows 10 UEFI
    mode." HP's docs state the component is required.
  - StorageWMI: the ESP lookup uses `Get-Partition`, `Get-Volume`, `Get-Disk` (FW 1386-1410).
  - Not needed by the flash path: DismCmdlets (no DISM calls), the BitLocker module (skipped with
    `-BitLocker ignore`), the HP WinPE driver pack (in HP's guide, no source says flashing needs it).
- x64 only. 32-bit WinPE is unsupported; ARM64 is undocumented and only `dfmbios32/64.dll` ship.
- Copy **all modules** of one CMSL version (HP: "always do so by deploying the entire library")
  to `X:\Program Files\WindowsPowerShell\Modules\HP.X\<version>\`. Every `.psm1` finds
  `HP.CMSLHelper.dll` by a relative path with the version hard coded, so mixed versions or a mixed
  flat/versioned layout break loading. `dfmbios64.dll` (native, static C runtime, needs only
  `mi.dll` from WinPE-WMI) is resolved by `DllImport` from the `HP.Private` folder.
- HPCMSL 1.9.1 = 15 modules pinned to exactly 1.9.1 (HP.Private, Utility, ClientManagement,
  Firmware, Repo, Sinks, Softpaq, Consent, Retail, Notifications, SmartExperiences, Displays,
  Security, Docks, WorkforceExperience). HP.Firmware 1.9.1 is **unlisted** on the Gallery and 1.9.1
  has no changelog entry or installer. **Media Builder should pin a tested CMSL version**, not take
  the latest.
- `Save-Module ... -AcceptLicense` needs a newer PowerShellGet than the 1.0.0.1 that ships with
  Windows PowerShell 5.1, which has no `-AcceptLicense`. The Gallery package has
  `RequireLicenseAcceptance = true`. HP.Consent is analytics consent, not licence acceptance.
- Modules are signed "CN=HP Inc., OU=HP Cybersecurity" (also `dfmbios64.dll`); verify the
  Authenticode signature of every `.psm1`, `.psd1` and `.dll` after download. Execution policy:
  HP's guidance is to trust HP as publisher.

### Runtime (WinPE Client)

- Run CMSL in a **separate 64-bit `powershell.exe` process**, so a CMSL or native DLL failure
  cannot take down the Client. CMSL sets
  `Set-StrictMode -Version 3.0` and `$ErrorActionPreference = "Stop"` (FW 14-15).
- **Import HP.Private, HP.Utility, HP.ClientManagement, HP.Firmware explicitly, not `HPCMSL`.**
  `HPCMSL` also imports HP.Notifications, which resolves WinRT types at import (unsupported in
  WinPE-NetFx; inferred, not tested). HP.Firmware depends on HP.ClientManagement without declaring
  it, so module auto loading or the explicit import is required.
- No network on the flash path, no consent prompt, no prompt at all with `-BitLocker ignore`.
- **CMSL has no AC power check** (none in the scripts, no power API in `dfmbios64.dll`). The
  Client must check AC power itself.
- `Test-HPFirmwareFlashSupported` reads `ReleaseId` under StrictMode (FW 1258); a missing value
  would throw. HP documents `Update-HPFirmware` in WinPE, which runs this check; confirm on
  hardware.

### `Update-HPFirmware -File <bin> -Offline -BitLocker ignore -Quiet`, in order (FW 903-1185)

1. Elevation, UEFI (above), "Lock BIOS Version" must be `Disable` (missing setting is ignored).
   No 64-bit, OS version or power check.
2. Current version: `(Get-HPBIOSVersion -IncludeFamily)` turned into `<family>_<6 digits>`, e.g.
   `X84 Ver. 01.06.01` to `X84_010601`; must match `^[A-Z][0-9]{2}_[0-9]{4,6}$`.
3. **Same version without `-Force`: two `Write-Host` lines ("This system is already running BIOS
   version ...") and return. No exception, not hidden by `-Quiet`.**
4. The target version comes from the **file name** (`X84_01060100.bin`, cut to 10 characters).
   Keep HP's `Bin` name or pass `-FilenameHint`. Up/downgrade detection is a string comparison.
5. ESP: `Get-HPEFIPartitionPath -FixedOnly` (FW 1376-1420): fixed volumes of type System on online
   disks. In WinPE the "boot disk only" filter is skipped, so **two or more ESPs throw "Could not
   locate EFI partition, too many candidates."**; none throws "Could not locate EFI partition. ".
   Returns the volume GUID path; nothing is mounted. HP recommends an ESP of at least 300 MB; our
   default scheme has 500 MB (`PartitioningScheme.Default`), custom schemes may be smaller.
6. Online mode probe runs even with `-Offline` and can throw.
7. Authorization: `Test-HPAuthRequired` (PV 3024) returns false without a BIOS call when no setup
   password and no Sure Admin; otherwise the BIOS decides ("BIOS Update Credential Policy",
   CMSL 1.8.0). Throws "HP Sure Admin is enabled, and authentication is required..." or "BIOS
   Setup Password is set, and authentication is required...".
8. Native `flash_hp_device64(..., delayed: true, no_wait: false)` writes into the ESP (`EFI\HP\...`
   folders; exact files decided natively). The flash happens at the next restart in the pre-OS
   environment; CMSL never restarts the device. Sure Start may restart more than once.
9. No BIN validation in PowerShell (no hash, signature or free space check); the BIOS verifies
   HP's signature at flash time.

**Result detection:** no return object. Staged = no exception. Up to date = `Write-Host` only, so
the Client compares versions **before** calling and treats "already running" in the Information
stream (`6>&1`) as a fallback. Failures are terminating exceptions; map them to outcomes:

| Message (exact) | Outcome |
|---|---|
| This cmdlet only supported Windows 10 UEFI mode. | Skipped: legacy BIOS mode (or SecureBootCmdlets missing) |
| You cannot flash while 'Lock BIOS Version' is enabled. | Skipped: BIOS version is locked |
| HP Sure Admin is enabled, and authentication is required... | Skipped: BIOS requires Sure Admin authorization |
| BIOS Setup Password is set, and authentication is required... | Skipped: BIOS password is set |
| Could not locate EFI partition, too many candidates. | Skipped: more than one EFI system partition |
| Could not locate EFI partition. | Failed: EFI system partition not found |
| Could not copy the file to the system partition. (Win32 1392) | Failed: not enough space on the EFI system partition |
| The firmware does not mach the target platform. (HP's typo) | Failed: firmware does not match this model |
| Current platform does not support this operation. / ...ensure this is a supported HP system. | Skipped: device does not support firmware updates |
| Flash not allowed / The request was not accepted by the BIOS. | Failed: BIOS refused the update |
| Could not call the support library... dfmbios64.dll | Failed: HP firmware tools are incomplete in the boot media |
| anything else | Failed: \<message\> |

### Pre-checks the Client can do without CMSL (`root\HP\InstrumentedBIOS`, WinPE-WMI)

- `HP_BIOSEnumeration` `CurrentValue` of "BIOS Update Credential Policy", "Lock BIOS Version",
  "Enhanced BIOS Authentication Mode" (Sure Admin), "Native OS Firmware Update Service".
- `HP_BIOSSetting` "Setup Password" `IsSet` (what `Get-HPBIOSSetupPasswordIsSet` reads, CM 893).
- `Win32_BIOS.SMBIOSBIOSVersion` (family and version), `Win32_BaseBoard.Product` (platform ID).
- CMSL's `Test-HPAuthRequired` stays the authority for "authorization required" (internal, but
  exported from HP.Private); the WMI values are for the device report and the portal.

### BIOS catalog (Core, .NET, no CMSL)

- `https://ftp.hp.com/pub/pcbios/<PLATFORM>/<PLATFORM>.xml`, platform = `Win32_BaseBoard.Product`
  trimmed and upper case; BIN at `https://ftp.hp.com/pub/pcbios/<PLATFORM>/<Bin>`.
- `<Rel Ver Date Bin RB L DP Md5 Sha256 Sha384/>` attributes, lower case hex. Ignore entries whose
  `Bin` is not `*.bin`. `RB` = rollback allowed, `L` = 0 Recommended / 1 Critical.
- CMSL sorts and compares versions as **strings** and skips the hash check when `Sha384` is
  missing (old entries have no hashes). Core compares numerically per segment, verifies `Sha384`
  or `Sha256`, and **refuses entries without a hash**. Keep the original `Bin` name for step 4.

## Proposed design (HP, Sure Admin)

1. **Device report (WinPE Client):** BIOS version, Sure Admin on or off, BIOS setup password set,
   SPM state, SMBIOS UUID, AC power and battery level, firmware mode (already reported for
   pre-flight). Read through HP's BIOS WMI (`root\HP\InstrumentedBIOS`), available in WinPE with
   WinPE-WMI. Gotcha: `DeviceGatewayApiClient.SignPayload` rebuilds the registration payload, so
   every new property must be copied there or it is silently dropped.
2. **Payload signing (Imaging Core), later phase:** only for devices whose "BIOS Update Credential
   Policy" requires credentials for upgrades. For each session that gets an update, create a payload that
   is single use, locked to the device UUID and to the BIOS file. Sign with the customer's SPM
   signing key held in **Azure Key Vault as a non exportable key**; Core's managed identity only
   gets the sign operation. The payload travels with the session (like the driver set
   descriptor). No secret ever reaches the device.
   - CMSL cannot sign with a non exportable Key Vault key (it needs a PFX, an X509 object with a
     private key, or HP's interactive KMS). So Core must **reproduce the CMSL payload format in
     .NET**. CMSL is PowerShell source, so the format can be read from it. This is the riskiest
     unknown.
3. **Flash (WinPE Client):** last step runs
   `Update-HPFirmware -File <bin> -Offline -BitLocker ignore -Quiet` (no payload in version 1),
   wrapped as described in "CMSL implementation reference".
   Outcomes: "Staged 01.18.00 to 01.21.00, applies at restart", "Up to date",
   "Skipped: on battery", "Skipped: BIOS requires Sure Admin authorization", "Skipped: BIOS
   password is set", "Skipped: BIOS version is locked", "Skipped: legacy BIOS mode", "Skipped: more
   than one EFI system partition", "Skipped: HP firmware tools not included in the boot media",
   "Failed: <reason>".
4. **Non Sure Admin devices with a BIOS password (phase 2):** same flow with `-Password` or an
   encrypted HP password file (`Write-HPFirmwarePasswordFile`, HPQPswd). Needs its own security
   review because a secret then reaches the device.
5. **Catalog:** HP publishes a BIOS catalog per platform that CMSL `Get-HPBIOSUpdates` reads (BIN
   files with versions and hashes). Location confirmed, see "HP BIOS catalog" above.
   Core reads it like the HP driver catalog (no scraping), verifies the hash and stores the BIN in
   the `drivers` storage container (or a new `firmware` one). Dell: the driver catalog already
   lists `BIOS`/`FRMW` entries that FR-003 currently drops; Dell flashing in WinPE uses
   `Flash64W.exe` (to research).

## Portal

- **Model page:** split into **Drivers** (per Windows release, today's page) and **Firmware** (per
  model, no release dropdown). Firmware view: policy (Off by default, Latest approved, Pinned
  version), version list from the catalog with release date and criticality, Approve, banners in
  the existing banner pattern. No Roll back (upgrade only).
- **Sync tab:** a "New firmware versions" row (Wait for approval by default, or Use
  automatically). Optionally reuse the driver "Wait for testing" flow (sets in testing, chosen per
  device by Operators). Undecided.
- **Models tab:** status "Firmware waiting for approval"; optional Firmware column.
- **Coupled Devices / Start Imaging:** each device shows "01.18.00 to 01.21.00" or "Up to date";
  Operators can skip the update per device; the Start Imaging overlay adds a firmware line
  ("2 devices update firmware at restart, 1 is up to date").
- **Deployment timeline:** new optional step "Update firmware" with the outcomes above.
- **Settings:** where the SPM signing key reference (Key Vault key name) is configured. Needs
  an Administrator only surface and a Bicep change for the Key Vault role assignment.
- Reuse Downloads, Storage, Reports and the USB cache (SHA-256 keyed) for BIN files.

## Open questions (to settle with a spike)

1. Can Core produce a payload the BIOS accepts, signed with a Key Vault key in .NET? Byte for
   byte match with CMSL **proven offline** (see "Desk spike results"). Remaining: flash a real
   device with a payload signed by a Key Vault key.
2. Does `Update-HPFirmware -Offline` work in **our** WinPE once StorageWMI, SecureBootCmdlets and
   CMSL are added? (No source says the HP WinPE driver pack is needed.)
3. Which files does `-Offline` write to the ESP (decided inside `dfmbios64.dll`)? Not needed if
   we stage as the last WinPE step, after `bcdboot`, so nothing touches the ESP afterwards.
   Confirm on hardware that the Windows boot entry survives the flash.
4. ~~With Sure Admin on, is an unauthorized flash refused?~~ Decided by "BIOS Update Credential
   Policy" (see "Test device findings"). Does the BIOS flash a staged update on battery?
5. Do HP's CMSL licence terms allow bundling the modules into our boot image? **No** (EULA forbids
   distribution). Decided: Media Builder downloads it after the operator accepts HP's licence (see
   "CMSL licence").
6. Nonce strategy: time based from Core is fine for single use payloads; confirm the BIOS accepts
   it when the device clock is wrong (the nonce is checked against the BIOS counter, not the
   device clock).
7. Which BCU settings matter for update policy on the user's hardware (see "Inputs needed").

## Inputs needed from the user

- ~~BIOS settings and Sure Admin state from a Sure Admin device~~ (done, see "Test device
  findings"; signing key is in an HP Sure Admin KMS in Azure).
- Output of `Test-HPAuthRequired -BiosUpdateType Upgrade` on the test device (expected `False`).
- ~~Decision: capsule injection or CMSL in WinPE for version 1.~~ CMSL in WinPE (2026-11-14).
- A second HP model (ideally Intel) and later a Dell one.

## Next steps

1. Write the spec (new feature folder under `specs/`), including the portal mockup under
   `docs/design/` (Firmware view of the model page, Coupled Devices firmware line).
2. Build: HP catalog reader, Media Builder boot image changes (components, CMSL download with
   licence acceptance), Client step, portal.
3. Hardware test on the EliteBook 8 G1a (open questions 2 to 4), done by the user later.
4. Later phase: Sure Admin payload signing (open questions 1 and 6), BIOS password support.

## Related

- [os-image-driver-injection.md](os-image-driver-injection.md): driver feature; firmware reuses its
  catalog, storage, approval and testing patterns.
- `specs/005-os-driver-management/spec.md` FR-003 (firmware exclusion).
- Repo memory note `/memories/repo/firmware-research.md` (short version of the findings above).
