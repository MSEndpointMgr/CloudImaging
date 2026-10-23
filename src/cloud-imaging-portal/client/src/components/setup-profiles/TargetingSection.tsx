import { Select } from '../ui/select.tsx';
import { LocationPicker, type PickerLocation } from '../LocationPicker.tsx';
import {
  allDevicesDisabledReason,
  locationDisabledReason,
  locationsTargetedElsewhere,
  type SetupProfileSummary,
  type SetupProfileTargeting,
} from '../../lib/setupProfiles.ts';

/**
 * Which devices get the profile: All devices, or selected locations. A location or All devices
 * that another profile already owns cannot be picked, and says which profile owns it.
 */
export function TargetingSection({ targeting, onChange, profiles, profileId, locations, problem }: {
  targeting: SetupProfileTargeting;
  onChange: (targeting: SetupProfileTargeting) => void;
  /** Every profile, to work out which locations are taken. */
  profiles: SetupProfileSummary[];
  /** The profile being edited; undefined for a new one. */
  profileId?: string;
  locations: PickerLocation[];
  problem?: string | null;
}): React.ReactElement {
  const allDevicesReason = allDevicesDisabledReason(profiles, profileId);
  const options = [
    {
      value: 'all',
      label: 'All devices',
      description: allDevicesReason ?? 'Every device with no profile for its location, including media prepared without a location or for locations added later',
      disabled: Boolean(allDevicesReason) && !targeting.allDevices,
    },
    { value: 'selected', label: 'Selected locations', description: 'Only USB media prepared for the locations you pick' },
  ];
  const takenElsewhere = locationsTargetedElsewhere(profiles, profileId, locations.map((l) => l.locationId));

  return (
    <div className="space-y-3">
      <Select
        id="setup-profile-scope"
        aria-label="Devices"
        value={targeting.allDevices ? 'all' : 'selected'}
        onValueChange={(v) => onChange({ allDevices: v === 'all', locationIds: v === 'all' ? [] : targeting.locationIds })}
        options={options}
        wrapperClassName="w-80 max-w-full"
      />
      {!targeting.allDevices && (
        <LocationPicker
          idPrefix="setup-loc"
          locations={locations}
          selected={targeting.locationIds}
          onChange={(locationIds) => onChange({ allDevices: false, locationIds })}
          disabledReason={(id) => locationDisabledReason(profiles, profileId, id)}
          footerNote={takenElsewhere > 0 ? `${String(takenElsewhere)} targeted by other profiles` : undefined}
        />
      )}
      {problem && <p className="text-sm text-destructive">{problem}</p>}
    </div>
  );
}
