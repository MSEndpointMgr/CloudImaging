import { Label } from './ui/label.tsx';
import { Select } from './ui/select.tsx';
import { IMAGE_ARCHITECTURES, architectureLabel, type ImageArchitecture } from '../lib/wimMetadata.ts';
import type { WimArchitectureState } from '../lib/useWimArchitecture.ts';

interface ArchitectureFieldProps {
  id: string;
  state: WimArchitectureState;
  /** Whether a file has been picked; the hint only makes sense once one has. */
  hasFile: boolean;
  /** ISO contents cannot be read in the browser, so the hint explains the later server-side check. */
  isIso?: boolean;
  disabled?: boolean;
}

/**
 * Architecture picker for image uploads: locked when read from the image, required otherwise.
 *
 * A successful read needs no hint, since the picker already shows the detected value. Only the
 * cases the operator has to act on get explanatory text.
 */
export function ArchitectureField({ id, state, hasFile, isIso = false, disabled = false }: ArchitectureFieldProps): React.ReactElement {
  const hint = !hasFile || state.detecting || state.detected
    ? null
    : isIso
      ? 'Choose the architecture of the Windows edition in this ISO. It is verified after upload.'
      : 'The image does not record its architecture. Choose the one it was built for.';

  return (
    <div className="space-y-2">
      <Label htmlFor={id}>Architecture</Label>
      <Select
        id={id}
        value={state.architecture}
        onValueChange={value => state.setArchitecture(value as ImageArchitecture)}
        options={IMAGE_ARCHITECTURES.map(arch => ({ value: arch, label: architectureLabel(arch) }))}
        placeholder="Select"
        disabled={disabled || state.detected || state.detecting}
        aria-label="Architecture"
        wrapperClassName="w-28"
      />
      {hint && <p className="text-sm text-muted-foreground">{hint}</p>}
    </div>
  );
}
