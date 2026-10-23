# Feature Specification: Operator Role

**Feature Branch**: `004-operator-role` (suggested name; no branch created yet)

**Created**: 2026-10-22

**Status**: Draft

**Input**: User description: "Introduce a new user app role `CloudImaging.Operator` between Technician and Administrator: Administrator implies Operator, Operator implies Technician (and therefore PortalAccess). Nobody loses access they have today; Reader and AutopilotApprover stay separate roles, but Operator grants the capabilities they offer. Operator can, in addition to Technician: upload, edit, delete OS, boot and recovery images and promote or demote boot images; all driver management edits (spec 005); manage the Locations catalog and Wi-Fi networks; Autopilot decisions and the Autopilot Registration History report; every report; Generate Boot Image in Media Builder and upload the result. Operator cannot: Configuration (pre-flight authorization, token and SAS expiry, boot media certificate), Branding, Autopilot settings, the partitioning scheme, the platform update check or pre-flight override decisions. Declared on the Portal and Media Builder registrations, assignable to users and groups, added manually as documented in the setup and upgrade instructions, enforced server-side everywhere."

## Clarifications

### Session 2026-10-22

- Q: Does Operator manage the Locations catalog and Wi-Fi networks? A: Yes, exactly as Administrator: create, update and delete locations, the country-to-region mapping, and view, add, edit, reorder, delete and assign Wi-Fi networks to locations. Wi-Fi passwords are still never returned to the Portal.
- Q: Does Operator get Autopilot decisions? A: Yes: approve, reject, retry a failed import, change the group tag of a device already in Autopilot, and the Autopilot Registration History report. Autopilot settings (feature switch, group tags, retention) stay Administrator-only.
- Q: Does Operator get Reports? A: Yes, every report Reader and Administrator see, including Autopilot Registration History. No Reader assignment is needed.
- Q: How does the role reach new and existing tenants? A: The administrator adds `CloudImaging.Operator` manually to the Cloud Imaging Portal and Cloud Imaging Media Builder registrations and assigns it, following the setup instructions (fresh installs) and the upgrade instructions and release notes (existing tenants). No script or template creates, verifies or reports the role.
- Q: Should the existing gap where Technician can read Reports data directly be fixed here? A: Yes. Reports-only data accepts only Reader, Operator and Administrator, and a Technician calling it directly is refused. The Technician Dashboard's Completed Sessions stat keeps working through a separate read that returns completion times only. Catalog lists shared with Technician pages stay as they are (FR-015a).
- Q: Where do release notes come from? A: From a file committed in the repository, one per release stream and version. The release workflows publish that file as the GitHub release body, so the Operator notice is written and reviewed like any other change (FR-034).
- Q: What about the banned characters already present in files this feature touches? A: Remove them. Documentation, user-facing text, log messages and comments in touched files contain no em dash, en dash, middle dot or arrow characters. Navigation paths are written as "Configuration > Autopilot" and the hierarchy with "implies" (FR-040).
- Q: Which roles does the deployment form tooltip for the Portal client ID list? A: All five user roles the Portal registration exposes: Administrator, Operator, Technician, Reader and AutopilotApprover, because the tooltip describes the registration. The Media Builder client ID tooltip lists no roles and stays unchanged (FR-035).
- Q: What is the role called? A: `CloudImaging.Operator` (display name Operator), matching the singular naming of the other user roles. It was first drafted as Operations.

## User Scenarios & Testing *(mandatory)*

### User Story 1 - Operator user runs day-to-day administration without security settings (Priority: P1)

An imaging lead is assigned Operator on the Portal. They upload a new OS image, correct its metadata, delete an obsolete recovery image, promote a tested pre-production boot image, adjust driver rules, add a location, assign a Wi-Fi network to it, approve pending Autopilot registrations and review reports. Branding and Configuration (including Autopilot settings) are not offered to them, and the server refuses those operations even when called directly.

**Why this priority**: This is the reason for the role. Today this work requires Administrator, which also hands out the boot media certificate, pre-flight authorization and tenant-wide settings. Operator covers day-to-day operational administration (content, locations, Wi-Fi, Autopilot approvals, reports, drivers) while security and tenant-wide settings (boot media certificate, pre-flight authorization, token lifetimes, branding, Autopilot settings, partitioning, update check) stay with Administrator. Operator users can manage Wi-Fi networks, whose passwords are encrypted to the boot media certificate and never returned to the Portal.

