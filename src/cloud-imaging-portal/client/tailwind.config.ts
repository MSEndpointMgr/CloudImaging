/** @type {import('tailwindcss').Config} */
export default {
  content: ['./index.html', './src/**/*.{js,ts,jsx,tsx}'],
  theme: {
    extend: {
      // Custom CSS variables injected at runtime for branding (FR-038, FR-039)
      colors: {
        brand: {
          primary: 'var(--brand-primary, #0078d4)',
          accent: 'var(--brand-accent, #005a9e)',
        },
      },
    },
  },
  plugins: [],
};
