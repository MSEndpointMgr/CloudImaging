# USB Autostart Validation Matrix

**Spec Reference**: SC-014  
**Objective**: Validate that prepared USB media boots into WinPE and auto-launches Cloud Imaging Client application 100% of the time across a representative device matrix.

## Scope & Constraints

- **Test sample size**: 20 distinct USB 3.x devices (32 GB minimum)
- **Device diversity**: Span multiple manufacturers and chipset families
- **Firmware versions**: Include recent (< 2 years) BIOS/UEFI versions
- **Success criteria**: 20/20 devices boot WinPE, auto-launch Cloud Imaging Client, and display session passcode within 5 minutes from power-on

## Test Matrix

### USB Device Matrix

| Manufacturer | Model | Chipset | Capacity | Qty | Notes |
|--------------|-------|---------|----------|-----|-------|
| SanDisk | Ultra Fit | Realtek | 32 GB | 2 | Common choice, high compatibility |
| Kingston | DataTraveler Exodia | | 32 GB | 2 | Industrial-grade option |
| Crucial | MX500 | | 32 GB | 2 | Includes DRAM cache variant |
| Verbatim | Store n Go | | 32 GB | 2 | Legacy firmware testing |
| Samsung | BAR Plus | | 32 GB | 2 | Recent chipset, high speed |
| ADATA | UV128 | | 32 GB | 2 | Budget option, generic chipset |
| PNY | Attaché | | 32 GB | 2 | Enterprise variant |
| Transcend | JetFlash | | 32 GB | 2 | Ruggedized design testing |
| Other (Generic) | Off-brand USB 3.0 | Unidentified | 32 GB | 2 | Real-world compatibility |

**Total: 20 devices**

### Host Device Matrix

Test across at least 3 distinct host machines:

| Host Machine | CPU | RAM | Firmware Type | Boot Mode | Notes |
|--------------|-----|-----|---------------|-----------|-------|
| Machine A | Intel 6th Gen Core i7 | 16 GB | UEFI | Secure Boot ON | Typical enterprise laptop |
| Machine B | AMD Ryzen 5000 | 32 GB | UEFI | Secure Boot ON | Modern consumer hardware |
| Machine C | Intel Xeon E5 | 64 GB | Legacy BIOS | Legacy mode | Older server/workstation |

## Preparation Procedure

1. **Media Builder setup**: Install Cloud Imaging Media Builder on Windows 11 workstation with .NET 10 runtime.
2. **Boot image generation**: Generate WinPE boot image with Cloud Imaging Client embedded using documented workflow (see Media Builder user guide).
3. **USB preparation**: For each device:
   - Insert USB into workstation
   - Launch Media Builder -> "Prepare USB Storage Device" workflow
   - Authenticate with Entra ID
   - Validate device is removable
   - Complete partition and boot image deployment
   - Eject USB

## Test Execution

### Per-USB Test Procedure

1. **Baseline validation**: Verify USB manifest on device (should record preparation timestamp, app version, boot image version, partition layout).
2. **Boot test on Machine A**:
   - Insert USB into Machine A USB 3.0 port
   - Power on device
   - Tap boot menu key (F12, Del, or manufacturer-specific key)
   - Select USB drive from boot order
   - Wait up to 5 minutes for WinPE to load and auto-launch Cloud Imaging Client
   - **Success criteria**: Cloud Imaging Client window appears with session passcode visible
   - **Recording**: Screenshot of session passcode + note boot time (T+0 to T+client-ready)
3. **Boot test on Machine B**: Repeat step 2 on second host device
4. **Boot test on Machine C**: Repeat step 2 on third host device
5. **Reboot test**: Warm-reboot device (Ctrl+Alt+Del -> Restart) and verify auto-launch again (2-min timeout for reboot)

## Success/Failure Criteria

### Automatic Pass (✓)

- USB boots to WinPE desktop
- Cloud Imaging Client appears within 5 minutes of boot
- Session passcode is clearly visible in large font
- Client is responsive to input

### Automatic Fail (✗)

- USB does not boot (no WinPE output detected)
- Boot reaches WinPE desktop but Cloud Imaging Client does not launch
- Client launches but displays UI error
- Passcode is not visible
- Boot takes >5 minutes from power-on

### Documented Failure

If a device fails, document:
- USB device manufacturer and model
- Host machine
- Failure mode (no boot, partial boot, client launch failure, etc.)
- Error messages if any
- Mitigation steps attempted

## Summary Report Template

```
# USB Autostart Validation Summary - [Date]

Prepared: [# of USB devices]
Tested: [# of USB devices]
Passed: [# of devices passed on all 3 hosts]
Failed: [# of devices with any failure]
Partial Pass: [# of devices passed on some hosts]

## Pass Rate by Host
- Machine A: X/20 pass
- Machine B: X/20 pass
- Machine C: X/20 pass

## Failures (if any)
- [Device X on Machine Y]: [Failure mode]
- ...

## Overall Success Metric
SC-014 Requirement: 100% (20/20) boot and auto-launch
Result: [PASS / FAIL / CONDITIONAL]

## Sign-Off
Validated by: [Name]
Date: [YYYY-MM-DD]
```

## Automation & CI Integration

- Manual validation required for first 20-device run (baseline establishment)
- Future runs (post-release): Automated validation script can spot-check 3-5 devices on each new Media Builder release
- Results logged to `tests/validation/usb-autostart-results.json` for trend analysis

## Timeline & Scheduling

- Estimated duration: 8-10 hours (includes boot waits and USB preparation)
- Recommended: Run before RC1 release and after any significant boot-image or Client changes
- Frequency: Quarterly or before each major release

## Related Artifacts

- Media Builder user guide: `docs/media-builder-user-guide.md`
- Boot image generation: `docs/boot-image-generation.md`
- T106 implementation task
