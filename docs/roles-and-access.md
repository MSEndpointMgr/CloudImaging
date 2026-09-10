# Cloud Imaging Roles & Access Reference

**Audience**: IT administrators assigning access, and technicians/administrators using the Portal or Media Builder

This page is the single reference for **who can do what** in Cloud Imaging. For the mechanics of
*creating* the app registrations and roles, see [setup-instructions.md](setup-instructions.md).

---

## 1. The five app roles

Cloud Imaging defines five Entra ID app roles across its three app registrations
(see [setup-instructions.md, Phase 1, Step 1](setup-instructions.md#step-1-create-three-app-registrations)).
Three are **user-facing** (you assign them to people); two are **service-facing** (assigned to an
app/identity, not a person).

| Role | Defined on | Assigned to | Purpose |
|---|---|---|---|
| `CloudImaging.Administrator` | **Cloud Imaging Portal** *and* **Cloud Imaging Media Builder** registrations | Users / groups | Full access: catalog writes, branding, configuration, boot-media certificate, plus everything Technician can do |
| `CloudImaging.Technician` | **Cloud Imaging Portal** *and* **Cloud Imaging Media Builder** registrations | Users / groups | Day-to-day imaging operations: sessions, coupling, assignment, read-only catalogs |
| `CloudImaging.Reader` | **Cloud Imaging Portal** registration only | Users / groups | Read-only visibility: Dashboard summary and Reports, nothing else. No Media Builder equivalent. |
| `CloudImaging.PortalAccess` | **Cloud Imaging Operator API** registration | The Portal backend's **managed identity** only (never a person) | Lets the Portal backend call the Operator API on the signed-in user's behalf. Assigned automatically by `assign-service-roles.ps1`; nothing to do manually. |
| `CloudImaging.MediaBuilderAccess` | **Cloud Imaging Operator API** registration | Users / groups | Lets a signed-in technician's Media Builder client actually call the Operator API. Required **in addition to** `CloudImaging.Administrator`/`Technician`; see [Phase 3, Step 2](setup-instructions.md#step-2-assign-access-to-your-administrators-and-technicians). |

> **Because the Portal and Media Builder are separate app registrations, `Administrator`/`Technician`
> must be assigned separately on each one**: assigning a user Administrator on the Portal does
> **not** automatically grant them anything on the Media Builder registration, and vice versa. A
> technician who only ever uses the Portal doesn't need a Media Builder assignment at all.

### How to assign `Administrator` / `Technician` / `Reader` to a user or group

For **each** registration the person needs (Portal, Media Builder, or both; `Reader` only exists
on the Portal registration):

1. Entra ID → **Enterprise applications** → select **Cloud Imaging Portal** (or **Cloud Imaging
   Media Builder**) → **Users and groups** → **Add user/group**
2. Select the user or group, choose the role (`CloudImaging.Administrator`,
   `CloudImaging.Technician`, or, Portal only, `CloudImaging.Reader`), and click **Assign**

A user with **no role assigned** on a registration can sign in, but the app shows an
"Access denied" screen (Portal) or blocks every workflow (Media Builder); the identity check and
the authorization check are separate steps.

### How to assign `MediaBuilderAccess` (Media Builder technicians only)

Entra ID → **Enterprise applications** → **Cloud Imaging Operator API** → **Users and groups** →
**Add user/group** → assign `CloudImaging.MediaBuilderAccess`. See
[setup-instructions.md, Phase 3, Step 2](setup-instructions.md#step-2-assign-access-to-your-administrators-and-technicians)
for why this is required in addition to the Media Builder registration's own
`Administrator`/`Technician` role.

`PortalAccess` requires no manual assignment; `assign-service-roles.ps1` (run once during
[Phase 3, Step 1](setup-instructions.md#step-1-run-the-post-deployment-scripts)) grants it to the
Portal backend's managed identity.

---

## 2. What each role can do in the Portal

| Area | No role | Reader | Technician | Administrator |
|---|---|---|---|---|
| Sign in | Allowed, but "Access denied" screen | ✅ | ✅ | ✅ |
| **Dashboard**: Completed Sessions summary | ❌ | ✅ (only stat shown) | ✅ (full) | ✅ (full) |
| **Reports**: session outcomes, image inventory, failure detail | ❌ | ✅ | ❌ | ✅ |
| **Sessions**: view, couple, single-assign, bulk-assign | ❌ | ❌ | ✅ | ✅ |
| **OS Images** catalog: view | ❌ | ❌ | ✅ (read-only) | ✅ |
| **OS Images** catalog: upload / edit / delete | ❌ | ❌ | ❌ | ✅ |
| **Boot Images** / **Recovery Images** catalog: view | ❌ | ❌ | ✅ (read-only) | ✅ |
| **Boot Images** / **Recovery Images** catalog: upload / edit / delete | ❌ | ❌ | ❌ | ✅ |
| **Locations**: select a location (Header account menu picker, Devices filter) | ❌ | ❌ | ✅ | ✅ |
| **Locations** catalog page: add / remove locations | ❌ | ❌ | ❌ | ✅ |
| **Branding**: view / edit logo | ❌ | ❌ | ❌ | ✅ |
| **Configuration** page (pre-flight authorization toggle, SAS/token expiry, boot media certificate generate/rotate/view) | ❌ | ❌ | ❌ | ✅ |

The Technician role's read access to Sessions/Images is what makes day-to-day imaging operations
possible without granting catalog or configuration changes. Administrator is a superset of
Technician: there is no capability a Technician has that Administrator lacks. Reader is **not**
a subset of Technician or a superset of "no role" in the same hierarchical sense; it's a
separate, narrow lane: full Reports access, but none of Technician's Sessions/Images visibility.

Locations is deliberately split into two separate capabilities: reading the catalog (needed by
the Header account menu's "My location" picker, the Devices filter, and the Media Builder USB
prep screen) is available to any Technician or Administrator, while managing the catalog itself
(the `/locations` admin page: adding or removing entries) is Administrator-only.

> **Implementation note**: the Portal backend expands roles through an implication hierarchy
> (`Administrator` ⟹ `Technician` ⟹ `PortalAccess`) so that any signed-in user satisfies the
> internal `PortalAccess` check used on read routes, while writes stay gated on the real
> `Administrator` role. You never assign `PortalAccess` to a person; it's a byproduct of holding
> `Administrator` or `Technician`, used only for the Portal backend's own authorization plumbing.
> `CloudImaging.Reader` is deliberately **outside** this chain: it never implies `PortalAccess`,
> and is instead allowlisted explicitly on only the handful of read routes Reports needs
> (session history, and the OS/boot/recovery image catalogs for the Image Inventory report). A
> Reader's token is rejected by every other route, including `/api/sessions`, even if called
> directly, not just hidden from the navigation menu.

---

## 3. What each role can do in the Media Builder

| Workflow | Technician | Administrator |
|---|---|---|
| **Prepare USB Storage Device** | ✅ | ✅ |
| **Generate Boot Image** | ❌ (nav item disabled, with a "restricted by role" explanation) | ✅ |

Generate Boot Image is Administrator-only because it embeds the active mTLS boot-media
certificate and branding into a new image, a higher-privilege operation than deploying an
already-published boot image to a USB drive.

Two independent gates control whether **Generate Boot Image** is available, and the Media Builder
always shows the specific reason that applies:

1. **Role gate**: signed-in user must hold `CloudImaging.Administrator` (Portal role naming reused
   on the Media Builder registration, same role names, separate assignment; see [§1](#1-the-five-app-roles)).
2. **ADK gate**: the Windows ADK + WinPE add-on must be installed on the workstation (see
   [setup-instructions.md](setup-instructions.md#installing-the-windows-adk-on-technician-workstations)).

`Prepare USB Storage Device` has neither gate beyond sign-in; any signed-in Technician or
Administrator with `CloudImaging.MediaBuilderAccess` on the Operator API can use it, including
reading the location catalog to optionally tag the USB with a site label.

> **Both roles still need `CloudImaging.MediaBuilderAccess`** on the Operator API enterprise
> application to make any API call from the Media Builder succeed; this is separate from, and in
> addition to, the `Administrator`/`Technician` role that controls which *workflow* is visible. A
> Technician missing `MediaBuilderAccess` can sign in and see the Prepare USB workflow, but calls to
> the Operator API (e.g. listing boot images) return `403`.

---

## 4. Quick troubleshooting

| Symptom | Likely cause |
|---|---|
| Portal shows "Access denied" after sign-in | No `Administrator`/`Technician`/`Reader` role assigned on the **Cloud Imaging Portal** enterprise application |
| Portal loads, but Branding/Configuration pages are missing or writes return 403 | Signed in as Technician or Reader, not Administrator |
| Reader signed in but Sessions/OS Images/Boot Images/Locations are missing from the nav and Dashboard | Expected: Reader is scoped to Dashboard + Reports only, by design |
| Technician can't reach the Locations page (redirected to Dashboard) | Expected: managing the Locations catalog is Administrator-only; the Header account menu's "My location" picker still works for Technician |
| Media Builder sign-in succeeds but every API call returns 403 | Missing `CloudImaging.MediaBuilderAccess` on the **Cloud Imaging Operator API** enterprise application |
| Media Builder shows "restricted by role" on Generate Boot Image | Signed in as Technician; only Administrators can generate boot images |
| Media Builder shows an ADK-related message on Generate Boot Image | Role is fine; the Windows ADK/WinPE add-on isn't installed (or the two are mismatched versions) on this workstation |

---

*For app registration setup and initial role creation, see
[setup-instructions.md](setup-instructions.md). For operational issues, see
[operations-runbook.md](operations-runbook.md).*
