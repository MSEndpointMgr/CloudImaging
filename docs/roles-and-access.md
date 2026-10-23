# Cloud Imaging Roles & Access Reference

**Audience**: IT administrators assigning access, and technicians/administrators using the Portal or Media Builder

This page is the single reference for **who can do what** in Cloud Imaging. For the mechanics of
*creating* the app registrations and roles, see [setup-instructions.md](setup-instructions.md).

---

## 1. The seven app roles

Cloud Imaging defines seven Entra ID app roles across its three app registrations
(see [setup-instructions.md, Phase 1, Step 1](setup-instructions.md#step-1-create-three-app-registrations)).
Five are **user-facing** (you assign them to people); two are **service-facing** (assigned to an
app/identity, not a person). `CloudImaging.AutopilotApprover` is optional.

| Role | Defined on | Assigned to | Purpose |
|---|---|---|---|
| `CloudImaging.Administrator` | **Cloud Imaging Portal** *and* **Cloud Imaging Media Builder** registrations | Users / groups | Full access: everything Operator can do, plus branding, configuration (including Autopilot settings), the boot-media certificate, pre-flight overrides and the update check |
| `CloudImaging.Operator` | **Cloud Imaging Portal** *and* **Cloud Imaging Media Builder** registrations | Users / groups | Day-to-day operational administration: OS, boot and recovery image catalogs, Locations, Wi-Fi networks, Autopilot decisions, every report, and Generate Boot Image in the Media Builder, plus everything Technician can do. No security or tenant-wide settings. |
| `CloudImaging.Technician` | **Cloud Imaging Portal** *and* **Cloud Imaging Media Builder** registrations | Users / groups | Day-to-day imaging operations: sessions, coupling, assignment, read-only catalogs |
| `CloudImaging.Reader` | **Cloud Imaging Portal** registration only | Users / groups | Read-only visibility: Dashboard summary and Reports, nothing else. No Media Builder equivalent. |
| `CloudImaging.AutopilotApprover` *(optional)* | **Cloud Imaging Portal** registration only | Users / groups | Approve or reject Autopilot registrations and view the history report. Nothing else; an approver without another role sees only the Autopilot page and its report. Operator and Administrators can approve without it. |
| `CloudImaging.PortalAccess` | **Cloud Imaging Operator API** registration | The Portal backend's **managed identity** only (never a person) | Lets the Portal backend call the Operator API on the signed-in user's behalf. Assigned automatically by `post-install.ps1`; nothing to do manually. |
| `CloudImaging.MediaBuilderAccess` | **Cloud Imaging Operator API** registration | Users / groups | Lets a signed-in user's Media Builder client actually call the Operator API. Required **in addition to** the Media Builder registration's `Administrator`, `Operator` or `Technician` role; see [Phase 3, Step 2](setup-instructions.md#step-2-assign-access-to-your-administrators-and-technicians). |

The three hierarchical roles nest: `Administrator` implies `Operator`, which implies
`Technician`. Assign each person only the highest role they need; holding a lower one as well
changes nothing. `Reader` and `AutopilotApprover` sit outside the hierarchy: Operator includes
every report and every Autopilot decision in its own right, without `Reader` or
`AutopilotApprover`, and neither of those roles implies Operator.

> **Because the Portal and Media Builder are separate app registrations, `Administrator`,
> `Operator` and `Technician` must be assigned separately on each one**: assigning a user
> Operator on the Portal does **not** automatically grant them anything on the Media Builder
> registration, and vice versa. Someone who only ever uses the Portal doesn't need a Media Builder
> assignment at all.

### How to assign a user role to a user or group

Prefer assigning a **group per persona** rather than individual users. A single person needs
assignments across up to three enterprise applications, and per-user assignment is where access
problems start. [Phase 3, Step 2](setup-instructions.md#step-2-assign-access-to-your-administrators-and-technicians)
walks through the group-based approach and lists every application/role pair in one table.

For **each** registration the person needs (Portal, Media Builder, or both; `Reader` and
`AutopilotApprover` only exist on the Portal registration):

1. Entra ID > **Enterprise applications** > select **Cloud Imaging Portal** (or **Cloud Imaging
   Media Builder**) > **Users and groups** > **Add user/group**
2. Select the user or group, choose the role (`CloudImaging.Administrator`,
   `CloudImaging.Operator`, `CloudImaging.Technician`, or, Portal only, `CloudImaging.Reader` or
   `CloudImaging.AutopilotApprover`), and click **Assign**
3. Have the person sign out and back in. Roles are read at sign-in, so an existing session keeps
   its previous access until then.

`CloudImaging.Operator` is only offered once it exists on the registration. Tenants deployed
before it existed add it by hand first:
[upgrade-instructions.md, Add the Operator role](upgrade-instructions.md#add-the-operator-role).

A user with **no role assigned** on a registration can sign in, but the app shows an
"Access denied" screen (Portal) or blocks every workflow (Media Builder); the identity check and
the authorization check are separate steps.

### How to assign `MediaBuilderAccess` (Media Builder users only)

Entra ID > **Enterprise applications** > **Cloud Imaging Operator API** > **Users and groups** >
**Add user/group** > assign `CloudImaging.MediaBuilderAccess`. See
[setup-instructions.md, Phase 3, Step 2](setup-instructions.md#step-2-assign-access-to-your-administrators-and-technicians)
for why this is required in addition to the Media Builder registration's own
`Administrator`, `Operator` or `Technician` role.

`PortalAccess` requires no manual assignment; `post-install.ps1` (run once during
[Phase 3, Step 1](setup-instructions.md#step-1-complete-the-microsoft-entra-grants)) grants it to the
Portal backend's managed identity.

---

## 2. What each role can do in the Portal

| Area | No role | Reader | Technician | Operator | Administrator |
|---|---|---|---|---|---|
| Sign in | Allowed, but "Access denied" screen | ✅ | ✅ | ✅ | ✅ |
| **Dashboard**: Completed Sessions summary | ❌ | ✅ (only stat shown) | ✅ (full) | ✅ (full) | ✅ (full) |
| **Reports**: device outcomes, location statistics, image inventory, failure detail | ❌ | ✅ | ❌ | ✅ | ✅ |
| **Sessions**: view, couple, single-assign, bulk-assign | ❌ | ❌ | ✅ | ✅ | ✅ |
| **Devices**: approve or revoke a pre-flight override | ❌ | ❌ | ❌ | ❌ | ✅ |
| **OS Images** catalog: view | ❌ | ❌ | ✅ (read-only) | ✅ | ✅ |
| **OS Images** catalog: upload / edit / delete | ❌ | ❌ | ❌ | ✅ | ✅ |
| **Boot Images** / **Recovery Images** catalog: view | ❌ | ❌ | ✅ (read-only) | ✅ | ✅ |
| **Boot Images** / **Recovery Images** catalog: upload / edit / delete | ❌ | ❌ | ❌ | ✅ | ✅ |
| **Boot Images**: promote a tested pre-production image to production, or demote one back | ❌ | ❌ | ❌ | ✅ | ✅ |
| **Locations**: select a location (Header account menu picker, Devices filter) | ❌ | ❌ | ✅ | ✅ | ✅ |
| **Locations** catalog page: add / remove locations, edit country-to-region mapping | ❌ | ❌ | ❌ | ✅ | ✅ |
| **Wi-Fi Networks**: view, add, edit, reorder, delete, assign to locations | ❌ | ❌ | ❌ | ✅ | ✅ |
| **Branding**: view / edit logo | ❌ | ❌ | ❌ | ❌ | ✅ |
| **Configuration** page (pre-flight authorization toggle, SAS/token expiry, boot media certificate generate/rotate/view, update check and notice) | ❌ | ❌ | ❌ | ❌ | ✅ |

**Autopilot** registrations are a separate lane:

| Area | Reader | Technician | Operator | Autopilot Approver | Administrator |
|---|---|---|---|---|---|
| **Autopilot**: view the approval queue | ❌ | ✅ (read-only) | ✅ | ✅ | ✅ |
| **Autopilot**: approve, reject, retry a failed import, change the group tag of a device already in Autopilot | ❌ | ❌ | ✅ | ✅ | ✅ |
| **Reports** > **Autopilot Registration History**: handled requests, CSV export | ✅ | ❌ | ✅ | ✅ | ✅ |
| **Configuration** > **Autopilot**: turn the feature on, manage group tags, set retention | ❌ | ❌ | ❌ | ❌ | ✅ |

`CloudImaging.AutopilotApprover` sits outside the role hierarchy, like Reader. A user who holds only
that role lands on the Autopilot page and sees no other part of the Portal except the history
report, which they open from the Autopilot page. An Operator or Administrator decision is
recorded with the deciding user, exactly like an approver's.

The Technician role's read access to Sessions/Images is what makes day-to-day imaging operations
possible without granting catalog or configuration changes. Each hierarchical role is a superset
of the one below it: there is no capability a Technician has that Operator lacks, and none that
Operator has that Administrator lacks. Operator adds catalog, Locations and Wi-Fi management,
Autopilot decisions and every report, and stops short of everything that changes security or
tenant-wide settings: Branding, Configuration (including Autopilot settings and the boot media
certificate), pre-flight overrides and the update check. Operator includes every report and
every Autopilot decision without `Reader` or `AutopilotApprover`; adding either to an Operator
user changes nothing. Reader is **not** a subset of Technician or a superset of "no role" in the
same hierarchical sense; it's a separate, narrow lane: full Reports access, but none of
Technician's Sessions/Images visibility.

Reports session data (device, location, pre-flight and failure detail for every finished session)
is refused to Technician tokens with `403`, even when the API is called directly rather than
through the Portal, which never shows Reports to Technicians. The Dashboard's Completed Sessions
stat, which Technicians do see, reads completion times only, from a separate route.

Locations is deliberately split into two separate capabilities: reading the catalog (needed by
the Header account menu's "My location" picker, the Devices filter, and the Media Builder USB
prep screen) is available to any Technician, Operator or Administrator user, while managing the
catalog itself (the `/locations` page: adding or removing entries) requires Operator or
Administrator.

Wi-Fi passwords are never returned to the Portal, for any role. Only two callers can read them:
Media Builder (`MediaBuilderAccess`; the Operator API refuses a Portal token on that route) while it
prepares USB media, and the Device Gateway for a device presenting the boot media certificate. Both
receive them encrypted to the boot media certificate, and Imaging Core audits every read with its
purpose.

> **Implementation note**: the Portal backend expands roles through an implication hierarchy
> (`Administrator` implies `Operator`, which implies `Technician`, which implies `PortalAccess`)
> so that any Technician, Operator or Administrator user satisfies the internal `PortalAccess`
> check used on read routes. Writes name the roles they accept: `Operator` or `Administrator`
> for catalog, Locations and Wi-Fi writes (plus `AutopilotApprover` for Autopilot decisions), and
> `Administrator` alone for the settings listed above. A refused request returns `403` naming the
> accepted roles. You never assign `PortalAccess` to a person; it's a byproduct of holding
> `Administrator`, `Operator` or `Technician`, used only for the Portal backend's own
> authorization plumbing. `CloudImaging.Reader` is deliberately **outside** this chain: it never
> implies `PortalAccess`, and is instead allowlisted explicitly on only the handful of read routes
> Reports and the Dashboard need (session history, completion times for the Completed Sessions
> stat, Autopilot Registration History, and the OS/boot/recovery image catalogs for the Image
> Inventory report). A Reader's token is
> rejected by every other route, including `/api/sessions`, even if called directly, not just
> hidden from the navigation menu.

---

## 3. What each role can do in the Media Builder

| Workflow | No role | Technician | Operator | Administrator |
|---|---|---|---|---|
| **Prepare USB Storage Device** | ❌ | ✅ (production boot images) | ✅ (production and pre-production boot images) | ✅ (production and pre-production boot images) |
| **Generate Boot Image** | ❌ | ❌ (nav item disabled, with a "restricted by role" explanation) | ✅ | ✅ |

A newly uploaded boot image starts in **pre-production**. Only Operator and Administrator users
see it in Prepare USB Storage Device, so they can test it on a device; it reaches Technicians, and
the self-update of existing USB devices, only once an Operator or Administrator user promotes it
in the Portal's Boot Images page. A USB device prepared with a pre-production image is never
self-updated back to the production image while the test image exists. The Operator API cannot
tell the Media Builder roles apart (all call it with `MediaBuilderAccess`), so hiding
pre-production images from Technicians is a workflow guard, not an access control.

A user with none of these roles can sign in but reaches no workflow at all: both the Home tiles
and the navigation items are disabled, and the Media Builder explains that the Technician,
Operator or Administrator role must be assigned. This matches what the Operator API would do
anyway, since every workflow calls it and a user with no role assignment gets a `403`.

Generate Boot Image requires Operator or Administrator because it embeds the active mTLS
boot-media certificate and branding into a new image, a higher-privilege operation than deploying
an already-published boot image to a USB drive.

The role gate is a **workflow guard in the Media Builder, not a server-side control**: the
Operator API cannot tell Technician, Operator and Administrator apart. What authorises
publishing is the Portal backend. A generated image reaches devices only after it is uploaded on
the Portal's Boot Images page, which requires Operator or Administrator on the **Portal**
registration. A user with Operator on the Media Builder registration only can generate an image
locally, but the Portal refuses the upload.

Three independent gates control whether **Generate Boot Image** is available, and the Media
Builder always shows the specific reason that applies:

1. **Role gate**: signed-in user must hold `CloudImaging.Operator` or
   `CloudImaging.Administrator` (Portal role naming reused on the Media Builder registration, same
   role names, separate assignment; see [section 1](#1-the-seven-app-roles)).
2. **ADK gate**: the Windows ADK + WinPE add-on must be installed on the workstation (see
   [setup-instructions.md](setup-instructions.md#installing-the-windows-adk-on-technician-workstations)).
3. **Certificate gate**: the Portal must have an active boot media certificate, and the Media
   Builder must be able to read it through the Operator API (see
   [setup-instructions.md, Phase 3, Step 3](setup-instructions.md#step-3-initial-portal-configuration)).

`Prepare USB Storage Device` has no ADK or certificate gate; any signed-in Technician, Operator
or Administrator user with `CloudImaging.MediaBuilderAccess` on the Operator API can use it,
including reading the location catalog to optionally tag the USB with a site label.

> **Every Media Builder role still needs `CloudImaging.MediaBuilderAccess`** on the Operator API
> enterprise application to make any API call from the Media Builder succeed; this is separate
> from, and in addition to, the `Administrator`, `Operator` or `Technician` role that controls
> which *workflow* is visible. A user missing `MediaBuilderAccess` can sign in and see their
> workflows, but calls to the Operator API (e.g. listing boot images) return `403`.

---

## 4. Quick troubleshooting

| Symptom | Likely cause |
|---|---|
| Portal shows "Access denied" after sign-in | No `Administrator`, `Operator`, `Technician`, `Reader` or `AutopilotApprover` role assigned on the **Cloud Imaging Portal** enterprise application |
| Approver sees requests but **Approve** is missing | Signed in as Technician, who can only view; assign `CloudImaging.AutopilotApprover` or `CloudImaging.Operator` |
| Portal loads, but Branding/Configuration pages are missing or writes return 403 | Signed in as Technician or Reader. Catalog, Locations and Wi-Fi writes need Operator or Administrator; Branding and Configuration need Administrator |
| Operator user cannot reach Branding or Configuration (missing from the navigation, redirected to the Dashboard by URL) | Expected: Branding, Configuration (including Autopilot settings and the boot media certificate), pre-flight overrides and the update check are Administrator-only |
| `CloudImaging.Operator` is not offered when assigning a user or group | The role was never added to that registration's **App roles**. Add it to the Portal and Media Builder registrations ([upgrade-instructions.md](upgrade-instructions.md#add-the-operator-role) on an existing tenant), then assign it |
| Newly assigned role has no effect | Roles are read at sign-in. Sign out of the Portal or Media Builder and back in |
| Reader signed in but Sessions/OS Images/Boot Images/Locations are missing from the nav and Dashboard | Expected: Reader is scoped to Dashboard + Reports only, by design |
| Technician can't reach the Locations or Reports page (redirected to Dashboard) | Expected: managing the Locations catalog needs Operator or Administrator, Reports needs Reader, Operator or Administrator; the Header account menu's "My location" picker still works for Technician |
| Media Builder sign-in succeeds but every API call returns 403 | Missing `CloudImaging.MediaBuilderAccess` on the **Cloud Imaging Operator API** enterprise application |
| Media Builder shows "restricted by role" on Generate Boot Image | Signed in as Technician; generating boot images needs Operator or Administrator on the **Cloud Imaging Media Builder** enterprise application |
| Media Builder shows an ADK-related message on Generate Boot Image | Role is fine; the Windows ADK/WinPE add-on isn't installed (or the two are mismatched versions) on this workstation |
| Generated boot image upload in the Portal returns 403 | Operator or Administrator is assigned on the Media Builder registration but not on the Portal registration |

---

*For app registration setup and initial role creation, see
[setup-instructions.md](setup-instructions.md). For operational issues, see
[operations-runbook.md](operations-runbook.md).*
