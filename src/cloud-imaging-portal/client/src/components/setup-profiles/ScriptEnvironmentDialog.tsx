import { createPortal } from 'react-dom';
import { X } from 'lucide-react';
import { Button } from '../ui/button.tsx';

const mono = (text: string) => <span className="font-mono text-xs">{text}</span>;

/** What a script can rely on in each phase; a row with one cell applies to both. */
const SCRIPT_ENVIRONMENT_ROWS: { label: string; offline: React.ReactNode; specialize?: React.ReactNode }[] = [
  { label: 'When', offline: 'Right after the image is applied, before the device restarts', specialize: 'During Windows setup, after the restart and before OOBE and Autopilot' },
  { label: 'Runs in', offline: 'The boot image (Windows PE or Windows RE), as SYSTEM', specialize: 'The installed Windows, as SYSTEM, with nobody signed in' },
  { label: 'PowerShell', offline: 'Windows PowerShell 5.1 from the boot image', specialize: 'Windows PowerShell 5.1 from the installed Windows' },
  { label: 'Installed Windows', offline: <>{mono('$env:CI_WINDOWS_DRIVE')}, for example {mono('W:')} (no trailing backslash)</>, specialize: <>{mono('$env:SystemDrive')} ({mono('C:')}), {mono('$env:WINDIR')}</> },
  { label: 'Native variables', offline: <>{mono('$env:SystemDrive')}, {mono('$env:WINDIR')} and {mono('$env:TEMP')} point at the boot image ({mono('X:')}), not the installed Windows</>, specialize: 'Point at the installed Windows, as usual' },
  { label: 'Registry', offline: <>The installed Windows&apos; hives are not loaded. Load them with {mono('reg load')} and unload them before the script ends</>, specialize: "Live registry of the installed Windows; HKCU is SYSTEM's" },
  { label: 'Network', offline: 'The connection the device used to reach Cloud Imaging', specialize: "Only if the network adapter's driver is in the image or was added during imaging" },
  { label: 'Result', offline: 'Exit code 0 is success; anything else fails, including 3010 and 1641. Shown on the deployment timeline with the last output line', specialize: <>Exit code 0 is success. Written to {mono('C:\\Windows\\Logs\\CloudImaging\\SetupRunner.log')} only, never changes the deployment result</> },
  { label: 'Timeout', offline: 'The script and every process it started are stopped, and the next script runs' },
  { label: 'Restarts', offline: <>Blocked. {mono('Restart-Computer')}, {mono('shutdown.exe')} and {mono('wpeutil reboot')} fail inside the script</> },
  { label: 'Secrets', offline: 'Everyone who can open the profile can read the script. Fetch secrets at run time instead' },
];

/** The Script environment dialog, opened from the info icon next to the script editor. */
export function ScriptEnvironmentDialog({ onClose }: { onClose: () => void }): React.ReactElement {
  return createPortal(
    <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/60 p-4" role="dialog" aria-modal="true" aria-labelledby="script-environment-title"
      onKeyDown={(e) => { if (e.key === 'Escape') { e.stopPropagation(); onClose(); } }}>
      <div className="flex max-h-full w-full max-w-4xl flex-col rounded-lg border border-border bg-background shadow-xl">
        <div className="flex items-start justify-between gap-3 border-b border-border p-5">
          <div className="space-y-1">
            <h2 id="script-environment-title" className="text-base font-semibold">Script environment</h2>
            <p className="text-sm text-muted-foreground">What a script can rely on in each phase.</p>
          </div>
          <Button variant="ghost" size="icon" onClick={onClose} aria-label="Close"><X /></Button>
        </div>
        <div className="overflow-y-auto p-5">
          <table className="w-full table-fixed border-collapse text-sm">
            <colgroup><col className="w-[170px]" /><col /><col /></colgroup>
            <thead>
              <tr>
                <th className="border-b border-border px-3 py-2 text-left"><span className="sr-only">Topic</span></th>
                <th className="border-b border-border px-3 py-2 text-left text-xs font-semibold uppercase text-muted-foreground">Offline</th>
                <th className="border-b border-border px-3 py-2 text-left text-xs font-semibold uppercase text-muted-foreground">Specialize</th>
              </tr>
            </thead>
            <tbody>
              {SCRIPT_ENVIRONMENT_ROWS.map((row) => (
                <tr key={row.label} className="border-t border-border/70 align-top">
                  <th scope="row" className="whitespace-nowrap px-3 py-2 text-left font-normal text-muted-foreground">{row.label}</th>
                  {row.specialize === undefined
                    ? <td colSpan={2} className="px-3 py-2">{row.offline}</td>
                    : <><td className="px-3 py-2">{row.offline}</td><td className="px-3 py-2">{row.specialize}</td></>}
                </tr>
              ))}
            </tbody>
          </table>
        </div>
        <div className="flex justify-end gap-2 border-t border-border px-5 py-3">
          <Button variant="outline" onClick={onClose}>Close</Button>
        </div>
      </div>
    </div>,
    document.body,
  );
}