**Independent Test**: Assign only Operator on the Portal to a test user. Perform every catalog, location, Wi-Fi and Autopilot decision write and open every report in the Portal, then call every Administrator-only operation directly with that user's token and confirm each is refused and nothing changes.

**Acceptance Scenarios**:

1. **Given** a user holding only Operator on the Portal, **When** they sign in, **Then** the Dashboard, Devices, OS Images, Boot Images, Recovery Images, Locations, Wi-Fi Networks, Autopilot and Reports are available, Drivers is available once spec 005 ships, and Branding and Configuration are absent from the navigation and the Dashboard.
2. **Given** an Operator user on OS Images, Boot Images or Recovery Images, **When** they upload, edit or delete an entry, **Then** the operation succeeds under exactly the rules that apply to an Administrator (capacity limits, in-use checks, validation).
3. **Given** an Operator user on Boot Images, **When** they promote a pre-production image or demote a production image, **Then** the change takes effect as it does for an Administrator.
4. **Given** an Operator user, **When** they open the Branding or Configuration page by URL, **Then** they are returned to the Dashboard.
5. **Given** an Operator token, **When** any operation on the Administrator-only list (FR-012) is called directly, **Then** it is refused as forbidden and no data changes.
6. **Given** an Operator user, **When** they use the Header's location picker or the Devices location filter, **Then** both work as they do for a Technician.
7. **Given** an Operator user on the Devices page, **When** pre-flight authorization is on, **Then** approving or revoking a pre-flight override is not offered, and the server refuses it if called directly.
8. **Given** an Operator user on the Autopilot page, **When** they approve, reject, retry a failed import or change the group tag of a device already in Autopilot, **Then** the decision takes effect as it does for an Autopilot Approver or Administrator, and Autopilot settings are neither offered nor accepted.
9. **Given** an Operator user on the Locations page, **When** they add, edit or delete a location or change the country-to-region mapping, **Then** the change succeeds under exactly the rules that apply to an Administrator.
10. **Given** an Operator user on the Wi-Fi Networks page, **When** they view, add, edit, reorder, delete or assign a network to a location, **Then** the change succeeds as it does for an Administrator, and no Wi-Fi password is returned to the Portal.
11. **Given** an Operator user, **When** they open Reports, **Then** every report available to Reader and Administrator is available, including Autopilot Registration History and its CSV export.

---

### User Story 2 - Administrator and existing roles keep everything they have (Priority: P1)

After the release, an Administrator continues to do everything they did before, including the new Operator capabilities, without being assigned Operator. Technicians, Readers and Autopilot Approvers see no change at all.

**Why this priority**: The role must be additive. A regression for an existing role blocks the release regardless of what Operator gains.

**Independent Test**: Run the full capability matrix of roles-and-access for users holding only Administrator, only Technician, only Reader and only Autopilot Approver, before and after the release, and compare.

**Acceptance Scenarios**:

1. **Given** a user holding only Administrator, **When** they use the Portal and Media Builder, **Then** every capability in the matrix is available, including every Operator capability, without an Operator assignment.
2. **Given** a user holding only Technician, **When** they use the Portal, **Then** catalogs stay read-only, driver management stays read-only, and every catalog write is refused by the server.
3. **Given** a user holding only Technician on the Media Builder registration, **When** Home loads, **Then** Generate Boot Image is restricted by role and Prepare USB Storage Device lists production boot images only.
4. **Given** a user holding only Reader or only Autopilot Approver, **Then** their access is identical to today.
5. **Given** a Technician token, **When** Reports session data is requested directly, **Then** it is refused as forbidden, and the Technician's Dashboard still shows the Completed Sessions stat with the same count as before.

---

### User Story 3 - Operator user generates boot images in Media Builder (Priority: P2)

An Operator user holding Operator on the Media Builder registration and Media Builder access on the Operator API generates a boot image on a workstation with the Windows ADK, uploads the result on the Portal's Boot Images page, prepares a test USB drive with the new pre-production image, and promotes it after testing.

**Why this priority**: Completes the content lifecycle. Portal content management (User Story 1) is valuable without it, so it ranks below the P1 slices.

**Independent Test**: With a user holding Operator on the Media Builder and Portal registrations and Media Builder access on the Operator API, generate a boot image, upload it in the Portal, prepare a USB drive with it while it is pre-production, and promote it.

