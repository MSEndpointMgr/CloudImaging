/** @type {import('tailwindcss').Config} */
export default {
  darkMode: 'class',
  content: ['./index.html', './src/**/*.{js,ts,jsx,tsx}'],
  theme: {
    container: {
      center: true,
      padding: '2rem',
      screens: { '2xl': '1400px' },
    },
    extend: {
      fontFamily: {
        // Inter for UI text: a neutral, high-legibility grotesque that reads cleanly at the
        // 12-14px sizes this console is built on. Declared explicitly rather than inheriting
        // Tailwind's system stack, which rendered a different typeface per operator OS.
        sans: [
          'Inter Variable',
          'Inter',
          'ui-sans-serif',
          'system-ui',
          '-apple-system',
          'Segoe UI',
          'Roboto',
          'Helvetica Neue',
          'Arial',
          'sans-serif',
        ],
        // JetBrains Mono for machine identifiers (device serials, SHA-256 digests, certificate
        // thumbprints). Chosen for disambiguated glyphs - slashed zero, seriffed 1, distinct
        // l/I - because operators transcribe these values against physical hardware.
        mono: [
          'JetBrains Mono Variable',
          'JetBrains Mono',
          'ui-monospace',
          'SFMono-Regular',
          'Menlo',
          'Consolas',
          'Liberation Mono',
          'monospace',
        ],
      },
      // Type scale pinned explicitly so it is an intentional contract rather than an inherited
      // default. Every step pairs a size with a line height that is a multiple of 4px, keeping
      // text on the same 4/8pt grid as the spacing scale. Values match Tailwind's defaults, so
      // pinning them changes no rendering today - it just makes an off-scale `text-[15px]`
      // visible in review.
      fontSize: {
        xs: ['0.75rem', { lineHeight: '1rem' }],       // 12/16 - badges, uppercase column headers
        sm: ['0.875rem', { lineHeight: '1.25rem' }],   // 14/20 - body text, labels, buttons
        base: ['1rem', { lineHeight: '1.5rem' }],      // 16/24 - page title
        lg: ['1.125rem', { lineHeight: '1.75rem' }],   // 18/28 - card and dialog titles
        xl: ['1.25rem', { lineHeight: '1.75rem' }],    // 20/28 - section headings
        '2xl': ['1.5rem', { lineHeight: '2rem' }],     // 24/32 - secondary stat values
        '3xl': ['1.875rem', { lineHeight: '2.25rem' }],// 30/36 - dashboard stat values
      },
      colors: {
        border: 'hsl(var(--border))',
        input: 'hsl(var(--input))',
        ring: 'rgb(var(--color-primary) / <alpha-value>)',
        background: 'rgb(var(--brand-page-bg) / <alpha-value>)',
        foreground: 'hsl(var(--foreground))',
        primary: {
          DEFAULT: 'rgb(var(--color-primary) / <alpha-value>)',
          foreground: 'hsl(var(--primary-foreground))',
        },
        secondary: {
          DEFAULT: 'hsl(var(--secondary))',
          foreground: 'hsl(var(--secondary-foreground))',
        },
        destructive: {
          DEFAULT: 'hsl(var(--destructive))',
          foreground: 'hsl(var(--destructive-foreground))',
        },
        muted: {
          DEFAULT: 'hsl(var(--muted))',
          foreground: 'hsl(var(--muted-foreground))',
        },
        accent: {
          DEFAULT: 'hsl(var(--accent))',
          foreground: 'hsl(var(--accent-foreground))',
        },
        popover: {
          DEFAULT: 'hsl(var(--popover))',
          foreground: 'hsl(var(--popover-foreground))',
        },
        card: {
          DEFAULT: 'rgb(var(--brand-card-bg) / <alpha-value>)',
          foreground: 'hsl(var(--card-foreground))',
        },
        sidebar: {
          DEFAULT: 'rgb(var(--brand-sidebar-bg) / <alpha-value>)',
          foreground: 'hsl(var(--sidebar-foreground))',
          accent: 'hsl(var(--sidebar-accent))',
          'accent-foreground': 'hsl(var(--sidebar-accent-foreground))',
          border: 'hsl(var(--sidebar-border))',
        },
        header: {
          DEFAULT: 'rgb(var(--brand-header-bg) / <alpha-value>)',
        },
        // Runtime-branded accent (FR-038), kept separate from shadcn neutral `accent`.
        brand: {
          primary: 'rgb(var(--color-primary) / <alpha-value>)',
          accent: 'rgb(var(--color-accent) / <alpha-value>)',
        },
      },
      borderRadius: {
        lg: 'var(--radius)',
        md: 'calc(var(--radius) - 2px)',
        sm: 'calc(var(--radius) - 4px)',
      },
      keyframes: {
        'accordion-down': {
          from: { height: '0' },
          to: { height: 'var(--radix-accordion-content-height)' },
        },
        'accordion-up': {
          from: { height: 'var(--radix-accordion-content-height)' },
          to: { height: '0' },
        },
        // Toast slide/fade in from the bottom-right (Azure/Intune-style notifications).
        'toast-in': {
          from: { opacity: '0', transform: 'translateY(0.75rem) scale(0.98)' },
          to: { opacity: '1', transform: 'translateY(0) scale(1)' },
        },
        'toast-out': {
          from: { opacity: '1', transform: 'translateY(0) scale(1)' },
          to: { opacity: '0', transform: 'translateX(1rem) scale(0.98)' },
        },
        // Success/failure icon "pop" used by toasts and button status indicators.
        pop: {
          '0%': { opacity: '0', transform: 'scale(0.5)' },
          '60%': { opacity: '1', transform: 'scale(1.15)' },
          '100%': { opacity: '1', transform: 'scale(1)' },
        },
        // Indeterminate sliding fill for the branded splash screen's progress bar.
        'loading-bar': {
          from: { transform: 'translateX(-100%)' },
          to: { transform: 'translateX(250%)' },
        },
        // Shared entry for anything that pops over the page from a trigger: select option lists,
        // the account menu. Scales from the top edge rather than the centre so the panel reads as
        // unfolding out of the control it belongs to.
        'popover-in': {
          from: { opacity: '0', transform: 'translateY(-0.25rem) scale(0.98)' },
          to: { opacity: '1', transform: 'translateY(0) scale(1)' },
        },
      },
      animation: {
        'accordion-down': 'accordion-down 0.2s ease-out',
        'accordion-up': 'accordion-up 0.2s ease-out',
        'toast-in': 'toast-in 0.22s cubic-bezier(0.22, 1, 0.36, 1)',
        'toast-out': 'toast-out 0.18s ease-in forwards',
        pop: 'pop 0.28s cubic-bezier(0.22, 1, 0.36, 1)',
        // 1.4s, not the 0.35s this started at. The fill travels 350% of the track per cycle, so
        // at 0.35s it crossed roughly three times a second - fast enough to read as an alarm
        // rather than as work in progress, on a screen whose entire job is to say "waiting".
        'loading-bar': 'loading-bar 1.4s ease-in-out infinite',
        'popover-in': 'popover-in 0.12s cubic-bezier(0.22, 1, 0.36, 1)',
      },
    },
  },
  plugins: [],
};

