# Troubleshooting index

Start with the symptom visible to the technician or administrator. The linked runbook section contains likely causes and corrective steps.

| Symptom | Start here |
|---|---|
| Device displays a passcode, but Portal coupling returns 404 | [Device displays passcode but coupling fails with 404](operations-runbook.md#symptom-device-displays-passcode-but-coupling-fails-with-404) |
| Portal coupling returns 409 | [Portal shows 409 when coupling](operations-runbook.md#symptom-portal-shows-409-when-coupling) |
| Device immediately shows **Not Authorized** | [Device shows Not Authorized after registration](operations-runbook.md#symptom-device-shows-not-authorized-after-registration) |
| Client receives HTTP 401 after coupling | [Client receives HTTP 401 after coupling](operations-runbook.md#symptom-client-receives-http-401-after-coupling) |
| Image download returns HTTP 403 or its link expired | [Image download link returns 403](operations-runbook.md#symptom-image-download-link-returns-403) |
| Progress stops during download or another imaging stage | [Progress reporting stops during imaging](operations-runbook.md#symptom-progress-reporting-stops-during-imaging) |
| Waiting or imaging session remains stale in Portal | [Session remains stale in Portal](operations-runbook.md#symptom-session-remains-stale-in-portal) |
| Device needs Wi-Fi before registration | [Device has no wired network](operations-runbook.md#symptom-device-has-no-wired-network-or-needs-wi-fi-to-reach-the-device-gateway) |
| Session creation takes longer than five seconds | [Session creation takes more than five seconds](operations-runbook.md#symptom-session-creation-takes-more-than-5-seconds) |
| Bulk assignment times out | [Bulk assignment times out for 20 or more sessions](operations-runbook.md#symptom-bulk-assignment-times-out-for-20-or-more-sessions) |

When investigating session behavior, record the session ID and current state first. Use [Session lifecycle](session-lifecycle.md) to identify the expected next transition, then correlate the same session ID across Client logs and Application Insights. Log locations and starter queries are listed in the [Operations runbook](operations-runbook.md#4-log-locations).