**Acceptance Scenarios**:

1. **Given** an Operator user, the Windows ADK installed and a configured boot media certificate, **When** Media Builder Home loads, **Then** Generate Boot Image is available.
2. **Given** an Operator user without the Windows ADK, **When** Home loads, **Then** Generate Boot Image shows the existing ADK reason, not a role restriction.
3. **Given** an Operator user and a missing, inaccessible or unverifiable boot media certificate, **When** Home loads, **Then** the existing certificate message is shown and generation is blocked as it is for an Administrator.
4. **Given** a Technician, **When** Home loads, **Then** Generate Boot Image is restricted and the explanation names the Operator or Administrator role as required.
5. **Given** an Operator user in Prepare USB Storage Device, **Then** production and pre-production boot images are listed, as for an Administrator.
6. **Given** a user with Operator on the Media Builder registration but no Operator or Administrator role on the Portal registration, **When** they try to upload the generated image in the Portal, **Then** the upload is refused, because each registration is evaluated separately.

---

### User Story 4 - Existing tenant adds the role after upgrade (Priority: P2)

An administrator of an existing deployment runs the upgrade, then follows the upgrade instructions to add the Operator role by hand to the Portal and Media Builder registrations without touching existing role definitions or assignments. They then create a Cloud Imaging Operators group and assign it.

**Why this priority**: Every current customer reaches the feature through an upgrade. Fresh installs follow the updated setup instructions, which are simpler.

**Independent Test**: Take a tenant with existing Administrator, Technician, Reader and Autopilot Approver assignments, run the upgrade, add the role as the upgrade instructions describe, and verify the role exists on both registrations, every existing assignment is unchanged, and every existing user's access is unchanged until someone is assigned Operator.

**Acceptance Scenarios**:

1. **Given** Portal and Media Builder registrations without Operator, **When** the administrator adds the role as the upgrade instructions describe, **Then** both registrations define an enabled `CloudImaging.Operator` role for users and groups, and every existing role definition and assignment is unchanged.
2. **Given** the upgrade instructions for this release, **When** an administrator reads them, **Then** they state that `CloudImaging.Operator` must be added to and assigned on the Portal and Media Builder registrations, and that existing roles and assignments must not be altered.
3. **Given** the new release is deployed but the role is not yet defined on a registration, **Then** every existing user keeps their current access and the application works normally.
4. **Given** a fresh installation that follows the setup instructions, **Then** Operator exists on both registrations from the start.
5. **Given** the backend and Media Builder releases that ship this feature, **When** an administrator opens either release on GitHub, **Then** its description tells them to add and assign the role and links to the upgrade instructions, and it matches the release notes file committed in the repository.

---

### User Story 5 - Administrators can tell who should get the Operator role (Priority: P3)

An administrator reads the roles reference and the setup instructions to decide whom to give Operator. The capability matrix has an Operator column, the Media Builder table has an Operator row, the group-per-persona table has a Cloud Imaging Operators group, and the troubleshooting table covers the common Operator mistakes.

**Why this priority**: Required to use the role correctly, but the role works without it.

**Independent Test**: An administrator unfamiliar with the feature assigns Operator to a group using only the documentation, and a member performs a catalog write after signing in again.

**Acceptance Scenarios**:

1. **Given** the roles reference, **When** an administrator reads the Portal capability matrix, **Then** each row states what Operator can do, consistent with the server's behaviour.
2. **Given** the setup instructions, **When** an administrator follows Phase 1 and Phase 3, **Then** they create the Operator role on both registrations and assign it, including Media Builder access on the Operator API for Operator users who build boot media.

---

### Edge Cases

