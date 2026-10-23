import { useEffect, useState } from 'react';
import { fetchSetupProfiles, groupDevicesBySetupProfile, type SetupProfileSummary } from '../../lib/setupProfiles.ts';

/**
 * Which setup profile the devices about to be imaged get, by the location on their USB media, so
 * media prepared for the wrong location is caught before the disk is wiped. Shows nothing when no
 * profiles exist or the list cannot be loaded; imaging never waits for it.
 */
export function StartImagingProfileLines({ sessions }: { sessions: { locationId?: string | null }[] }): React.ReactElement | null {
  const [profiles, setProfiles] = useState<SetupProfileSummary[]>([]);

  useEffect(() => {
    let current = true;
    fetchSetupProfiles()
      .then((list) => { if (current) setProfiles(list); })
      .catch(() => { /* No lines; the overlay works without them. */ });
    return () => { current = false; };
  }, []);

  const lines = groupDevicesBySetupProfile(sessions, profiles);
  if (lines.length === 0) return null;
  return (
    <div className="space-y-2">
      <p className="text-xs leading-relaxed text-muted-foreground">Setup profile, by the location on each device&apos;s USB media:</p>
      <ul className="list-disc space-y-1 pl-4 text-xs leading-relaxed text-muted-foreground">
        {lines.map((line) => <li key={line}>{line}</li>)}
      </ul>
    </div>
  );
}
