import * as React from 'react';
import { Search, X } from 'lucide-react';
import { cn } from '../../lib/utils.ts';
import { Button } from './button.tsx';
import { Input } from './input.tsx';

interface SearchInputProps {
  value: string;
  onValueChange: (value: string) => void;
  placeholder?: string;
  id?: string;
  'aria-label'?: string;
  /** Applied to the positioning wrapper, which participates in the parent's flex layout. */
  wrapperClassName?: string;
}

/**
 * Search field with a leading magnifier and a clear button once it has a value.
 *
 * Exists because the reports disagreed: one composed its own icon inline, the rest shipped a bare
 * `Input`, and none of them could be cleared without selecting the text by hand.
 */
export function SearchInput({
  value,
  onValueChange,
  placeholder,
  id,
  'aria-label': ariaLabel,
  wrapperClassName,
}: SearchInputProps): React.ReactElement {
  const inputRef = React.useRef<HTMLInputElement>(null);

  return (
    <div className={cn('relative flex items-center', wrapperClassName)}>
      <Search
        aria-hidden="true"
        className="pointer-events-none absolute left-3 h-4 w-4 text-muted-foreground"
      />
      <Input
        ref={inputRef}
        id={id}
        type="search"
        aria-label={ariaLabel}
        value={value}
        placeholder={placeholder}
        onChange={event => onValueChange(event.target.value)}
        // `pr-8` reserves the clear button's 32px track even while it is absent, so the text does
        // not reflow the moment the first character is typed.
        className="w-full pl-9 pr-8 [&::-webkit-search-cancel-button]:appearance-none"
      />
      {value && (
        <Button
          variant="ghost"
          size="icon"
          aria-label="Clear search"
          className="absolute right-0"
          onClick={() => {
            onValueChange('');
            inputRef.current?.focus();
          }}
        >
          <X />
        </Button>
      )}
    </div>
  );
}