- A user holds both Technician and Operator: the effective access is Operator; nothing is duplicated or conflicting in the navigation.
- A user holds only Operator (no Technician in the token): every Technician capability applies, including Devices, the Autopilot queue, location reads and the Dashboard. The user never sees the Access denied screen.
- Operator plus Reader: the union adds nothing beyond Operator, which already includes every report.
- Operator plus Autopilot Approver: the union adds nothing beyond Operator, which already includes Autopilot decisions and the history report.
- Operator is assigned after the user signed in: the current token lacks the role until the user signs out and back in; until then the user keeps their previous access. The same applies to the Media Builder.
- Operator is removed while the user is signed in: the existing token remains valid until it expires or is refreshed, as for every other role today.
- Media Builder user with Operator but without Media Builder access on the Operator API: they can sign in and Generate Boot Image passes the role gate, but every call to the Operator API is refused and the Media Builder reports the missing Operator API access, as it does for a Technician today.
- Operator on the Portal registration only, or on the Media Builder registration only: each application grants what its own registration assigns.
- Operator assigned through a group: behaves exactly like a direct assignment; group nesting follows Entra ID's existing app role assignment behaviour.
- An Operator user starts an image upload: every step of the upload (start, progress status, publish, abandon) is authorised on its own request, so an Operator user can complete the uploads they start.
- An Operator user deletes an OS image or a location that is in use, or uploads beyond capacity: the existing rules apply unchanged.
- An Operator user edits a Wi-Fi network: the password can be set or replaced but is never shown or returned, as for an Administrator.
- The platform update notice: remains Administrator-only and is not shown to Operator.
- A Technician opens a Reports URL or calls Reports data directly: the page redirects to the Dashboard as today and the data request is refused (FR-015a). Their Dashboard's Completed Sessions stat is unaffected.
- The release is rolled back to a version without this feature: a user holding only Operator loses all Portal and Media Builder access until the rollback is undone or they are also assigned Technician or Administrator.
- The administrator applying the upgrade cannot modify app registrations: the release deploys and works normally without the role; nobody can be assigned Operator until someone with that permission adds it.
- Driver management (spec 005) is not yet released: the Operator driver requirements apply when it ships; nothing else in this specification depends on it.

## Requirements *(mandatory)*

### Functional Requirements

#### Role definition and hierarchy

- **FR-001**: The system MUST define a user-facing app role with value and display name `CloudImaging.Operator`, allowed for users and groups, on both the Cloud Imaging Portal and the Cloud Imaging Media Builder registrations. Its description MUST state that it covers day-to-day operational administration (OS, boot and recovery images, drivers, boot image generation, locations, Wi-Fi networks, Autopilot approvals, reports) without security or tenant-wide settings.
- **FR-002**: The role hierarchy MUST be: Administrator implies Operator, Operator implies Technician, Technician implies the internal portal access used on read routes. A user holding only a higher role MUST receive every capability of the roles it implies, without those roles being assigned.
- **FR-003**: Reader and Autopilot Approver MUST remain separate roles outside the hierarchy. Operator MUST NOT imply the Reader or Autopilot Approver role itself, and neither of them implies Operator. Operator grants the same capabilities those roles offer (every report; Autopilot decisions and the history report) in its own right (FR-013, FR-015).
- **FR-004**: The implication MUST be applied identically by the Portal backend, the Portal UI and the Media Builder, because sign-in tokens carry only directly assigned roles.
- **FR-005**: No user MUST lose a capability they hold before this feature. Administrator, Technician, Reader and Autopilot Approver capabilities MUST be unchanged. The one exception is FR-015a: Technician loses direct API access to Reports-only data that the Portal never offered them.
- **FR-006**: The Operator API's service roles (portal access for the Portal backend's managed identity, Media Builder access for Media Builder users) MUST remain unchanged; Operator MUST NOT be defined on the Operator API registration.

#### Portal backend

