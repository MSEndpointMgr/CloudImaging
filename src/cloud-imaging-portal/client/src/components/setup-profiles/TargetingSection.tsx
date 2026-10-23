import { Label } from '../ui/label.tsx';
import { Select } from '../ui/select.tsx';
import { LocationPicker, type PickerLocation } from '../LocationPicker.tsx';
import {
  allDevicesDisabledReason,
  locationDisabledReason,
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
    { value: 'all', label: 'All devices', description: allDevicesReason ?? 'Devices whose location has no profile of its own', disabled: Boolean(allDevicesReason) && !targeting.allDevices },
    { value: 'selected', label: 'Selected locations' },
  ];

  return (
    <div className="space-y-2">
      <Label htmlFor="setup-profile-scope">Devices</Label>
      <p className="text-sm text-muted-foreground">
        A location can belong to one profile. Devices whose location has no profile get the All devices profile, if one exists.
      </p>
      <Select
        id="setup-profile-scope"
        value={targeting.allDevices ? 'all' : 'selected'}
        onValueChange={(v) => onChange({ allDevices: v === 'all', locationIds: v === 'all' ? [] : targeting.locationIds })}
        options={options}
        wrapperClassName="max-w-xs"
      />
      {!targeting.allDevices && (
        <LocationPicker
          idPrefix="setup-loc"
          locations={locations}
          selected={targeting.locationIds}
          onChange={(locationIds) => onChange({ allDevices: false, locationIds })}
          disabledReason={(id) => locationDisabledReason(profiles, profileId, id)}
        />
      )}
      {problem && <p className="text-sm text-destructive">{problem}</p>}
    </div>
  );
}
