import js from '@eslint/js';
import globals from 'globals';
import reactHooks from 'eslint-plugin-react-hooks';
import reactRefresh from 'eslint-plugin-react-refresh';
import tseslint from 'typescript-eslint';

export default tseslint.config(
  // Ignore build artefacts, compiled output and files not in tsconfig
  { ignores: ['dist', 'node_modules', 'coverage', '**/*.d.ts', '**/*.js', 'tailwind.config.ts', 'postcss.config.*', 'vite.config.ts'] },
  {
    // Use recommendedTypeChecked. strictTypeChecked adds rules that conflict
    // with common React patterns (no-misused-promises on JSX onClick, etc.)
    extends: [js.configs.recommended, ...tseslint.configs.recommendedTypeChecked],
    files: ['**/*.{ts,tsx}'],
    languageOptions: {
      ecmaVersion: 2022,
      globals: globals.browser,
      parserOptions: {
        project: ['./tsconfig.json'],
        tsconfigRootDir: import.meta.dirname,
      },
    },
    plugins: {
      'react-hooks': reactHooks,
      'react-refresh': reactRefresh,
    },
    rules: {
      ...reactHooks.configs.recommended.rules,
      // eslint-plugin-react-hooks v7 turns the React Compiler rules on as part of
      // "recommended". Two of them fire across our existing pages and are opt-in
      // improvements rather than correctness bugs, so they stay off for now:
      //
      // - set-state-in-effect flags the ordinary "fetch on mount in useEffect, then
      //   setState with the result" pattern we use on every data-backed page. Silencing
      //   it properly means moving those pages onto react-query, which is a separate
      //   piece of work, not something to bundle into a lint upgrade.
      // - immutability flags the mutually-recursive poll scheduling in SessionsPage.
      //
      // Revisit both when the pages move to react-query.
      'react-hooks/set-state-in-effect': 'off',
      'react-hooks/immutability': 'off',
      'react-refresh/only-export-components': ['warn', { allowConstantExport: true }],
      '@typescript-eslint/no-unused-vars': ['error', { argsIgnorePattern: '^_' }],
      // Allow async functions as JSX event handlers (standard React pattern)
      '@typescript-eslint/no-misused-promises': ['error', { checksVoidReturn: { attributes: false } }],
      // Allow numbers and undefined in template literals (common in JSX)
      '@typescript-eslint/restrict-template-expressions': ['error', { allowNumber: true, allowBoolean: true }],
    },
  },
);