- **FR-007**: The Portal backend MUST accept Operator or Administrator for every OS image write: create, start upload, publish, abandon, edit metadata (including the Windows release introduced by spec 005) and delete.
- **FR-008**: The Portal backend MUST accept Operator or Administrator for every boot image write: start upload, publish, promote, demote and delete.
- **FR-009**: The Portal backend MUST accept Operator or Administrator for every recovery image write: start upload, publish and delete.
- **FR-010**: The Portal backend MUST accept Operator or Administrator for reading the status of an image upload job.
- **FR-011**: The Portal backend MUST accept Operator or Administrator for every driver management write defined in spec 005, and allow Technician read access as that specification defines. Delivered by spec 005 (`specs/005-os-driver-management`) once it ships; this feature supplies the role and the guard it uses.
- **FR-011a**: The Portal backend MUST accept Operator or Administrator for every Locations catalog write: create, update and delete locations, read and change the country-to-region mapping, and assign Wi-Fi networks to a location.
- **FR-011b**: The Portal backend MUST accept Operator or Administrator for every Wi-Fi network operation: view, add, edit, reorder and delete. No Portal response MUST return a Wi-Fi password, for any role.
- **FR-012**: The following MUST remain Administrator-only and MUST be refused for an Operator token: Branding writes; reading and changing the portal configuration (pre-flight authorization toggle, token and SAS expiry); changing the partitioning scheme; viewing, generating and rotating the boot media certificate; approving and revoking pre-flight overrides; changing Autopilot settings (feature switch, group tags, retention); the platform update check; and abuse-control observability (spec 002 FR-014).
- **FR-013**: The Portal backend MUST accept Operator, Autopilot Approver or Administrator for every Autopilot decision (approve, reject, retry a failed import, change the group tag of a device already in Autopilot) and for every read those decisions need, as it does for Autopilot Approver today. Changing Autopilot settings remains Administrator-only (FR-012).
- **FR-014**: Operator MUST keep every Technician read and session capability: devices and sessions (view, couple, assign, bulk assign, cancel, logs), catalog reads, location reads and user preferences.
- **FR-015**: The Portal backend MUST accept Operator for every Reports read that Reader and Administrator are accepted for, including the Autopilot Registration History report and its CSV export. An Operator user MUST NOT need Reader to see Reports.
- **FR-015a**: Reports-only data (session outcome records with device, location, pre-flight and failure detail) MUST be accepted only for Reader, Operator and Administrator. A Technician calling it directly MUST be refused as forbidden (FR-016). The Dashboard's Completed Sessions stat, which Technicians see, MUST be served by a separate read that returns only completion times and is accepted for Technician, Operator, Administrator and Reader. Catalog lists used by both Reports and Technician pages MUST keep their current access. No deprecation notice is required (constitution Principle IV): `GET /api/session-history` is the Portal's internal backend-for-frontend API, called only by the Portal frontend shipped in the same backend release, not a public or user-facing interface, and Technicians see no UI change because the Dashboard stat moves to `GET /api/session-history/completed-timestamps` in the same release.
- **FR-016**: A refused request MUST return the existing forbidden response, which names the roles that would be accepted, and MUST leave all data unchanged.
- **FR-017**: Role-protected writes that already record the acting user MUST record an Operator user the same way. No new audit records are required by this feature.

#### Portal UI

- **FR-018**: The Portal MUST let a user holding any of Administrator, Operator, Technician, Reader or Autopilot Approver past the Access denied screen.
- **FR-019**: For Operator users, the OS Images, Boot Images, Recovery Images, Locations and Wi-Fi Networks pages MUST show the same controls as for Administrators, the Autopilot page MUST show the decision controls (approve, reject, retry, change group tag), Reports and every report MUST be available, and, once spec 005 ships, the Drivers page MUST show its edit controls (the Drivers part is delivered by spec 005).
- **FR-020**: For Operator users, the navigation, the Dashboard tiles and direct page URLs for Branding and Configuration (including its Autopilot settings) MUST behave as they do for Technicians: hidden from navigation and the Dashboard, and redirected to the Dashboard when opened by URL.
- **FR-021**: For Operator users, the Devices page MUST NOT offer pre-flight override approval or revocation, the Autopilot page MUST NOT offer Autopilot settings, and the platform update notice MUST NOT be shown.
- **FR-022**: Every control the Portal shows to an Operator user MUST lead to an operation the server accepts for that user. UI gating is a convenience only; the server is the authority (FR-007 to FR-016).
- **FR-023**: Where the Portal tells a user that an action requires a role, the text MUST name Operator or Administrator for catalog, driver, location and Wi-Fi writes, Operator, Autopilot Approver or Administrator for Autopilot decisions, and Administrator for the operations in FR-012.

#### Operator API

- **FR-024**: The Operator API MUST NOT be changed to check user roles for this feature. It continues to trust the Portal backend's portal access role, which is only exercised after the Portal backend has enforced the user's role, and to limit Media Builder access to its existing allow list.
- **FR-025**: The Operator API's descriptions of who may perform the writes in FR-007 to FR-013 MUST be corrected to name the roles the Portal backend accepts, so that operators reading API documentation see the same model as the Portal.

#### Media Builder

