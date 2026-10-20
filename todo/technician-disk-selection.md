# TODO: Technician target disk selection

Status: **not started**. Small, and closer to a defect than a missing feature: there is hardware
this product cannot image at all today.

## Context / what happens now

[`DiskFormatService`](../src/CloudImaging.Client/Services/DiskFormatService.cs) picks the target
disk itself. It enumerates physical disks, excludes USB media, and then requires **exactly one**
candidate to remain. Zero candidates is an error, and so is more than one.

The automatic choice is right for the common case, a laptop with a single internal NVMe, and it
avoids the worst possible mistake in this product, which is wiping the wrong disk. That reasoning
still holds and should survive any change here.

What it does not handle is a device with two internal disks. A workstation with an OS NVMe plus a
data SSD, a machine with an extra drive left in, a server-class device: all of these are refused.
The technician has no way to say which one, so the deployment stops.

## What to build

A disk selection step in the client, shown **only when the automatic choice is ambiguous**. With
one eligible disk, nothing changes and nothing is shown. The common case must not become slower or
more dangerous for the sake of the uncommon one.

When shown, the list needs enough to identify a disk physically: model, size, bus type, partition
style, and whether it currently holds a Windows installation. Size alone is not enough when a
machine has two identical drives.

## The part that needs care

This screen is the one irreversible choice in the entire product. Everything after it is recoverable
by re-imaging; this is not. It should:

- Default to nothing selected, never to a guessed disk.
- Show what will be destroyed, including any detected existing OS or volume labels.
- Use the destructive confirmation treatment the portal already uses for impactful actions, not a
  bare continue button.
- Keep excluding the USB boot media from the list entirely, rather than merely disabling it.

## Open questions

- Should the portal be able to express a policy, for example "always the smallest eligible disk" or
  "always the disk with an existing Windows installation", so that ambiguity is resolved centrally
  and the technician is never asked? That fits the product's model better than a prompt, but it
  cannot cover every case, so the prompt is still needed as a fallback.
- Does a chosen disk need recording on the session for audit? Probably yes, given it is destructive
  and currently nothing records which disk was wiped.
