# TODO: Consistent release asset filenames

Status: **proposed**, not implemented. Needs a decision on the client compatibility window
before any of it is safe to ship.

## The problem

The three release streams name their assets three different ways, and two of them publish
unversioned filenames:

| Stream | Tag | Assets today |
| --- | --- | --- |
| Backend/IaC | `mse-ci-v1.0.0` | `cloud-imaging-mse-ci-v1.0.0.zip`, `SHA256SUMS` |
| Client | `mse-ci-client-v1.0.0` | `CloudImaging.Client.zip`, `SHA256SUMS` |
| Media Builder | `mse-ci-mediabuilder-v1.0.0` | `CloudImaging.MediaBuilder.msi`, `CloudImaging.MediaBuilder.zip`, `SHA256SUMS` |

Four separate issues:

1. Two naming styles: kebab-case `cloud-imaging-*` versus PascalCase-dotted `CloudImaging.*`.
2. The backend bundle embeds the whole tag, so the internal `mse-ci-` prefix leaks into a
   customer-facing filename and the product name effectively stutters.
3. The client and Media Builder assets carry **no version at all**. Once downloaded,
   `CloudImaging.Client.zip` is unidentifiable, two versions collide in the same folder, and
   support cannot tell which build a customer is actually running.
4. The bundle's internal component names are mixed too (`DeviceGatewayApi.zip` versus
   `portal-backend.zip`), though see [Explicitly out of scope](#explicitly-out-of-scope).

## Proposed structure

```
cloud-imaging[-<component>]-v<major>.<minor>.<patch>[-<prerelease>].<ext>
```

The asset name is the tag name minus the `mse-ci-` prefix, with `cloud-imaging` as the product
root, so an asset URL is derivable from a tag without a lookup table:

| Stream | Tag | Proposed assets |
| --- | --- | --- |
| Backend/IaC | `mse-ci-v1.0.0` | `cloud-imaging-v1.0.0.zip` |
| Client | `mse-ci-client-v1.0.0` | `cloud-imaging-client-v1.0.0.zip` |
| Media Builder | `mse-ci-mediabuilder-v1.0.0` | `cloud-imaging-mediabuilder-v1.0.0.msi`, `cloud-imaging-mediabuilder-v1.0.0.zip` |
| All | — | `SHA256SUMS` (unchanged) |

Pre-release tags fall out of the pattern unchanged: `cloud-imaging-v1.2.0-beta.1.zip`.

`SHA256SUMS` keeps its name. It is a well-known convention, it is scoped to one release, and
both consumers below look it up by that exact name.

Kebab-case is chosen over the `CloudImaging.Client-v1.0.0.zip` alternative because it matches
the existing tag names, the existing backend bundle, and the docs, and because it avoids
case-sensitivity ambiguity in URLs and shell scripts.

### Fixed-name copies on the alias releases

Versioning every filename costs the stable
`releases/latest/download/<name>` URL, because that endpoint requires a constant filename. So on
the `mse-ci-iac-latest` and `mse-ci-client-latest` alias releases **only**, also publish an
unversioned copy:

- `cloud-imaging.zip`
- `cloud-imaging-client.zip`

That restores a permanent documentation link and doubles as the compatibility hook below.

## Migration impact

This is the part that makes it more than a rename.

### Client rename breaks Media Builder installs in the field

[GitHubReleasesClient](../src/CloudImaging.MediaBuilder/Services/GitHubReleasesClient.cs) resolves
the `mse-ci-client-latest` alias and matches `ClientAssetName = "CloudImaging.Client.zip"` by exact
name, then verifies the download against the `SHA256SUMS` entry **for that same name**. Every
Media Builder already installed does this, and those copies are never going to be updated
retroactively. Renaming the asset outright breaks the "Automatic download" boot image source for
all of them.

Safe sequence:

1. Publish the versioned name as the primary asset.
2. Keep publishing `CloudImaging.Client.zip` on the alias release as a compatibility asset, listed
   in `SHA256SUMS` alongside the versioned name.
3. Update `GitHubReleasesClient` to prefer the versioned name and fall back to the legacy name, so
   new builds are not tied to the compatibility asset.
4. Drop the compatibility asset only at an announced version, once old Media Builder installs are
   assumed gone.

### Backend rename is safe

[update.ps1](../src/deploy/scripts/update.ps1) selects `cloud-imaging-*.zip`, preferring an exact
`cloud-imaging-<tag>.zip` match and falling back to the first wildcard hit. `cloud-imaging-v1.0.0.zip`
still matches the wildcard, so customer copies of older `update.ps1` keep working. The exact-match
branch should be updated to the new form so it stops relying on the fallback.

### Media Builder MSI/ZIP rename is safe

Nothing automated consumes either file; they are downloaded by hand from the release page.

## Explicitly out of scope

**Do not rename the bundle's internal components** (`DeviceGatewayApi.zip`, `OperatorApi.zip`,
`ImagingCoreApi.zip`, `portal-backend.zip`, `portal-frontend.zip`, `deploy.zip`). `update.ps1`
reads them by exact name, customers routinely run an older copy of `update.ps1` against a newer
bundle, and the names are never seen outside the archive. Renaming them breaks that combination
and buys nothing.

## Work required

- `release-iac.yml`: bundle name, and the fixed-name copy on the alias release.
- `release-client.yml`: asset name, compatibility asset on the alias release.
- `release-mediabuilder.yml`: MSI and ZIP names.
- `update.ps1`: exact-match asset name.
- `GitHubReleasesClient`: preferred name plus legacy fallback, and the tests covering it.
- `docs/setup-instructions.md` and `docs/upgrade-instructions.md`: example filenames.