- **FR-026**: The Media Builder MUST make Generate Boot Image available to Operator and Administrator users, still subject to the existing Windows ADK check and boot media certificate check, which keep their own messages.
- **FR-027**: A Technician-only user MUST see Generate Boot Image restricted by role, with an explanation that it requires the Operator or Administrator role. A user without any Media Builder role MUST see the existing no-role explanation, naming Technician, Operator or Administrator.
- **FR-028**: Prepare USB Storage Device MUST list pre-production boot images to Operator and Administrator users and production images only to Technicians.
- **FR-029**: The Media Builder MUST treat Operator as a Media Builder role for reaching any workflow, so an Operator-only user is never shown the no-role state.
- **FR-030**: The generated boot image MUST continue to be published through the Portal's Boot Images upload, which the Portal backend authorises (FR-008). The Media Builder's role gate MUST NOT be presented in documentation as a server-side control.

#### Provisioning and upgrade

- **FR-031**: An administrator MUST add the Operator role manually to the Cloud Imaging Portal and Cloud Imaging Media Builder registrations and assign it, following the setup instructions on fresh installs and the upgrade instructions and release notes on existing tenants. No deployment, verification or upgrade script creates, checks or reports the role.
- **FR-032**: The setup and upgrade instructions MUST tell the administrator that adding the role must not change, remove or re-create any existing role definition, role ID or role assignment on any registration, and to skip the step when the role already exists.
- **FR-033**: The app registration verification script MUST stay unchanged. It checks only the existing roles, so a registration with or without Operator neither fails verification nor is reported.
- **FR-034**: Each release workflow (backend, Client, Media Builder) MUST publish the GitHub release body from a release notes file committed in the repository, one file per stream and version, so the published body equals the file. A release run whose notes file is missing or empty MUST fail before anything is built. The notes for the backend and Media Builder releases that ship this feature MUST tell administrators of existing tenants to add `CloudImaging.Operator` to the Portal and Media Builder registrations and assign it, and link to the upgrade instructions for the steps.
- **FR-035**: Infrastructure templates and deployment scripts MUST need no logic change, because no app role is declared or created in them. The deployment form tooltip for the Portal client ID MUST list every user role the Portal registration exposes: `CloudImaging.Administrator`, `CloudImaging.Operator`, `CloudImaging.Technician`, `CloudImaging.Reader` and `CloudImaging.AutopilotApprover`. The Media Builder client ID tooltip lists no roles and stays unchanged. This is a text change only.

#### Documentation

- **FR-036**: The roles reference MUST describe seven app roles, add an Operator column to the Portal capability matrix and the Autopilot table, add Operator to the Media Builder table, state the hierarchy as "Administrator implies Operator, which implies Technician", state that Operator includes every report and Autopilot decision, state that Reports data is refused to Technician tokens even when called directly, and update the troubleshooting table (for example: Operator user cannot reach Branding or Configuration, Technician sees Generate Boot Image restricted, Operator cannot be assigned because the role was not added to the registration).
- **FR-037**: The setup instructions MUST add Operator to the role lists of the Portal and Media Builder registrations, add a Cloud Imaging Operators group to the group-per-persona step and its assignment table (Portal, Media Builder, and Media Builder access on the Operator API for Operator users who build boot media), and update the Media Builder sign-in prerequisites.
- **FR-038**: The upgrade instructions MUST describe the manual steps to add the role to both registrations and assign it, that existing roles and assignments must not be altered (FR-032), that existing access is unchanged until someone is assigned Operator, that users must sign out and back in after assignment, and the rollback consequence for Operator-only users.
- **FR-039**: User-facing text in the Portal and the Media Builder MUST NOT contain requirement or task identifiers.
- **FR-040**: Files changed by this feature MUST NOT contain the em dash, en dash, middle dot, right arrow or long right arrow characters in documentation, user-facing text, log messages or comments. Navigation paths MUST be written with ">" and spaces (for example "Configuration > Autopilot") and the role hierarchy with "implies".

### Key Entities *(include if feature involves data)*

