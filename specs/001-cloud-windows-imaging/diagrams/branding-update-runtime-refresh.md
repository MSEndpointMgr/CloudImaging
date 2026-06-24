# Branding Update and Runtime Refresh

Administrator configures branding, portal backend persists via Operator API, and users fetch on next page load with auto-update.

```mermaid
sequenceDiagram
    actor Admin as Administrator
    participant Portal as Cloud Imaging<br/>Portal (frontend)
    participant PortalBackend as Cloud Imaging<br/>Portal (backend)
    participant OperatorAPI as Operator API
    participant ImagingCore as Imaging Core API
    participant MetadataStore as Azure Table<br/>Storage
    actor Tech as Technician
    participant PortalFront2 as Cloud Imaging<br/>Portal (frontend) - Tech

    Admin->>Portal: Open Settings / Branding
    Admin->>Portal: Change logo, colors, organization name
    Portal->>PortalBackend: PUT /api/branding<br/>(logo-url, colors, org-name)
    Note over PortalBackend: Entra ID admin role required
    
    PortalBackend->>OperatorAPI: PUT /api/branding<br/>(Entra ID token + branding)
    Note over OperatorAPI: Validate admin role
    OperatorAPI->>ImagingCore: Update branding config
    ImagingCore->>MetadataStore: Store branding record
    Note over MetadataStore: Config: {logo-url, colors, org-name, updated-at}
    ImagingCore-->>OperatorAPI: success
    OperatorAPI-->>PortalBackend: success
    PortalBackend-->>Portal: 'Branding updated'
    Portal->>Admin: Display confirmation
    
    Note over Portal: Admin session reflects new branding immediately<br/>(cached locally)
    
    Tech->>PortalFront2: Open portal (new session)
    PortalFront2->>PortalBackend: GET /api/branding
    PortalBackend->>OperatorAPI: GET /api/branding
    OperatorAPI->>ImagingCore: Fetch branding config
    ImagingCore->>MetadataStore: Query latest branding
    MetadataStore-->>ImagingCore: branding config
    ImagingCore-->>OperatorAPI: config
    OperatorAPI-->>PortalBackend: config
    PortalBackend-->>PortalFront2: JSON {logo-url, colors, org-name}
    PortalFront2->>PortalFront2: Apply CSS: logo, colors, name in UI
    PortalFront2->>Tech: Display updated branding

    Note over Tech: Technician sees new branding on page load
    
    alt Tech already has portal open (existing session)
        Tech->>PortalFront2: (background service worker polling every 60s)
        PortalFront2->>PortalBackend: GET /api/branding
        PortalBackend->>OperatorAPI: GET /api/branding
        OperatorAPI->>ImagingCore: Fetch latest
        ImagingCore-->>OperatorAPI: config (updated-at changed)
        OperatorAPI-->>PortalBackend: config (updated-at changed)
        PortalBackend-->>PortalFront2: new config
        PortalFront2->>PortalFront2: Detect config change (via etag or updated-at)
        PortalFront2->>PortalFront2: Apply new CSS + re-render
        PortalFront2->>Tech: UI updates without page reload
    end
```

## Branding Configuration

| Field | Type | Scope |
|-------|------|-------|
| Logo URL | String | Global, all users |
| Colors | CSS Map | Global, all users |
| Organization Name | String | Global, all users |
| Updated At | Timestamp | Track version |

## Caching & Refresh

- **On page load**: Fetch latest branding config from backend
- **In-session polling**: Background worker polls every 60 seconds for changes
- **Change detection**: Compare updated-at timestamp or etag
- **Auto-update**: Re-apply CSS and re-render on change without page reload
