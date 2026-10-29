# Drivers

Cloud Imaging can install the vendor drivers for each device model while it deploys Windows. You
onboard a model once, Imaging Core keeps its driver set up to date from the vendor's catalog, and
every device of that model gets the set that was current when imaging started.

HP and Dell business models are supported, for x64 and ARM64. Open **Drivers** in the portal.

## How a device gets its drivers

1. When imaging starts, Imaging Core matches the device to an onboarded model (HP by baseboard
   system ID, Dell by system SKU) for the Windows release set on the OS image, and pins the
   model's current driver set on the session. Later changes do not affect that deployment.
2. After **Apply Windows image**, the Client runs **Install drivers**: per component it copies the
   file from the USB drive or downloads it, checks its SHA-256, extracts it and adds the drivers
   to the installed Windows with DISM. Components that install an application are handed to
   Windows setup and run during the Specialize pass.
3. A device without a matching model, an OS image without a Windows release, or a release the
   model is not onboarded for images without drivers. The step shows why.

Set the Windows release on every OS image (OS Images, edit the image). Without it no device gets
drivers from that image.

## Onboarding a model

1. **Drivers** > **Models** > **Onboard model**. Choose the vendor, search the vendor's model list
   and select one or more models.
2. Choose the Windows releases and press **Continue**. The preview shows what each set would
   contain and its size. Nothing is downloaded yet.
3. Press **Onboard**. The models are drafts: they match no device. Review each model's
   components, then press **Complete onboarding**. Imaging Core downloads the files and builds
   driver set 1. The model matches devices once its first set is ready.

Turning on sync for a vendor happens when you complete onboarding of its first model.

## Components and actions

The model page lists every component of the vendor's catalog for the chosen Windows release.
Each component has an action:

| Action | What the device does |
|---|---|
| Offline | Drivers are added to the installed Windows before it starts. |
| Offline and Specialize | Drivers are added offline, and the vendor's installer runs during Specialize to add its application. |
| Specialize | The vendor's installer runs during Specialize. |
| Skip | Not installed. |

Imaging Core picks the action from the component's category and the rules on the **Rules** tab.
Change one component with its action menu; that override wins over every rule and is kept when
the vendor publishes a new version. A component the vendor no longer publishes is shown as
withdrawn until you remove it.

## Approvals

With **Wait for approval of new components** turned on (**Sync** tab), a component that appears
in the vendor's catalog for the first time is not added to the set until an Operator approves it
on the model page. Updated versions of approved components are not held.

## Testing new driver sets

Set **Newer versions** on the **Sync** tab to **Wait for testing** to try new driver sets on a
few devices first. Every new set, including a new model's first, then waits in testing once its
files are ready: devices keep the current set, or get no drivers when the model has none yet.

- Operators and Administrators pick **<model> (testing)** in the **Driver set** column of
  **Coupled Devices** for the devices that should get it. Technicians do not see sets in testing.
- The model page shows which set is in testing. **Release** makes it current for every device,
  with the previous set kept for **Roll back**.
- A newer set that arrives while one is in testing replaces it, so testing starts again.
- Switching **Newer versions** back to **Use automatically** releases every set in testing. The
  portal asks first and says how many sets that releases.
- The **Models** tab shows **In testing** for such a model, and **Storage** counts and lists sets
  in testing.

## Sync

Imaging Core reads the vendor catalogs on the schedule set on the **Sync** tab, and on **Sync
now** or **Check for updates**. For each model and release it merges the catalog into the
components, downloads new files, verifies them against the vendor's hashes and builds a new set
when anything changed. The **Sync** tab lists every run and what it changed.

While a sync or check runs, a card at the top of the **Sync** tab shows how far it got: vendor
catalogs read, models checked, components downloaded and driver sets checked, each with its own
count, plus what it found so far and the latest changes. The totals grow while the sync finds
work. When it ends, the card shows the result until you dismiss it.

**Reports** > **Driver Synchronization** covers the last 90 days: how many synchronizations ran
and how many completed, how long they took, the components they changed per week, what they
downloaded, and the components that keep failing. Open a synchronization for its vendor catalogs,
downloads and changes per model.

A new set becomes current only after all its files are verified. A file that cannot be downloaded
keeps the previous version in the set, or leaves the component out when there is none; the run
reports it as failed and the next sync tries again.

## Downloads and Storage

- **Downloads** lists the sets being prepared, with progress per file, and the finished ones.
- **Storage** shows the space the driver files use per vendor and model, and how many previous
  driver sets are kept per model and release (0 to 3). Files no set or session uses are removed
  at the next clean-up.

## Rolling back

- **A driver set**: on the model page, **Roll back driver set** makes the previous set current
  again. Devices get it from their next deployment. The rolled-back set is not built again until
  something changes.
- **One component**: open the component, choose an earlier version under **Version history** and
  roll back until a newer version is published, or until you undo it. A version that is no
  longer stored is downloaded again from the vendor first.

## Custom components

**Add custom component** on the model page uploads your own package, for example a driver the
vendor does not publish or a fixed build of one it does.

- A ZIP with INF files is added offline. Unsigned drivers are not forced: sign them, or Windows
  setup refuses them.
- An installer runs during Specialize with the command you enter.
- A custom component can replace a vendor component on a model. The vendor component is skipped
  until you remove the custom one, and the model page shows a notice when the vendor publishes a
  newer version of what it replaces.

## Driver files on the USB drive

The Client keeps every verified driver file on the USB drive's CACHE partition and reuses it for
the next device of the same model, so only the first device downloads it. The timeline shows how
many files came from the USB drive. Files unused for 30 days are removed.

**Media Builder** can preload driver sets when it prepares a USB drive: under **Driver sets**,
select the models and Windows releases. Media Builder downloads the files first, then copies them
to the CACHE partition. Preloading pins nothing: a device still gets the set that is current when
its imaging starts, and only downloads what is not on the drive. Keep at least 20 GB free for OS
images.

## ARM64

ARM64 models are onboarded like x64 models and only match ARM64 OS images. Vendor packages that
cannot be extracted on ARM64 are staged and run during Specialize instead.

## Choosing a device's driver set when imaging starts

On **Devices**, the **Driver set** column shows the model each selected device matches for the
chosen OS image. Anyone who can start imaging can choose another model, or no drivers, for a
device before pressing **Start imaging**. The deployment records who chose it.

## Network access

Imaging Core must reach these hosts over HTTPS:

- `hpia.hpcloud.hp.com`, `ftp.hp.com`, `ftp.ext.hp.com` (HP)
- `downloads.dell.com`, `dl.dell.com` (Dell)

Devices download driver files from the deployment's storage account, never from the vendors.

## Not yet available

Holding a driver set for test deployments before it reaches every device is planned for a later
version.