- **App role Operator**: a user-facing role on the Portal and Media Builder registrations, with value, display name, description and allowed member types (users and groups).
- **Role hierarchy**: the implication chain Administrator, Operator, Technician, internal portal access, applied by each application to the roles in the user's token. Reader and Autopilot Approver are standalone.
- **Role assignment**: a user or group assigned Operator on one registration. Portal and Media Builder assignments are independent.
- **Administrator-only operation list**: the operations in FR-012 that Operator is refused.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: 100% of operations on the Administrator-only list are refused for a token holding only Operator, including direct API calls, with 0 data changes.
- **SC-002**: 100% of Operator capabilities (catalog writes, boot image promotion and demotion, upload status reads, driver management writes once spec 005 ships, Locations catalog and Wi-Fi network operations, Autopilot decisions, every report, Generate Boot Image) succeed for a user assigned only Operator, without Technician, Administrator, Reader or Autopilot Approver also assigned.
- **SC-003**: 0 capability changes for users holding only Administrator, Technician, Reader or Autopilot Approver, measured across every row of the capability matrix before and after the release. The only API-level change is FR-015a, which removes no page or control a Technician sees.
- **SC-004**: After an administrator follows the upgrade instructions on an existing tenant, Operator is defined on both registrations and 100% of the role definitions and assignments that existed before are unchanged.
- **SC-005**: 0 controls shown to an Operator user in the Portal or Media Builder lead to an operation the server refuses, and 0 Administrator-only pages (Branding, Configuration) are reachable by an Operator user.
- **SC-006**: An administrator following only the documentation can assign Operator to a group and have a member complete a catalog write within 10 minutes, including the required sign-out and sign-in.
- **SC-007**: 100% of direct requests for Reports-only data with a Technician-only token are refused, and the Technician Dashboard's Completed Sessions count equals the count before the release for the same period.
- **SC-008**: The published body of each release that ships this feature equals its committed release notes file, and 0 banned characters (FR-040) remain in the files this feature changes.

## Assumptions

- Sign-in tokens carry only directly assigned roles, so the hierarchy is applied by each application, as it already is for Administrator and Technician in the Portal backend.
- Operator includes every report and every Autopilot decision, so Operator users need neither Reader nor Autopilot Approver. Those roles remain for users who need only reports or only Autopilot decisions.
- Operator manages Wi-Fi networks under the existing password rules: passwords are encrypted to the boot media certificate and never returned to the Portal, so Wi-Fi management grants no access to stored passwords or to the certificate.
- Operator sees pre-production boot images in Prepare USB Storage Device, because Operator promotes boot images and needs to test them on a device first.
- The Media Builder does not upload boot images itself. The generated image is uploaded on the Portal's Boot Images page, so the server-side boundary for publishing a generated image is the Portal backend. The Operator API identifies all Media Builder callers by the same Media Builder access role and cannot distinguish Technician, Operator and Administrator, so the Generate Boot Image role gate stays a workflow guard, as it is today for Administrator. Both workflows already read the boot media certificate through that shared role; tightening that is out of scope.
- The partitioning scheme setting is part of the Configuration page and stays Administrator-only.
- The platform update notice is an administration concern and stays Administrator-only.
- Catalog writes do not record the acting user today. Wi-Fi and Autopilot changes do, and record an Operator user the same way (FR-017); pre-flight override changes stay Administrator-only. Driver management records the acting user under spec 005 for both Operator and Administrator.
- The role is added to the registrations by hand, as the existing roles are (spec 001 FR-040b). Scripts and templates are not changed.
- The role uses the same naming convention as the existing roles: display name equal to the value.

### Dependencies

- **Supersedes in part**: spec 001 FR-040a (Administrator-only catalog and boot image lifecycle writes), FR-040b ("exactly two user-facing app roles") and FR-050b (Generate Boot Image Administrator-only), plus any earlier requirement that limits Locations catalog management, Wi-Fi network management, Autopilot decisions or Reports to the roles that held them before, for the capabilities in FR-011a, FR-011b, FR-013 and FR-015. The other parts of those requirements stand.
- **Spec 002 FR-014**: abuse-control observability remains Administrator-only.
- **Spec 005 (OS driver management)**: its FR-075 and FR-076 assume this role. Driver requirements here apply once spec 005 ships; this feature does not depend on it.
- The Cloud Imaging Portal and Cloud Imaging Media Builder app registrations exist, and whoever adds the role has permission to modify them.
- Affected documentation: `docs/roles-and-access.md`, `docs/setup-instructions.md`, `docs/upgrade-instructions.md`, `docs/image-upload-pipeline.md`, `README.md`, `CONTRIBUTING.md`, and the release notes files for the backend, Client and Media Builder releases that ship this feature (only the backend and Media Builder notes carry the Operator notice).
