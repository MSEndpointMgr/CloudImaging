import { useRef, useState } from 'react';
import { detectWimArchitecture, type ImageArchitecture } from './wimMetadata.ts';

export interface WimArchitectureState {
  /** Empty until read from the image or chosen by the operator; there is deliberately no default. */
  architecture: ImageArchitecture | '';
  setArchitecture: (architecture: ImageArchitecture) => void;
  /** True when the value was read from the image itself (the picker is then locked). */
  detected: boolean;
  detecting: boolean;
  /** Inspects a newly picked file. Resolves to an error message for unsupported (e.g. x86) images. */
  inspect: (file: File | null) => Promise<string | null>;
}

/** Architecture state for an image upload dialog, derived from the WIM when possible. */
export function useWimArchitecture(): WimArchitectureState {
  const [architecture, setArchitecture] = useState<ImageArchitecture | ''>('');
  const [detected, setDetected] = useState(false);
  const [detecting, setDetecting] = useState(false);
  const currentFile = useRef<File | null>(null);

  const inspect = async (file: File | null): Promise<string | null> => {
    currentFile.current = file;
    setArchitecture('');
    setDetected(false);
    if (!file) {
      setDetecting(false);
      return null;
    }

    setDetecting(true);
    const detection = await detectWimArchitecture(file);
    // A newer pick supersedes this result.
    if (currentFile.current !== file) return null;
    setDetecting(false);

    if (detection.kind === 'supported') {
      setArchitecture(detection.architecture);
      setDetected(true);
      return null;
    }
    return detection.kind === 'unsupported'
      ? `This image targets ${detection.description}. Only x64 and ARM64 images are supported.`
      : null;
  };

  return { architecture, setArchitecture, detected, detecting, inspect };
}
