# Contract: Media Builder Gates

**Feature**: [../spec.md](../spec.md) | **Model**: [../data-model.md](../data-model.md) (section 5)

Roles come from the Media Builder registration's ID token, expanded by `MediaBuilderRoles.Expand`.
Every Operator API call still needs `CloudImaging.MediaBuilderAccess` on the Operator API
registration; that requirement is unchanged and independent of the gates below (FR-030).

**Legend**: Adm, Ops, Tech as in the Portal contracts; "None" = signed in with no Media Builder role.

---

## Gates

| # | Gate | Code | Allowed before | Allowed after |
|---|---|---|---|---|
| 1 | Generate Boot Image role gate (Home tile, nav item, Continue) | `ShellViewModel.IsGenerateBootImageAvailable`, `OperationSelectionViewModel.IsGenerateBootImageAvailable` | Adm | Adm, Ops |
| 2 | Boot media certificate check runs at shell start | `ShellViewModel` constructor | Adm | Adm, Ops |
| 3 | Pre-production boot images listed in Prepare USB Storage Device | `PrepareStorageDeviceViewModel.VisibleBootImages` | Adm | Adm, Ops |
| 4 | Prepare USB Storage Device reachable | `IsPrepareUsbAvailable` (`HasMediaBuilderRole`) | Adm, Tech | Adm, Ops, Tech |
| 5 | No-role state (both workflows blocked) | `HasNoRole` | not Adm and not Tech | not Adm, not Ops and not Tech |

Unchanged: the ADK gate and the certificate status gate (Not configured, Access denied, Check
failed) and their messages. Reason precedence stays: no role, role, ADK, Operator API access,
certificate check failed, certificate missing.

Gate 2 matters: `_certificateStatus` starts as `Configured`, and the check is skipped for any user
it is not triggered for. Opening gate 1 without gate 2 would offer generation to an Operations user
with no certificate configured (US3 AS3).

## Outcome per user

| User | Generate Boot Image | Prepare USB | Pre-production images |
|---|---|---|---|
| Adm, ADK, certificate configured | Available | Available | Listed |
| Ops, ADK, certificate configured | Available | Available | Listed |
| Ops, no ADK | ADK reason | Available | Listed |
| Ops, certificate missing, refused or unverifiable | Existing certificate reason | Available | Listed |
| Tech | Restricted by role: Operations or Administrator required | Available | Not listed |
| None | No-role reason naming Technician, Operations or Administrator | Blocked | n/a |
| Ops on Media Builder, no Ops or Adm on Portal | Generates locally; Portal upload refused with 403 | Available | Listed |

## View model API changes

| Member | Before | After |
|---|---|---|
| `EntraAuthenticationService.HasMediaBuilderRole` | `IsAdministrator \|\| IsTechnician` | expanded set contains Technician |
| `EntraAuthenticationService.CanGenerateBootImage` | n/a | **new**: expanded set contains Operations |
| `EntraAuthenticationService.CanSeePreProductionBootImages` | n/a | **new**: expanded set contains Operations |
| `OperationSelectionViewModel(..., bool isAdministrator, ...)` | parameter `isAdministrator` | parameter `canGenerateBootImage` |
| `PrepareStorageDeviceViewModel.VisibleBootImages(images, bool isAdministrator)` | parameter `isAdministrator` | parameter `canSeePreProduction` |
| `ShellViewModel.IsAdministrator` | gate input | replaced by `CanGenerateBootImage` |

## User-facing strings

| Location | Before | After |
|---|---|---|
| `OperationSelectionViewModel.GenerateBootImageUnavailableReason` (no role) | `Requires the Technician or Administrator role.` | `Requires the Technician, Operations or Administrator role.` |
| `OperationSelectionViewModel.GenerateBootImageUnavailableReason` (role) | `Requires the Administrator role.` | `Requires the Operations or Administrator role.` |
| `OperationSelectionViewModel.PrepareUsbUnavailableReason` | `Requires the Technician or Administrator role.` | `Requires the Technician, Operations or Administrator role.` |
| `ShellViewModel.GenerateBootImageUnavailableReason` (no role) | `Generate Boot Image requires the Technician or Administrator role.` | `Generate Boot Image requires the Technician, Operations or Administrator role.` |
| `ShellViewModel.GenerateBootImageUnavailableReason` (role) | `Generate Boot Image requires the Administrator role.` | `Generate Boot Image requires the Operations or Administrator role.` |
| `ShellViewModel.PrepareUsbUnavailableReason` | `Prepare USB Device requires the Technician or Administrator role.` | `Prepare USB Device requires the Technician, Operations or Administrator role.` |
| `OperationSelectionView.xaml` Generate tile caption (role) | `Requires the Administrator role` | `Requires the Operations or Administrator role` |
| `OperationSelectionView.xaml` Generate tile caption (no role) | `Requires the Technician or Administrator role` | `Requires the Technician, Operations or Administrator role` |
| `OperationSelectionView.xaml` Prepare tile caption (no role) | `Requires the Technician or Administrator role` | `Requires the Technician, Operations or Administrator role` |
| `OperationSelectionView.xaml` no-role banner | `... assign you the Technician role to prepare USB media, or the Administrator role to also generate boot images.` | `... assign you the Technician role to prepare USB media, or the Operations role to also generate boot images.` |
| `OperationSelectionView.xaml` role banner | `Administrator role required.` / `Generating boot images is restricted to the Administrator role. Ask your Cloud Imaging administrator to grant it if you need access.` | `Operations role required.` / `Generating boot images requires the Operations or Administrator role. Ask your Cloud Imaging administrator to assign it.` |
| `OperationSelectionView.xaml` Operator API denied banner | `... granted separately from your Technician or Administrator role ...` | `... granted separately from your Technician, Operations or Administrator role ...` |
| `OperatorApiErrorDescription.AccessDenied` | same phrase as above | same change |
| `PrepareStorageDeviceViewModel` empty-list status | `No production boot images are available yet. An Administrator must promote one in the portal.` | `No production boot images are available yet. An Operations or Administrator user must promote one in the portal.` |
| `PrepareStorageDeviceViewModel` boot image list label | `v{Version} \u00B7 {Architecture} \u00B7 Wi-Fi ({Size}){Stage}` (middle dots) | `v{Version}, {Architecture}, Wi-Fi ({Size}){Stage}` |

15 occurrences. All ASCII punctuation, no requirement or task identifiers (FR-039, FR-040). The
middle dot is written as its escape here so this contract stays free of the character. No Media
Builder test asserts on the boot image label (searched `tests/CloudImaging.MediaBuilder.Tests`).
