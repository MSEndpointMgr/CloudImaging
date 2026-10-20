# TODO: Configurable post-deployment restart

Status: **not started**. The smallest item on the list. Included because the current value is
actively unhelpful during the one moment an operator needs to read the screen.

## Context / what happens now

`ResultsViewModel` restarts the device **10 seconds** after a successful deployment. The value is a
hardcoded constant in [`ResultsViewModel`](../src/CloudImaging.Client/ViewModels/ResultsViewModel.cs);
there is no setting anywhere, in the portal or on the media.

Ten seconds is fine for a technician imaging a rack of identical machines. It is too short in two
situations that matter more:

- **Validating a new boot image or OS image**, where the results screen carries the outcome, the
  serial and the support reference, and ten seconds is not enough to read or photograph it.
- **A deployment that succeeded but logged warnings**, where the detail disappears before anyone
  can act on it.

Foundry OSD makes both the delay and the automatic restart itself configurable, and its own quick
start tells first-time users to raise or disable it. That is a reasonable signal that the fixed
value is wrong.

## What to build

Two fields in **Configuration → Miscellaneous**, beside the existing numeric settings there:

- **Restart automatically after a successful deployment**, a switch, default on.
- **Restart delay**, seconds, shown only while the switch is on. Suggested bounds 0 to 600, default
  10 so existing behaviour is unchanged.

Both belong on `PortalConfiguration`, which the session already carries to the device, so no new
transport is required.

## Notes

- Applies to **success only**. The failure and blocked screens already stay up deliberately, and
  must keep doing so: those are the screens an operator is most likely to need.
- The countdown should be cancellable from the results screen regardless of the configured delay.
  A technician who wants to read the result should not have to wait for a timeout, and today there
  is no way to stop it at all.
- With the switch off, the screen needs an explicit restart control, otherwise the device sits in
  WinPE until someone power-cycles it.
