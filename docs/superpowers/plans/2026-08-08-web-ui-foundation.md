# Web UI Spec 1 (Foundation) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build the React SPA foundation — toolchain, design system, typed API client with single-flight token refresh, auth screens, and app shell — served same-origin by the existing ASP.NET Core API.

**Architecture:** A Vite + React + TypeScript app at `src/JiraUnpacked.Web/` builds into `src/JiraUnpacked.Api/wwwroot/`, which the API serves; in development the Vite dev server proxies `/api` to the running API. Because the browser only ever sees one origin, **no CORS policy exists anywhere**. Token handling is deliberately split: a React-free `auth/session.ts` owns the tokens and the single-flight refresh, `api/client.ts` owns HTTP and knows nothing about React, and a thin `AuthContext` adapts them for components — so the highest-risk logic is unit-testable without rendering.

**Tech Stack:** React 19 · TypeScript (strict) · Vite 6 · Tailwind CSS v4 (`@tailwindcss/vite`) · React Router 7 · TanStack Query 5 · Vitest + React Testing Library + jsdom + MSW. Node 24 / npm 11.

## Global Constraints

- **No backend behaviour change** beyond the three static-file lines in `Program.cs` (Task WF2). No controller, service, entity, migration, or auth-logic edits.
- **No CORS policy anywhere** — same-origin serving is the design. Adding `AddCors`/`UseCors` is a defect.
- The FR-B2 gate `tests/JiraUnpacked.Tests/Tenancy/TenantIsolationGateTests.cs` stays **untouched and green**.
- The full .NET suite (**276 tests**) must stay green. A changed status code after WF2 is a **design defect to report**, never a test to update.
- **Access token lives in memory only** — never `localStorage`, `sessionStorage`, cookies, or any persistent store. **No token is ever logged** (no `console.log` of a token, request body, or auth header).
- **Runtime dependencies are exactly:** `react`, `react-dom`, `react-router-dom`, `@tanstack/react-query`, `tailwindcss` + `@tailwindcss/vite`. No component library, no Redux/Zustand, no i18n, no SSR/PWA, no Playwright.
- **Tests target logic, not markup.** No snapshot tests. Test the refresh/single-flight, error mapping, password meter, and route guards.
- TypeScript `strict: true`. All API enums serialise **by name** — model them as TS string unions.
- Password policy mirrored exactly from `src/JiraUnpacked.Domain/Security/PasswordPolicy.cs`: **MinLength 12, at least 3 of 4 character classes**.
- Every task ends with a commit. Never push, open a PR, or merge.

---

## File Structure

| File | Responsibility |
|---|---|
| `src/JiraUnpacked.Web/vite.config.ts` | Build + dev proxy + Vitest config |
| `src/JiraUnpacked.Web/src/api/errors.ts` | `ApiError` + normalise every error body shape |
| `src/JiraUnpacked.Web/src/api/client.ts` | `apiFetch<T>` + the `AuthBridge` seam (no React) |
| `src/JiraUnpacked.Web/src/api/auth.ts` | Typed auth endpoint wrappers |
| `src/JiraUnpacked.Web/src/auth/session.ts` | Token storage + **single-flight refresh** (no React) |
| `src/JiraUnpacked.Web/src/auth/password.ts` | Client mirror of the server password policy |
| `src/JiraUnpacked.Web/src/auth/AuthContext.tsx` | React adapter over `session.ts`; registers the bridge |
| `src/JiraUnpacked.Web/src/auth/RequireAuth.tsx` | Route guard |
| `src/JiraUnpacked.Web/src/ui/*.tsx` | 11 presentational primitives, no API knowledge |
| `src/JiraUnpacked.Web/src/theme.css` | CSS custom properties, light + dark |
| `src/JiraUnpacked.Web/src/routes/*.tsx` | Login, Register, Verify, Home, NotFound |
| `src/JiraUnpacked.Web/src/shell/*.tsx` | AppShell, SideNav, UserMenu |
| `src/JiraUnpacked.Api/Program.cs` | +3 lines: static files, `/api` 404 terminal, SPA fallback |
| `.github/workflows/ci.yml` | +1 `web` job |

**The dependency seam that avoids a cycle:** `client.ts` must attach a token and trigger a refresh, but `session.ts` must call `apiFetch` to perform that refresh. Rather than import each other, `client.ts` exposes `setAuthBridge()`; `session.ts` registers itself. This keeps both modules React-free and independently testable.

---

## Task WF1: Scaffold, toolchain, and CI

**Files:**
- Create: `src/JiraUnpacked.Web/package.json`, `tsconfig.json`, `tsconfig.node.json`, `vite.config.ts`, `eslint.config.js`, `index.html`, `src/main.tsx`, `src/App.tsx`, `src/theme.css`, `src/test/setup.ts`, `src/test/handlers.ts`, `src/smoke.test.ts`
- Modify: `.gitignore`, `.github/workflows/ci.yml`

**Interfaces:**
- Produces: the `npm` scripts `dev`, `build`, `test`, `typecheck`, `lint`; the MSW `server` export from `src/test/setup.ts` that every later test file relies on.

- [ ] **Step 1: Create the project directory and `package.json`**

```json
{
  "name": "jiraunpacked-web",
  "private": true,
  "version": "0.0.0",
  "type": "module",
  "scripts": {
    "dev": "vite",
    "build": "tsc -b && vite build",
    "preview": "vite preview",
    "test": "vitest run",
    "test:watch": "vitest",
    "typecheck": "tsc -b --noEmit",
    "lint": "eslint ."
  },
  "dependencies": {
    "@tanstack/react-query": "^5.62.0",
    "react": "^19.0.0",
    "react-dom": "^19.0.0",
    "react-router-dom": "^7.1.0"
  },
  "devDependencies": {
    "@eslint/js": "^9.17.0",
    "@tailwindcss/vite": "^4.0.0",
    "@testing-library/jest-dom": "^6.6.0",
    "@testing-library/react": "^16.1.0",
    "@testing-library/user-event": "^14.5.2",
    "@types/react": "^19.0.0",
    "@types/react-dom": "^19.0.0",
    "@vitejs/plugin-react": "^4.3.4",
    "eslint": "^9.17.0",
    "eslint-plugin-react-hooks": "^5.1.0",
    "globals": "^15.14.0",
    "jsdom": "^25.0.1",
    "msw": "^2.7.0",
    "tailwindcss": "^4.0.0",
    "typescript": "^5.7.2",
    "typescript-eslint": "^8.18.0",
    "vite": "^6.0.0",
    "vitest": "^2.1.8"
  }
}
```

- [ ] **Step 2: Create `tsconfig.json` and `tsconfig.node.json`**

`tsconfig.json`:
```json
{
  "compilerOptions": {
    "target": "ES2022",
    "lib": ["ES2022", "DOM", "DOM.Iterable"],
    "module": "ESNext",
    "moduleResolution": "bundler",
    "jsx": "react-jsx",
    "strict": true,
    "noUnusedLocals": true,
    "noUnusedParameters": true,
    "noFallthroughCasesInSwitch": true,
    "noEmit": true,
    "skipLibCheck": true,
    "isolatedModules": true,
    "resolveJsonModule": true,
    "types": ["vitest/globals", "@testing-library/jest-dom"]
  },
  "include": ["src"]
}
```

`tsconfig.node.json`:
```json
{
  "compilerOptions": {
    "target": "ES2022",
    "lib": ["ES2023"],
    "module": "ESNext",
    "moduleResolution": "bundler",
    "strict": true,
    "noEmit": true,
    "skipLibCheck": true,
    "isolatedModules": true
  },
  "include": ["vite.config.ts"]
}
```

Note: `"tsc -b"` in the build script requires project references or standalone configs; using `tsc -b` with these two standalone configs works because each has `noEmit`. If `tsc -b` errors about references, change the `build` script to `tsc --noEmit && vite build` and `typecheck` to `tsc --noEmit`.

- [ ] **Step 3: Create `vite.config.ts` (proxy + build output + Vitest)**

```ts
/// <reference types="vitest/config" />
import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';
import tailwindcss from '@tailwindcss/vite';

export default defineConfig({
  plugins: [react(), tailwindcss()],
  server: {
    port: 5173,
    proxy: {
      // Same-origin from the browser's point of view => no CORS anywhere.
      '/api': {
        target: 'https://localhost:7191',
        changeOrigin: true,
        secure: false, // the ASP.NET dev certificate is self-signed
      },
    },
  },
  build: {
    outDir: '../JiraUnpacked.Api/wwwroot',
    emptyOutDir: true,
  },
  test: {
    environment: 'jsdom',
    globals: true,
    setupFiles: ['./src/test/setup.ts'],
    css: false,
  },
});
```

- [ ] **Step 4: Create `index.html`, `src/main.tsx`, `src/App.tsx`, `src/theme.css`**

`index.html`:
```html
<!doctype html>
<html lang="en">
  <head>
    <meta charset="UTF-8" />
    <meta name="viewport" content="width=device-width, initial-scale=1.0" />
    <title>Jira Unpacked</title>
  </head>
  <body>
    <div id="root"></div>
    <script type="module" src="/src/main.tsx"></script>
  </body>
</html>
```

`src/theme.css` (placeholder in this task; Task WF3 fills in the tokens):
```css
@import "tailwindcss";
```

`src/App.tsx`:
```tsx
export default function App() {
  return <h1>Jira Unpacked</h1>;
}
```

`src/main.tsx`:
```tsx
import { StrictMode } from 'react';
import { createRoot } from 'react-dom/client';
import App from './App';
import './theme.css';

createRoot(document.getElementById('root')!).render(
  <StrictMode>
    <App />
  </StrictMode>,
);
```

- [ ] **Step 5: Create the MSW test harness**

`src/test/handlers.ts`:
```ts
import { http, HttpResponse } from 'msw';

// Default handlers are intentionally empty: each test declares the API
// behaviour it depends on via server.use(...), so no test silently relies
// on a shared fixture it cannot see.
export const handlers = [
  http.get('/api/health-placeholder', () => HttpResponse.json({ ok: true })),
];
```

`src/test/setup.ts`:
```ts
import '@testing-library/jest-dom/vitest';
import { afterAll, afterEach, beforeAll } from 'vitest';
import { setupServer } from 'msw/node';
import { handlers } from './handlers';

export const server = setupServer(...handlers);

beforeAll(() => server.listen({ onUnhandledRequest: 'error' }));
afterEach(() => {
  server.resetHandlers();
  localStorage.clear();
});
afterAll(() => server.close());
```

- [ ] **Step 6: Write the smoke test**

`src/smoke.test.ts`:
```ts
import { describe, expect, it } from 'vitest';

describe('toolchain', () => {
  it('runs TypeScript under Vitest with a DOM', () => {
    const el = document.createElement('div');
    el.textContent = 'ready';
    expect(el.textContent).toBe('ready');
  });
});
```

- [ ] **Step 7: Create `eslint.config.js`**

```js
import js from '@eslint/js';
import globals from 'globals';
import reactHooks from 'eslint-plugin-react-hooks';
import tseslint from 'typescript-eslint';

export default tseslint.config(
  { ignores: ['dist', 'coverage'] },
  {
    extends: [js.configs.recommended, ...tseslint.configs.recommended],
    files: ['**/*.{ts,tsx}'],
    languageOptions: { ecmaVersion: 2022, globals: globals.browser },
    plugins: { 'react-hooks': reactHooks },
    rules: { ...reactHooks.configs.recommended.rules },
  },
);
```

- [ ] **Step 8: Install and verify**

Run from `src/JiraUnpacked.Web/`:
```bash
npm install
npm run typecheck
npm run lint
npm run test
npm run build
```
Expected: all five succeed. `npm run build` writes `src/JiraUnpacked.Api/wwwroot/index.html`.

If any dependency version above fails to resolve, install the current major instead (`npm install <pkg>@latest`) and record the resolved version in the task report — do not downgrade React below 19 or Tailwind below 4.

- [ ] **Step 9: Ignore the build output**

Append to `.gitignore` (note `node_modules/` is already ignored at line 292; do **not** uncomment the generic `wwwroot/` at line 41):

```gitignore

# Web UI build output (Vite writes the SPA bundle into the API's wwwroot)
src/JiraUnpacked.Api/wwwroot/
```

Verify with `git status --porcelain` that no `wwwroot` or `node_modules` paths appear.

- [ ] **Step 10: Add the `web` CI job**

In `.github/workflows/ci.yml`, add a second job **as a sibling of `build-test`** (same indentation under `jobs:`):

```yaml
  web:
    name: Web Build & Test
    runs-on: ubuntu-latest
    timeout-minutes: 10
    defaults:
      run:
        working-directory: src/JiraUnpacked.Web

    steps:
      - name: Checkout
        uses: actions/checkout@v4

      - name: Setup Node 24
        uses: actions/setup-node@v4
        with:
          node-version: '24'
          cache: 'npm'
          cache-dependency-path: src/JiraUnpacked.Web/package-lock.json

      - name: Install
        run: npm ci

      - name: Typecheck
        run: npm run typecheck

      - name: Lint
        run: npm run lint

      - name: Test
        run: npm run test

      - name: Build
        run: npm run build
```

- [ ] **Step 11: Commit**

```bash
git add src/JiraUnpacked.Web .gitignore .github/workflows/ci.yml
git commit -m "feat(web): Vite + React + TS scaffold, test harness, and CI job"
```

---

## Task WF2: Serve the SPA from the API

**Files:**
- Modify: `src/JiraUnpacked.Api/Program.cs`
- Test: `tests/JiraUnpacked.Tests/Integration/SpaHostingTests.cs`

**Interfaces:**
- Consumes: nothing from WF1 at runtime (the changes are safe with no `wwwroot` present).
- Produces: the production hosting model — the API serves `index.html` for non-API paths.

- [ ] **Step 1: Write the failing integration test**

Create `tests/JiraUnpacked.Tests/Integration/SpaHostingTests.cs`. Follow the existing integration-test style — look at `tests/JiraUnpacked.Tests/Integration/IssueListTests.cs` for how `JiraUnpackedApiFactory` is used.

```csharp
using System.Net;
using Xunit;

namespace JiraUnpacked.Tests.Integration;

public sealed class SpaHostingTests : IClassFixture<JiraUnpackedApiFactory>
{
    private readonly JiraUnpackedApiFactory _factory;

    public SpaHostingTests(JiraUnpackedApiFactory factory) => _factory = factory;

    // An unknown /api path must stay a JSON-shaped 404. If the SPA fallback
    // swallowed it, this would be 200 with an HTML body and every client would
    // try to parse HTML as JSON.
    [Fact]
    public async Task Unknown_api_route_returns_404_not_the_spa_shell()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/definitely-not-a-route");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("<!doctype html", body, StringComparison.OrdinalIgnoreCase);
    }

    // A real endpoint must still win over the catch-all. Unauthenticated =>
    // 401, which proves routing reached the controller rather than the fallback.
    [Fact]
    public async Task Real_api_route_still_reaches_its_controller()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/auth/me");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
```

- [ ] **Step 2: Run to verify the first test fails**

Run: `dotnet test tests/JiraUnpacked.Tests --filter FullyQualifiedName~SpaHostingTests`
Expected: `Real_api_route_still_reaches_its_controller` PASSES already; `Unknown_api_route_returns_404_not_the_spa_shell` also passes **today** (no fallback exists yet). Both must still pass after Step 3 — that is the point of the test. Record the pre-change result in the report.

- [ ] **Step 3: Add the three lines to `Program.cs`**

Locate this existing block:

```csharp
app.UseHttpsRedirection();
app.UseAuthentication();
app.UseAuthorization();
```

Insert `UseStaticFiles` **before** `UseAuthentication` so bundle assets are served without an auth challenge:

```csharp
app.UseHttpsRedirection();
// Serves the built SPA from wwwroot. Before authentication so the bundle and
// its assets load without a token. No-ops when wwwroot is absent (tests, fresh
// clones), which is why the integration suite is unaffected.
app.UseStaticFiles();
app.UseAuthentication();
app.UseAuthorization();
```

Then locate the end of the pipeline:

```csharp
app.MapControllers();

app.Run();
```

and replace it with:

```csharp
app.MapControllers();
// An unknown /api path must remain a JSON 404. Without this terminal route the
// SPA fallback below would answer it with index.html and HTTP 200, and clients
// would parse HTML as JSON. Catch-all routes have the lowest precedence, so
// every real controller route still wins.
app.Map("/api/{**rest}", () => Results.NotFound());
// Any other unmatched path serves the SPA shell so client-side routing works on
// a deep-link refresh.
app.MapFallbackToFile("index.html");

app.Run();
```

- [ ] **Step 4: Run the SPA hosting tests**

Run: `dotnet test tests/JiraUnpacked.Tests --filter FullyQualifiedName~SpaHostingTests`
Expected: both PASS.

- [ ] **Step 5: Run the FULL .NET suite — the critical gate**

Run: `dotnet test JiraUnpacked.sln --configuration Release`
Expected: **278 passed** (the 276 existing plus the 2 new), **0 failed**.

**If any previously-passing test now fails or returns a different status code, STOP.** Do not modify the test. Report it as a design defect in this plan — it means the fallback is shadowing a route the design assumed it would not.

- [ ] **Step 6: Confirm no CORS was introduced**

Run: `grep -rn "AddCors\|UseCors" src/`
Expected: no matches.

- [ ] **Step 7: Commit**

```bash
git add src/JiraUnpacked.Api/Program.cs tests/JiraUnpacked.Tests/Integration/SpaHostingTests.cs
git commit -m "feat(api): serve the SPA from wwwroot without shadowing /api routes"
```

---

## Task WF3: Design system and theme

**Files:**
- Create: `src/JiraUnpacked.Web/src/theme.css` (replace WF1 placeholder), `src/ui/Button.tsx`, `Field.tsx`, `Select.tsx`, `Card.tsx`, `Table.tsx`, `Badge.tsx`, `Spinner.tsx`, `EmptyState.tsx`, `ErrorState.tsx`, `Toast.tsx`, `Modal.tsx`, `src/ui/useTheme.ts`, `src/ui/ThemeToggle.tsx`, `src/ui/index.ts`
- Test: `src/ui/useTheme.test.ts`, `src/ui/Modal.test.tsx`

**Interfaces:**
- Produces: `useTheme(): { theme: 'light' | 'dark'; toggle(): void }`; `<Button variant="primary"|"secondary"|"danger" loading?>`; `<Field label id error?>`; `<Badge tone="neutral"|"success"|"warning"|"danger">`; `<Modal open onClose title>`; `<Toast>`; `<EmptyState title description? action?>`; `<ErrorState error onRetry?>`; `<Spinner>`; `<Card>`; `<Table>`.

- [ ] **Step 1: Write `src/theme.css`**

```css
@import "tailwindcss";

/* Light is the default; dark is applied by the `dark` class on <html>, which
   useTheme sets. Tailwind v4 needs this custom variant to target that class. */
@custom-variant dark (&:where(.dark, .dark *));

:root {
  --ju-bg: #ffffff;
  --ju-surface: #f8fafc;
  --ju-border: #e2e8f0;
  --ju-text: #0f172a;
  --ju-muted: #64748b;
  --ju-primary: #4338ca;
  --ju-primary-text: #ffffff;
  --ju-danger: #b91c1c;
  --ju-success: #15803d;
  --ju-warning: #b45309;
}

.dark {
  --ju-bg: #0b1120;
  --ju-surface: #111827;
  --ju-border: #1f2937;
  --ju-text: #e5e7eb;
  --ju-muted: #9ca3af;
  --ju-primary: #818cf8;
  --ju-primary-text: #0b1120;
  --ju-danger: #f87171;
  --ju-success: #4ade80;
  --ju-warning: #fbbf24;
}

body {
  background-color: var(--ju-bg);
  color: var(--ju-text);
}

/* Respect users who ask for reduced motion. */
@media (prefers-reduced-motion: reduce) {
  *, *::before, *::after {
    animation-duration: 0.01ms !important;
    transition-duration: 0.01ms !important;
  }
}
```

- [ ] **Step 2: Write the failing theme test**

`src/ui/useTheme.test.ts`:
```ts
import { act, renderHook } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { THEME_KEY, useTheme } from './useTheme';

describe('useTheme', () => {
  beforeEach(() => {
    localStorage.clear();
    document.documentElement.classList.remove('dark');
  });

  it('defaults to the OS preference when nothing is stored', () => {
    vi.stubGlobal('matchMedia', (q: string) => ({
      matches: q.includes('dark'),
      media: q, addEventListener() {}, removeEventListener() {},
    }));

    const { result } = renderHook(() => useTheme());

    expect(result.current.theme).toBe('dark');
    expect(document.documentElement.classList.contains('dark')).toBe(true);
  });

  it('persists an explicit choice and applies the dark class', () => {
    vi.stubGlobal('matchMedia', (q: string) => ({
      matches: false, media: q, addEventListener() {}, removeEventListener() {},
    }));

    const { result } = renderHook(() => useTheme());
    expect(result.current.theme).toBe('light');

    act(() => result.current.toggle());

    expect(result.current.theme).toBe('dark');
    expect(localStorage.getItem(THEME_KEY)).toBe('dark');
    expect(document.documentElement.classList.contains('dark')).toBe(true);
  });

  it('prefers the stored choice over the OS preference', () => {
    localStorage.setItem(THEME_KEY, 'light');
    vi.stubGlobal('matchMedia', (q: string) => ({
      matches: true, media: q, addEventListener() {}, removeEventListener() {},
    }));

    const { result } = renderHook(() => useTheme());

    expect(result.current.theme).toBe('light');
  });
});
```

- [ ] **Step 3: Run to verify it fails**

Run: `npm run test -- useTheme`
Expected: FAIL — cannot resolve `./useTheme`.

- [ ] **Step 4: Implement `useTheme.ts`**

```ts
import { useCallback, useEffect, useState } from 'react';

export const THEME_KEY = 'ju.theme';
export type Theme = 'light' | 'dark';

function initialTheme(): Theme {
  const stored = localStorage.getItem(THEME_KEY);
  if (stored === 'light' || stored === 'dark') return stored;
  return window.matchMedia('(prefers-color-scheme: dark)').matches ? 'dark' : 'light';
}

export function useTheme(): { theme: Theme; toggle: () => void } {
  const [theme, setTheme] = useState<Theme>(initialTheme);

  useEffect(() => {
    document.documentElement.classList.toggle('dark', theme === 'dark');
  }, [theme]);

  const toggle = useCallback(() => {
    setTheme((current) => {
      const next: Theme = current === 'dark' ? 'light' : 'dark';
      localStorage.setItem(THEME_KEY, next);
      return next;
    });
  }, []);

  return { theme, toggle };
}
```

- [ ] **Step 5: Run to verify it passes**

Run: `npm run test -- useTheme`
Expected: PASS (3 tests).

- [ ] **Step 6: Implement the 11 primitives**

Each is small and presentational. Write them with Tailwind utility classes referencing the CSS variables via arbitrary values (e.g. `bg-[var(--ju-surface)]`). `Button.tsx` is the exemplar — follow its shape (typed props extending the native element, no API imports, accessible by default) for the rest:

```tsx
import type { ButtonHTMLAttributes, ReactNode } from 'react';
import { Spinner } from './Spinner';

type Variant = 'primary' | 'secondary' | 'danger';

const VARIANTS: Record<Variant, string> = {
  primary: 'bg-[var(--ju-primary)] text-[var(--ju-primary-text)] hover:opacity-90',
  secondary: 'bg-[var(--ju-surface)] text-[var(--ju-text)] border border-[var(--ju-border)] hover:bg-[var(--ju-bg)]',
  danger: 'bg-[var(--ju-danger)] text-white hover:opacity-90',
};

interface ButtonProps extends ButtonHTMLAttributes<HTMLButtonElement> {
  variant?: Variant;
  loading?: boolean;
  children: ReactNode;
}

export function Button({ variant = 'primary', loading = false, disabled, children, className = '', ...rest }: ButtonProps) {
  return (
    <button
      {...rest}
      disabled={disabled || loading}
      aria-busy={loading || undefined}
      className={`inline-flex items-center justify-center gap-2 rounded-md px-4 py-2 text-sm font-medium transition disabled:cursor-not-allowed disabled:opacity-60 ${VARIANTS[variant]} ${className}`}
    >
      {loading && <Spinner aria-label="Working" />}
      {children}
    </button>
  );
}
```

Required behaviour for the rest:

- `Field.tsx` — props `label: string`, `id: string`, `error?: string`, `hint?: string`, `children`. Renders `<label htmlFor={id}>`; when `error` is set, renders it in `--ju-danger` with `role="alert"` and wires `aria-describedby` to the error element's id.
- `Select.tsx` — a styled native `<select>` forwarding all native props.
- `Card.tsx` — surface container: `bg-[var(--ju-surface)]`, 1px `--ju-border`, rounded, padded.
- `Table.tsx` — exports `Table`, `THead`, `TBody`, `TR`, `TH`, `TD`; the wrapper sets `overflow-x-auto` so wide tables scroll rather than breaking the page on a phone.
- `Badge.tsx` — props `tone?: 'neutral' | 'success' | 'warning' | 'danger'` (default `neutral`).
- `Spinner.tsx` — an SVG spinner with `role="status"` and an `aria-label` defaulting to `"Loading"`.
- `EmptyState.tsx` — props `title: string`, `description?: string`, `action?: ReactNode`.
- `ErrorState.tsx` — props `title: string`, `detail?: string`, `onRetry?: () => void`. Renders the title in `--ju-danger` inside `role="alert"`, the detail beneath when present, and a retry `Button` when `onRetry` is given. **It takes plain strings, not an error object** — `ui/` must not import from `api/`, so callers do `<ErrorState title={e instanceof ApiError ? e.title : 'Something went wrong.'} />`. This keeps the primitives free of API knowledge and lets WF3 land before WF4.
- `Toast.tsx` — props `message: string`, `tone?: 'success' | 'danger'`, `onDismiss: () => void`; container has `role="status"` and `aria-live="polite"`.
- `Modal.tsx` — props `open: boolean`, `onClose: () => void`, `title: string`, `children`. Renders nothing when closed. When open: `role="dialog"`, `aria-modal="true"`, `aria-labelledby` pointing at the title, closes on `Escape`, and focuses the first focusable element on open.

`src/ui/index.ts` re-exports all of them.

- [ ] **Step 7: Write the Modal behaviour test**

`src/ui/Modal.test.tsx`:
```tsx
import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it, vi } from 'vitest';
import { Modal } from './Modal';

describe('Modal', () => {
  it('renders nothing when closed', () => {
    render(<Modal open={false} onClose={() => {}} title="Confirm">body</Modal>);
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument();
  });

  it('exposes an accessible dialog and closes on Escape', async () => {
    const onClose = vi.fn();
    const user = userEvent.setup();
    render(<Modal open onClose={onClose} title="Confirm">body</Modal>);

    expect(screen.getByRole('dialog')).toHaveAccessibleName('Confirm');

    await user.keyboard('{Escape}');
    expect(onClose).toHaveBeenCalledTimes(1);
  });
});
```

- [ ] **Step 8: Run the full web suite**

Run: `npm run test && npm run typecheck && npm run lint`
Expected: all pass.

- [ ] **Step 9: Commit**

```bash
git add src/JiraUnpacked.Web/src/theme.css src/JiraUnpacked.Web/src/ui
git commit -m "feat(web): theme tokens, light/dark toggle, and UI primitives"
```

---

## Task WF4: API client and error normalisation

**Files:**
- Create: `src/JiraUnpacked.Web/src/api/errors.ts`, `src/api/client.ts`, `src/api/auth.ts`
- Test: `src/api/errors.test.ts`, `src/api/client.test.ts`

**Interfaces:**
- Produces:
  - `class ApiError extends Error { readonly status: number; readonly title: string; readonly detail?: string }`
  - `toApiError(response: Response): Promise<ApiError>`
  - `interface AuthBridge { getAccessToken(): string | null; refresh(): Promise<boolean>; onAuthFailure(): void }`
  - `setAuthBridge(bridge: AuthBridge | null): void`
  - `apiFetch<T>(path: string, options?: ApiFetchOptions): Promise<T>` where `ApiFetchOptions = { method?: string; body?: unknown; signal?: AbortSignal; skipAuthRetry?: boolean }`
  - `TokenResponse = { accessToken: string; expiresAtUtc: string; refreshToken: string }`
  - auth wrappers: `login`, `register`, `verify`, `resendVerification`, `refreshTokens`, `logout`, `getMe`

- [ ] **Step 1: Write the failing error-normalisation test**

`src/api/errors.test.ts`:
```ts
import { describe, expect, it } from 'vitest';
import { ApiError, RATE_LIMIT_MESSAGE, UPSTREAM_JIRA_MESSAGE, toApiError } from './errors';

function res(status: number, body?: unknown, contentType = 'application/json'): Response {
  if (body === undefined) return new Response(null, { status });
  return new Response(JSON.stringify(body), { status, headers: { 'content-type': contentType } });
}

describe('toApiError', () => {
  it('reads RFC7807 ProblemDetails title and detail', async () => {
    const e = await toApiError(res(400, { title: 'Unknown field.', detail: 'field=nope' }));
    expect(e).toBeInstanceOf(ApiError);
    expect(e.status).toBe(400);
    expect(e.title).toBe('Unknown field.');
    expect(e.detail).toBe('field=nope');
  });

  it('reads the plain {error} shape', async () => {
    const e = await toApiError(res(400, { error: 'bucket must be one of: day, week, month' }));
    expect(e.title).toBe('bucket must be one of: day, week, month');
  });

  it('falls back to a status message for an empty body', async () => {
    const e = await toApiError(res(404));
    expect(e.status).toBe(404);
    expect(e.title).toBe('Not found.');
  });

  it('falls back for a non-JSON body without throwing', async () => {
    const e = await toApiError(new Response('<html>oops</html>', {
      status: 500, headers: { 'content-type': 'text/html' },
    }));
    expect(e.status).toBe(500);
    expect(e.title).toBe('Something went wrong on the server.');
  });

  it('maps 429 to the rate-limit message regardless of body', async () => {
    const e = await toApiError(res(429, { title: 'Too Many Requests' }));
    expect(e.title).toBe(RATE_LIMIT_MESSAGE);
  });

  it('maps 502 to the upstream-Jira message', async () => {
    const e = await toApiError(res(502, { title: 'Bad Gateway' }));
    expect(e.title).toBe(UPSTREAM_JIRA_MESSAGE);
  });
});
```

- [ ] **Step 2: Run to verify it fails**

Run: `npm run test -- errors`
Expected: FAIL — cannot resolve `./errors`.

- [ ] **Step 3: Implement `src/api/errors.ts`**

```ts
export const RATE_LIMIT_MESSAGE = 'Too many attempts — please wait a minute and try again.';
export const UPSTREAM_JIRA_MESSAGE =
  'Jira did not respond as expected. Check the connection and try again.';

export class ApiError extends Error {
  readonly status: number;
  readonly title: string;
  readonly detail?: string;

  constructor(status: number, title: string, detail?: string) {
    super(title);
    this.name = 'ApiError';
    this.status = status;
    this.title = title;
    this.detail = detail;
  }
}

function statusFallback(status: number): string {
  switch (status) {
    case 400: return 'That request was not valid.';
    case 401: return 'Your session has expired. Please sign in again.';
    case 403: return 'You do not have access to that.';
    case 404: return 'Not found.';
    case 409: return 'That conflicts with something that already exists.';
    default: return status >= 500 ? 'Something went wrong on the server.' : `Request failed (${status}).`;
  }
}

async function readJson(response: Response): Promise<unknown> {
  try {
    const text = await response.text();
    if (!text) return null;
    return JSON.parse(text) as unknown;
  } catch {
    return null; // empty or non-JSON body — the caller falls back to the status.
  }
}

/** Normalises every error shape the API produces into one typed error. */
export async function toApiError(response: Response): Promise<ApiError> {
  // These two are decided by status alone so the user gets an actionable
  // message instead of the framework's generic wording.
  if (response.status === 429) return new ApiError(429, RATE_LIMIT_MESSAGE);
  if (response.status === 502) return new ApiError(502, UPSTREAM_JIRA_MESSAGE);

  const body = await readJson(response);
  if (body && typeof body === 'object') {
    const shape = body as { title?: unknown; detail?: unknown; error?: unknown };
    if (typeof shape.title === 'string' && shape.title.length > 0) {
      return new ApiError(
        response.status,
        shape.title,
        typeof shape.detail === 'string' ? shape.detail : undefined,
      );
    }
    if (typeof shape.error === 'string' && shape.error.length > 0) {
      return new ApiError(response.status, shape.error);
    }
  }
  return new ApiError(response.status, statusFallback(response.status));
}
```

- [ ] **Step 4: Run to verify it passes**

Run: `npm run test -- errors`
Expected: PASS (6 tests).

- [ ] **Step 5: Write the failing client test**

`src/api/client.test.ts`:
```ts
import { http, HttpResponse } from 'msw';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { server } from '../test/setup';
import { ApiError } from './errors';
import { apiFetch, setAuthBridge } from './client';

describe('apiFetch', () => {
  beforeEach(() => setAuthBridge(null));

  it('returns the parsed body on success', async () => {
    server.use(http.get('/api/thing', () => HttpResponse.json({ value: 42 })));
    await expect(apiFetch<{ value: number }>('/api/thing')).resolves.toEqual({ value: 42 });
  });

  it('sends the bearer token from the bridge', async () => {
    let seen: string | null = null;
    server.use(http.get('/api/thing', ({ request }) => {
      seen = request.headers.get('authorization');
      return HttpResponse.json({ ok: true });
    }));
    setAuthBridge({ getAccessToken: () => 'tok-1', refresh: async () => false, onAuthFailure: () => {} });

    await apiFetch('/api/thing');

    expect(seen).toBe('Bearer tok-1');
  });

  it('sends no Authorization header when there is no token', async () => {
    let seen: string | null = 'unset';
    server.use(http.get('/api/thing', ({ request }) => {
      seen = request.headers.get('authorization');
      return HttpResponse.json({ ok: true });
    }));

    await apiFetch('/api/thing');

    expect(seen).toBeNull();
  });

  it('returns undefined for a 204 with no body', async () => {
    server.use(http.post('/api/thing', () => new HttpResponse(null, { status: 204 })));
    await expect(apiFetch('/api/thing', { method: 'POST' })).resolves.toBeUndefined();
  });

  it('throws a normalised ApiError on failure', async () => {
    server.use(http.get('/api/thing', () =>
      HttpResponse.json({ title: 'Nope.' }, { status: 400 })));

    await expect(apiFetch('/api/thing')).rejects.toMatchObject({ status: 400, title: 'Nope.' });
    await expect(apiFetch('/api/thing')).rejects.toBeInstanceOf(ApiError);
  });

  it('refreshes once and retries the request after a 401', async () => {
    let calls = 0;
    server.use(http.get('/api/thing', ({ request }) => {
      calls++;
      return request.headers.get('authorization') === 'Bearer good'
        ? HttpResponse.json({ ok: true })
        : new HttpResponse(null, { status: 401 });
    }));

    let token = 'stale';
    const refresh = vi.fn(async () => { token = 'good'; return true; });
    setAuthBridge({ getAccessToken: () => token, refresh, onAuthFailure: () => {} });

    await expect(apiFetch('/api/thing')).resolves.toEqual({ ok: true });
    expect(refresh).toHaveBeenCalledTimes(1);
    expect(calls).toBe(2);
  });

  it('gives up and signals auth failure when the refresh fails', async () => {
    server.use(http.get('/api/thing', () => new HttpResponse(null, { status: 401 })));
    const onAuthFailure = vi.fn();
    setAuthBridge({ getAccessToken: () => 'stale', refresh: async () => false, onAuthFailure });

    await expect(apiFetch('/api/thing')).rejects.toBeInstanceOf(ApiError);
    expect(onAuthFailure).toHaveBeenCalledTimes(1);
  });

  it('does not loop when the retry also 401s', async () => {
    let calls = 0;
    server.use(http.get('/api/thing', () => { calls++; return new HttpResponse(null, { status: 401 }); }));
    const refresh = vi.fn(async () => true);
    const onAuthFailure = vi.fn();
    setAuthBridge({ getAccessToken: () => 'x', refresh, onAuthFailure });

    await expect(apiFetch('/api/thing')).rejects.toBeInstanceOf(ApiError);
    expect(refresh).toHaveBeenCalledTimes(1);
    expect(calls).toBe(2); // original + exactly one retry
    expect(onAuthFailure).toHaveBeenCalledTimes(1);
  });

  it('never retries when skipAuthRetry is set', async () => {
    server.use(http.post('/api/auth/refresh', () => new HttpResponse(null, { status: 401 })));
    const refresh = vi.fn(async () => true);
    setAuthBridge({ getAccessToken: () => null, refresh, onAuthFailure: () => {} });

    await expect(
      apiFetch('/api/auth/refresh', { method: 'POST', body: {}, skipAuthRetry: true }),
    ).rejects.toBeInstanceOf(ApiError);
    expect(refresh).not.toHaveBeenCalled();
  });
});
```

- [ ] **Step 6: Run to verify it fails**

Run: `npm run test -- client`
Expected: FAIL — cannot resolve `./client`.

- [ ] **Step 7: Implement `src/api/client.ts`**

```ts
import { ApiError, toApiError } from './errors';

/**
 * The seam that lets the HTTP layer use tokens without importing the auth
 * module (which must call apiFetch to refresh). auth/session.ts registers
 * itself here at start-up.
 */
export interface AuthBridge {
  getAccessToken(): string | null;
  /** Single-flight: concurrent callers share one in-flight refresh. */
  refresh(): Promise<boolean>;
  onAuthFailure(): void;
}

let bridge: AuthBridge | null = null;

export function setAuthBridge(next: AuthBridge | null): void {
  bridge = next;
}

export interface ApiFetchOptions {
  method?: string;
  body?: unknown;
  signal?: AbortSignal;
  /** Set on the refresh call itself so a 401 there cannot recurse. */
  skipAuthRetry?: boolean;
}

async function send(path: string, options: ApiFetchOptions): Promise<Response> {
  const headers: Record<string, string> = {};
  const token = bridge?.getAccessToken() ?? null;
  if (token) headers.Authorization = `Bearer ${token}`;
  if (options.body !== undefined) headers['Content-Type'] = 'application/json';

  return fetch(path, {
    method: options.method ?? 'GET',
    headers,
    body: options.body === undefined ? undefined : JSON.stringify(options.body),
    signal: options.signal,
  });
}

async function parseOk<T>(response: Response): Promise<T> {
  if (response.status === 204) return undefined as T;
  const text = await response.text();
  if (!text) return undefined as T;
  return JSON.parse(text) as T;
}

export async function apiFetch<T>(path: string, options: ApiFetchOptions = {}): Promise<T> {
  const response = await send(path, options);

  if (response.status === 401 && !options.skipAuthRetry && bridge) {
    const refreshed = await bridge.refresh();
    if (!refreshed) {
      bridge.onAuthFailure();
      throw await toApiError(response);
    }
    const retry = await send(path, options);
    if (retry.status === 401) {
      // A 401 after a successful refresh is a real authorisation failure,
      // not a stale token. Stop here rather than looping.
      bridge.onAuthFailure();
      throw await toApiError(retry);
    }
    if (!retry.ok) throw await toApiError(retry);
    return parseOk<T>(retry);
  }

  if (!response.ok) throw await toApiError(response);
  return parseOk<T>(response);
}

export { ApiError };
```

- [ ] **Step 8: Run to verify it passes**

Run: `npm run test -- client`
Expected: PASS (9 tests).

- [ ] **Step 9: Implement `src/api/auth.ts`**

```ts
import { apiFetch } from './client';

export interface TokenResponse {
  accessToken: string;
  expiresAtUtc: string;
  refreshToken: string;
}

export interface MeResponse {
  sub: string | null;
  role: string | null;
  endUserId: string | null;
}

export const login = (emailOrUsername: string, password: string) =>
  apiFetch<TokenResponse>('/api/auth/login', {
    method: 'POST', body: { emailOrUsername, password }, skipAuthRetry: true,
  });

export const register = (inviteCode: string, email: string, username: string, password: string) =>
  apiFetch<void>('/api/auth/register', {
    method: 'POST', body: { inviteCode, email, username, password }, skipAuthRetry: true,
  });

export const verify = (email: string, code: string) =>
  apiFetch<void>('/api/auth/verify', {
    method: 'POST', body: { email, code }, skipAuthRetry: true,
  });

export const resendVerification = (email: string) =>
  apiFetch<void>('/api/auth/verify/resend', {
    method: 'POST', body: { email }, skipAuthRetry: true,
  });

export const refreshTokens = (refreshToken: string) =>
  apiFetch<TokenResponse>('/api/auth/refresh', {
    method: 'POST', body: { refreshToken }, skipAuthRetry: true,
  });

export const logoutRequest = (refreshToken: string) =>
  apiFetch<void>('/api/auth/logout', {
    method: 'POST', body: { refreshToken }, skipAuthRetry: true,
  });

export const getMe = () => apiFetch<MeResponse>('/api/auth/me');

export const deleteMe = () => apiFetch<void>('/api/auth/me', { method: 'DELETE' });
```

**Verify the `verify` request shape before writing it.** Open `src/JiraUnpacked.Api/Contracts/Auth/VerifyRequest.cs` and `ResendVerificationRequest.cs` and match the property names exactly. If they differ from `{email, code}` / `{email}`, use the real names and note the correction in the task report.

- [ ] **Step 10: Run the full web suite and commit**

Run: `npm run test && npm run typecheck && npm run lint`

```bash
git add src/JiraUnpacked.Web/src/api
git commit -m "feat(web): typed API client with ProblemDetails normalisation and 401 retry"
```

---

## Task WF5: Token store, single-flight refresh, and auth context

**Files:**
- Create: `src/JiraUnpacked.Web/src/auth/session.ts`, `src/auth/AuthContext.tsx`
- Test: `src/auth/session.test.ts`

**Interfaces:**
- Consumes (WF4): `apiFetch`, `setAuthBridge`, `AuthBridge`, `refreshTokens`, `logoutRequest`, `TokenResponse`, `ApiError`.
- Produces:
  - `session.ts`: `REFRESH_KEY`, `getAccessToken()`, `getRefreshToken()`, `setTokens(t: TokenResponse)`, `clearTokens()`, `refreshOnce(): Promise<boolean>`, `installAuthBridge(onAuthFailure: () => void): void`
  - `AuthContext.tsx`: `<AuthProvider>`, `useAuth(): { status: 'loading' | 'authenticated' | 'anonymous'; role: string | null; username: string | null; signIn(emailOrUsername, password): Promise<void>; signOut(): Promise<void> }`

**This task is the centrepiece.** The tests are the deliverable as much as the code.

- [ ] **Step 1: Write the failing session test**

`src/auth/session.test.ts`:
```ts
import { http, HttpResponse } from 'msw';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { server } from '../test/setup';
import { apiFetch, setAuthBridge } from '../api/client';
import {
  REFRESH_KEY, clearTokens, getAccessToken, getRefreshToken,
  installAuthBridge, refreshOnce, setTokens,
} from './session';

const token = (n: number) => ({
  accessToken: `access-${n}`, expiresAtUtc: '2099-01-01T00:00:00Z', refreshToken: `refresh-${n}`,
});

beforeEach(() => {
  clearTokens();
  setAuthBridge(null);
  localStorage.clear();
});

describe('token storage', () => {
  it('keeps the access token in memory and the refresh token in localStorage', () => {
    setTokens(token(1));

    expect(getAccessToken()).toBe('access-1');
    expect(getRefreshToken()).toBe('refresh-1');
    // The access token must never be persisted.
    expect(JSON.stringify(localStorage)).not.toContain('access-1');
  });

  it('clears both on clearTokens', () => {
    setTokens(token(1));
    clearTokens();

    expect(getAccessToken()).toBeNull();
    expect(localStorage.getItem(REFRESH_KEY)).toBeNull();
  });
});

describe('refreshOnce', () => {
  it('stores the rotated pair on success', async () => {
    localStorage.setItem(REFRESH_KEY, 'refresh-1');
    server.use(http.post('/api/auth/refresh', () => HttpResponse.json(token(2))));

    await expect(refreshOnce()).resolves.toBe(true);

    expect(getAccessToken()).toBe('access-2');
    expect(getRefreshToken()).toBe('refresh-2');
  });

  it('is single-flight: concurrent callers share ONE network refresh', async () => {
    localStorage.setItem(REFRESH_KEY, 'refresh-1');
    let hits = 0;
    server.use(http.post('/api/auth/refresh', async () => {
      hits++;
      await new Promise((r) => setTimeout(r, 20));
      return HttpResponse.json(token(2));
    }));

    const results = await Promise.all([refreshOnce(), refreshOnce(), refreshOnce()]);

    expect(results).toEqual([true, true, true]);
    expect(hits).toBe(1);
  });

  it('allows a fresh refresh after the previous one settles', async () => {
    localStorage.setItem(REFRESH_KEY, 'refresh-1');
    let hits = 0;
    server.use(http.post('/api/auth/refresh', () => { hits++; return HttpResponse.json(token(2)); }));

    await refreshOnce();
    await refreshOnce();

    expect(hits).toBe(2);
  });

  it('returns false and clears tokens when the refresh is rejected', async () => {
    localStorage.setItem(REFRESH_KEY, 'expired');
    server.use(http.post('/api/auth/refresh', () => new HttpResponse(null, { status: 401 })));

    await expect(refreshOnce()).resolves.toBe(false);

    expect(getAccessToken()).toBeNull();
    expect(getRefreshToken()).toBeNull();
  });

  it('returns false without a network call when no refresh token is stored', async () => {
    let hits = 0;
    server.use(http.post('/api/auth/refresh', () => { hits++; return HttpResponse.json(token(2)); }));

    await expect(refreshOnce()).resolves.toBe(false);

    expect(hits).toBe(0);
  });
});

describe('installAuthBridge', () => {
  it('makes N concurrent 401s trigger exactly ONE refresh', async () => {
    localStorage.setItem(REFRESH_KEY, 'refresh-1');
    setTokens({ ...token(1), refreshToken: 'refresh-1' });

    let refreshHits = 0;
    server.use(
      http.post('/api/auth/refresh', async () => {
        refreshHits++;
        await new Promise((r) => setTimeout(r, 20));
        return HttpResponse.json(token(9));
      }),
      http.get('/api/thing', ({ request }) =>
        request.headers.get('authorization') === 'Bearer access-9'
          ? HttpResponse.json({ ok: true })
          : new HttpResponse(null, { status: 401 })),
    );

    installAuthBridge(() => {});

    const all = await Promise.all([
      apiFetch('/api/thing'), apiFetch('/api/thing'), apiFetch('/api/thing'),
    ]);

    expect(all).toEqual([{ ok: true }, { ok: true }, { ok: true }]);
    expect(refreshHits).toBe(1);
  });

  it('calls onAuthFailure when the refresh cannot succeed', async () => {
    setTokens(token(1));
    const onAuthFailure = vi.fn();
    server.use(
      http.post('/api/auth/refresh', () => new HttpResponse(null, { status: 401 })),
      http.get('/api/thing', () => new HttpResponse(null, { status: 401 })),
    );

    installAuthBridge(onAuthFailure);

    await expect(apiFetch('/api/thing')).rejects.toBeInstanceOf(Error);
    expect(onAuthFailure).toHaveBeenCalledTimes(1);
    expect(getAccessToken()).toBeNull();
  });
});
```

- [ ] **Step 2: Run to verify it fails**

Run: `npm run test -- session`
Expected: FAIL — cannot resolve `./session`.

- [ ] **Step 3: Implement `src/auth/session.ts`**

```ts
import { setAuthBridge } from '../api/client';
import { refreshTokens, type TokenResponse } from '../api/auth';

export const REFRESH_KEY = 'ju.refreshToken';

/**
 * The access token is deliberately module state and never touches any
 * persistent store — a page reload re-obtains it from the refresh token.
 */
let accessToken: string | null = null;

/** The in-flight refresh, shared by every concurrent caller (single-flight). */
let inFlight: Promise<boolean> | null = null;

export function getAccessToken(): string | null {
  return accessToken;
}

export function getRefreshToken(): string | null {
  return localStorage.getItem(REFRESH_KEY);
}

export function setTokens(tokens: TokenResponse): void {
  accessToken = tokens.accessToken;
  localStorage.setItem(REFRESH_KEY, tokens.refreshToken);
}

export function clearTokens(): void {
  accessToken = null;
  localStorage.removeItem(REFRESH_KEY);
}

async function doRefresh(): Promise<boolean> {
  const stored = getRefreshToken();
  if (!stored) return false;
  try {
    // Refresh rotates: the response carries a new refresh token that
    // replaces the stored one.
    setTokens(await refreshTokens(stored));
    return true;
  } catch {
    clearTokens();
    return false;
  }
}

/**
 * Refreshes the session, collapsing concurrent callers onto one network
 * request. Without this, a page issuing several requests at once would fire
 * one refresh per 401 and — because refresh rotates — all but the first would
 * present an already-consumed token and fail.
 */
export function refreshOnce(): Promise<boolean> {
  if (inFlight) return inFlight;
  inFlight = doRefresh().finally(() => {
    inFlight = null;
  });
  return inFlight;
}

/** Wires the token lifecycle into the HTTP layer. Call once at start-up. */
export function installAuthBridge(onAuthFailure: () => void): void {
  setAuthBridge({
    getAccessToken,
    refresh: refreshOnce,
    onAuthFailure: () => {
      clearTokens();
      onAuthFailure();
    },
  });
}
```

- [ ] **Step 4: Run to verify it passes**

Run: `npm run test -- session`
Expected: PASS (9 tests). The single-flight tests are the ones that matter — if `hits` is 3 rather than 1, the shared-promise logic is wrong.

- [ ] **Step 5: Implement `src/auth/AuthContext.tsx`**

```tsx
import { createContext, useCallback, useContext, useEffect, useMemo, useState, type ReactNode } from 'react';
import { getMe, login as loginRequest, logoutRequest } from '../api/auth';
import { clearTokens, getRefreshToken, installAuthBridge, refreshOnce, setTokens } from './session';

export type AuthStatus = 'loading' | 'authenticated' | 'anonymous';

interface AuthValue {
  status: AuthStatus;
  role: string | null;
  username: string | null;
  signIn: (emailOrUsername: string, password: string) => Promise<void>;
  signOut: () => Promise<void>;
}

const AuthContext = createContext<AuthValue | null>(null);

export function AuthProvider({ children }: { children: ReactNode }) {
  const [status, setStatus] = useState<AuthStatus>('loading');
  const [role, setRole] = useState<string | null>(null);
  const [username, setUsername] = useState<string | null>(null);

  const reset = useCallback(() => {
    clearTokens();
    setRole(null);
    setUsername(null);
    setStatus('anonymous');
  }, []);

  useEffect(() => {
    installAuthBridge(reset);
  }, [reset]);

  // Boot: a stored refresh token means the session can be resumed without a
  // second sign-in. Anything else lands on the anonymous state.
  useEffect(() => {
    let cancelled = false;
    (async () => {
      if (!getRefreshToken()) {
        if (!cancelled) setStatus('anonymous');
        return;
      }
      const ok = await refreshOnce();
      if (cancelled) return;
      if (!ok) { reset(); return; }
      try {
        const me = await getMe();
        if (cancelled) return;
        setRole(me.role);
        setStatus('authenticated');
      } catch {
        if (!cancelled) reset();
      }
    })();
    return () => { cancelled = true; };
  }, [reset]);

  const signIn = useCallback(async (emailOrUsername: string, password: string) => {
    setTokens(await loginRequest(emailOrUsername, password));
    const me = await getMe();
    setRole(me.role);
    setUsername(emailOrUsername);
    setStatus('authenticated');
  }, []);

  const signOut = useCallback(async () => {
    const stored = getRefreshToken();
    try {
      if (stored) await logoutRequest(stored);
    } catch {
      // Revoking server-side is best effort; the local session must end
      // regardless, so a failed call never leaves the user signed in.
    } finally {
      reset();
    }
  }, [reset]);

  const value = useMemo<AuthValue>(
    () => ({ status, role, username, signIn, signOut }),
    [status, role, username, signIn, signOut],
  );

  return <AuthContext.Provider value={value}>{children}</AuthContext.Provider>;
}

export function useAuth(): AuthValue {
  const value = useContext(AuthContext);
  if (!value) throw new Error('useAuth must be used inside <AuthProvider>.');
  return value;
}
```

- [ ] **Step 6: Write the AuthContext boot and sign-out tests**

The spec requires two behaviours that live in the provider, not in `session.ts`: boot hydration, and sign-out clearing the session **even when the server call fails**.

`src/auth/AuthContext.test.tsx`:
```tsx
import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { http, HttpResponse } from 'msw';
import { beforeEach, describe, expect, it } from 'vitest';
import { server } from '../test/setup';
import { REFRESH_KEY, getAccessToken } from './session';
import { AuthProvider, useAuth } from './AuthContext';

function Probe() {
  const { status, role, signOut } = useAuth();
  return (
    <div>
      <span data-testid="status">{status}</span>
      <span data-testid="role">{role ?? 'none'}</span>
      <button onClick={() => void signOut()}>sign out</button>
    </div>
  );
}

const renderProbe = () => render(<AuthProvider><Probe /></AuthProvider>);

beforeEach(() => localStorage.clear());

describe('AuthProvider boot', () => {
  it('lands anonymous with no stored refresh token and makes no network call', async () => {
    renderProbe();
    expect(await screen.findByTestId('status')).toHaveTextContent('anonymous');
  });

  it('hydrates the session from a stored refresh token', async () => {
    localStorage.setItem(REFRESH_KEY, 'refresh-1');
    server.use(
      http.post('/api/auth/refresh', () => HttpResponse.json({
        accessToken: 'access-2', expiresAtUtc: '2099-01-01T00:00:00Z', refreshToken: 'refresh-2',
      })),
      http.get('/api/auth/me', () => HttpResponse.json({ sub: 'u', role: 'Admin', endUserId: 'e' })),
    );

    renderProbe();

    expect(await screen.findByText('authenticated')).toBeInTheDocument();
    expect(screen.getByTestId('role')).toHaveTextContent('Admin');
  });

  it('clears storage and goes anonymous when the stored token is rejected', async () => {
    localStorage.setItem(REFRESH_KEY, 'expired');
    server.use(http.post('/api/auth/refresh', () => new HttpResponse(null, { status: 401 })));

    renderProbe();

    expect(await screen.findByText('anonymous')).toBeInTheDocument();
    expect(localStorage.getItem(REFRESH_KEY)).toBeNull();
  });
});

describe('signOut', () => {
  it('clears the local session even when the logout call fails', async () => {
    localStorage.setItem(REFRESH_KEY, 'refresh-1');
    server.use(
      http.post('/api/auth/refresh', () => HttpResponse.json({
        accessToken: 'access-2', expiresAtUtc: '2099-01-01T00:00:00Z', refreshToken: 'refresh-2',
      })),
      http.get('/api/auth/me', () => HttpResponse.json({ sub: 'u', role: null, endUserId: 'e' })),
      // The server refuses to revoke — the local session must end anyway.
      http.post('/api/auth/logout', () => new HttpResponse(null, { status: 500 })),
    );
    renderProbe();
    await screen.findByText('authenticated');

    await userEvent.click(screen.getByRole('button', { name: /sign out/i }));

    expect(await screen.findByText('anonymous')).toBeInTheDocument();
    expect(localStorage.getItem(REFRESH_KEY)).toBeNull();
    expect(getAccessToken()).toBeNull();
  });
});
```

- [ ] **Step 7: Run to verify it passes**

Run: `npm run test -- AuthContext`
Expected: PASS (4 tests). If the first test fails with an unhandled-request error, the provider is calling `/api/auth/refresh` without a stored token — fix the provider, not the test.

- [ ] **Step 8: Run the full web suite and commit**

Run: `npm run test && npm run typecheck && npm run lint`

```bash
git add src/JiraUnpacked.Web/src/auth
git commit -m "feat(web): session store with single-flight refresh and auth context"
```

---

## Task WF6: Authentication screens

**Files:**
- Create: `src/JiraUnpacked.Web/src/auth/password.ts`, `src/routes/Login.tsx`, `src/routes/Register.tsx`, `src/routes/Verify.tsx`
- Test: `src/auth/password.test.ts`, `src/routes/Login.test.tsx`

**Interfaces:**
- Consumes (WF3–WF5): the `ui` primitives, `useAuth`, `register`, `verify`, `resendVerification`, `ApiError`.
- Produces: `assessPassword(pw: string): PasswordAssessment`, `MIN_LENGTH`, `MIN_CLASSES`; the three route components.

- [ ] **Step 1: Write the failing password-policy test**

The boundaries matter: the server rejects 11 characters and accepts 12; it rejects 2 character classes and accepts 3.

`src/auth/password.test.ts`:
```ts
import { describe, expect, it } from 'vitest';
import { MIN_CLASSES, MIN_LENGTH, assessPassword } from './password';

describe('assessPassword', () => {
  it('mirrors the server thresholds', () => {
    expect(MIN_LENGTH).toBe(12);
    expect(MIN_CLASSES).toBe(3);
  });

  it('rejects 11 characters and accepts 12', () => {
    expect(assessPassword('Abcdefg1!aa').valid).toBe(false);  // 11
    expect(assessPassword('Abcdefg1!aaa').valid).toBe(true);  // 12
  });

  it('rejects 2 character classes and accepts 3', () => {
    expect(assessPassword('abcdefghijkl').classes).toBe(1);
    expect(assessPassword('abcdefghijkL').classes).toBe(2);
    expect(assessPassword('abcdefghijkL').valid).toBe(false);
    expect(assessPassword('abcdefghijL1').classes).toBe(3);
    expect(assessPassword('abcdefghijL1').valid).toBe(true);
  });

  it('counts symbols as the fourth class', () => {
    expect(assessPassword('abcdefghijk!').classes).toBe(2);
    expect(assessPassword('ABCDEFGHIJK!').classes).toBe(2);
    expect(assessPassword('abcDEF123!@#').classes).toBe(4);
  });

  it('treats an empty password as invalid without throwing', () => {
    expect(assessPassword('').valid).toBe(false);
    expect(assessPassword('').classes).toBe(0);
  });
});
```

- [ ] **Step 2: Run to verify it fails**

Run: `npm run test -- password`
Expected: FAIL — cannot resolve `./password`.

- [ ] **Step 3: Implement `src/auth/password.ts`**

```ts
/**
 * Client mirror of src/JiraUnpacked.Domain/Security/PasswordPolicy.cs.
 * This is a hint that saves a round-trip — the server remains authoritative
 * and its validation errors are shown verbatim when they occur. The Unicode
 * property escapes match .NET's Unicode-aware char.IsLower/IsUpper/IsDigit.
 */
export const MIN_LENGTH = 12;
export const MIN_CLASSES = 3;

export interface PasswordAssessment {
  longEnough: boolean;
  classes: number;
  enoughClasses: boolean;
  valid: boolean;
}

export function assessPassword(password: string): PasswordAssessment {
  const longEnough = password.length >= MIN_LENGTH;

  let classes = 0;
  if (/\p{Ll}/u.test(password)) classes++;
  if (/\p{Lu}/u.test(password)) classes++;
  if (/\p{Nd}/u.test(password)) classes++;
  if (/[^\p{L}\p{Nd}]/u.test(password)) classes++;

  const enoughClasses = classes >= MIN_CLASSES;
  return { longEnough, classes, enoughClasses, valid: longEnough && enoughClasses };
}
```

- [ ] **Step 4: Run to verify it passes**

Run: `npm run test -- password`
Expected: PASS (5 tests).

- [ ] **Step 5: Implement the three screens**

`Login.tsx` — the accessible names below are what the tests query, so keep them:

```tsx
export default function Login() {
  const { signIn } = useAuth();
  const navigate = useNavigate();
  const location = useLocation();
  const [emailOrUsername, setEmailOrUsername] = useState('');
  const [password, setPassword] = useState('');
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  async function submit(e: FormEvent) {
    e.preventDefault();
    setBusy(true);
    setError(null);
    try {
      await signIn(emailOrUsername, password);
      // Return the user to whatever they originally asked for.
      const from = (location.state as { from?: string } | null)?.from ?? '/';
      navigate(from, { replace: true });
    } catch (err) {
      setError(err instanceof ApiError ? err.title : 'Sign-in failed.');
      setBusy(false);
    }
  }

  return (
    <Card>
      <form onSubmit={submit}>
        <h1>Sign in</h1>
        {error && <p role="alert">{error}</p>}
        <Field label="Email or username" id="identifier">
          <input id="identifier" autoComplete="username" value={emailOrUsername}
                 onChange={(e) => setEmailOrUsername(e.target.value)} />
        </Field>
        <Field label="Password" id="password">
          <input id="password" type="password" autoComplete="current-password" value={password}
                 onChange={(e) => setPassword(e.target.value)} />
        </Field>
        <Button type="submit" loading={busy}>Sign in</Button>
        <Link to="/register">Have an invite code? Create an account</Link>
      </form>
    </Card>
  );
}
```

`Register.tsx` — `Field`s in this order: invite code, email, username, password. Below the password field render the strength meter from `assessPassword`: a checklist showing "At least 12 characters" and "At least 3 of: lowercase, uppercase, digit, symbol", each ticked when satisfied. Disable submit while `!assessPassword(password).valid`. On success navigate to `/verify?email=<email>`. On an `ApiError`, render `error.title` verbatim, and `error.detail` beneath it when present — the server's password errors must reach the user unaltered.

`Verify.tsx` — reads `email` from the query string, offers a code `Field` and a submit `Button`, plus a "Resend code" `Button` that calls `resendVerification` and then disables itself for 60 seconds showing a live countdown (`Resend in 42s`). Resend is server-throttled, so a 429 renders the client's rate-limit message. On successful verification navigate to `/login`.

- [ ] **Step 6: Write the Login behaviour test**

`src/routes/Login.test.tsx`:
```tsx
import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter } from 'react-router-dom';
import { http, HttpResponse } from 'msw';
import { describe, expect, it } from 'vitest';
import { server } from '../test/setup';
import { AuthProvider } from '../auth/AuthContext';
import Login from './Login';

function renderLogin() {
  return render(
    <MemoryRouter>
      <AuthProvider><Login /></AuthProvider>
    </MemoryRouter>,
  );
}

describe('Login', () => {
  it('shows the server message when credentials are rejected', async () => {
    server.use(http.post('/api/auth/login', () =>
      HttpResponse.json({ title: 'Invalid credentials.' }, { status: 401 })));
    const user = userEvent.setup();
    renderLogin();

    await user.type(screen.getByLabelText(/email or username/i), 'someone');
    await user.type(screen.getByLabelText(/password/i), 'whatever12A!');
    await user.click(screen.getByRole('button', { name: /sign in/i }));

    expect(await screen.findByRole('alert')).toHaveTextContent('Invalid credentials.');
  });

  it('shows the rate-limit message on 429 rather than a generic failure', async () => {
    server.use(http.post('/api/auth/login', () => new HttpResponse(null, { status: 429 })));
    const user = userEvent.setup();
    renderLogin();

    await user.type(screen.getByLabelText(/email or username/i), 'someone');
    await user.type(screen.getByLabelText(/password/i), 'whatever12A!');
    await user.click(screen.getByRole('button', { name: /sign in/i }));

    expect(await screen.findByRole('alert')).toHaveTextContent(/too many attempts/i);
  });
});
```

- [ ] **Step 7: Run to verify it passes**

Run: `npm run test -- Login`
Expected: PASS (2 tests). If the labels do not match, fix the **component's** labels to be the accessible names users would read — do not weaken the query to `getByTestId`.

- [ ] **Step 8: Run the full web suite and commit**

Run: `npm run test && npm run typecheck && npm run lint`

```bash
git add src/JiraUnpacked.Web/src/auth/password.ts src/JiraUnpacked.Web/src/auth/password.test.ts src/JiraUnpacked.Web/src/routes
git commit -m "feat(web): login, register with policy meter, and verify screens"
```

---

## Task WF7: App shell, routing, and guards

**Files:**
- Create: `src/JiraUnpacked.Web/src/auth/RequireAuth.tsx`, `src/shell/AppShell.tsx`, `src/shell/SideNav.tsx`, `src/shell/UserMenu.tsx`, `src/routes/Home.tsx`, `src/routes/NotFound.tsx`
- Modify: `src/JiraUnpacked.Web/src/App.tsx`, `src/main.tsx`
- Test: `src/auth/RequireAuth.test.tsx`

**Interfaces:**
- Consumes: `useAuth`, `useTheme`, the `ui` primitives, `deleteMe`.
- Produces: the route table — `/login`, `/register`, `/verify`, `/` (guarded), `*` → NotFound.

- [ ] **Step 1: Write the failing guard test**

`src/auth/RequireAuth.test.tsx`:
```tsx
import { render, screen } from '@testing-library/react';
import { MemoryRouter, Route, Routes, useLocation } from 'react-router-dom';
import { describe, expect, it, vi } from 'vitest';
import { RequireAuth } from './RequireAuth';
import { useAuth } from './AuthContext';

// vi.mock, not vi.spyOn: ES module namespace objects are frozen, so spying on
// a live export is unreliable under Vitest. Mocking the module is the stable way.
vi.mock('./AuthContext', () => ({ useAuth: vi.fn() }));
const mockUseAuth = vi.mocked(useAuth);

function LoginProbe() {
  const location = useLocation();
  const from = (location.state as { from?: string } | null)?.from ?? 'none';
  return <div>login page (from: {from})</div>;
}

function renderAt(path: string) {
  return render(
    <MemoryRouter initialEntries={[path]}>
      <Routes>
        <Route path="/login" element={<LoginProbe />} />
        <Route element={<RequireAuth />}>
          <Route path="/secret" element={<div>secret content</div>} />
        </Route>
      </Routes>
    </MemoryRouter>,
  );
}

describe('RequireAuth', () => {
  it('renders a loading state while the session is being resolved', () => {
    mockUseAuth.mockReturnValue({
      status: 'loading', role: null, username: null,
      signIn: vi.fn(), signOut: vi.fn(),
    });

    renderAt('/secret');

    expect(screen.getByRole('status')).toBeInTheDocument();
    expect(screen.queryByText('secret content')).not.toBeInTheDocument();
  });

  it('redirects an anonymous visitor to /login preserving the attempted path', () => {
    mockUseAuth.mockReturnValue({
      status: 'anonymous', role: null, username: null,
      signIn: vi.fn(), signOut: vi.fn(),
    });

    renderAt('/secret');

    expect(screen.getByText(/login page \(from: \/secret\)/)).toBeInTheDocument();
  });

  it('renders the protected content when authenticated', () => {
    mockUseAuth.mockReturnValue({
      status: 'authenticated', role: 'EndUser', username: 'sam',
      signIn: vi.fn(), signOut: vi.fn(),
    });

    renderAt('/secret');

    expect(screen.getByText('secret content')).toBeInTheDocument();
  });
});
```

- [ ] **Step 2: Run to verify it fails**

Run: `npm run test -- RequireAuth`
Expected: FAIL — cannot resolve `./RequireAuth`.

- [ ] **Step 3: Implement `RequireAuth.tsx`**

```tsx
import { Navigate, Outlet, useLocation } from 'react-router-dom';
import { Spinner } from '../ui';
import { useAuth } from './AuthContext';

export function RequireAuth() {
  const { status } = useAuth();
  const location = useLocation();

  // 'loading' is a distinct state on purpose: redirecting during the boot
  // refresh would bounce a returning user to the sign-in page for a moment.
  if (status === 'loading') {
    return (
      <div className="flex min-h-screen items-center justify-center">
        <Spinner aria-label="Restoring your session" />
      </div>
    );
  }

  if (status === 'anonymous') {
    return <Navigate to="/login" replace state={{ from: location.pathname }} />;
  }

  return <Outlet />;
}
```

- [ ] **Step 4: Run to verify it passes**

Run: `npm run test -- RequireAuth`
Expected: PASS (3 tests).

- [ ] **Step 5: Implement the shell**

`SideNav.tsx` — a `<nav>` with an app title and a placeholder list. Later specs add Connections, Projects, Issues, Metrics, Checks; for now render a single "Home" `NavLink` to `/` and nothing else. Collapses to a top bar under `md`.

`UserMenu.tsx` — shows `username ?? 'Account'` and a dropdown with **Sign out** and **Delete account**. Sign out calls `useAuth().signOut()`. Account deletion is irreversible and cascades across every tenant table, so it is gated behind a typed confirmation — a single misclick must not be able to destroy the account:

```tsx
const CONFIRM_WORD = 'DELETE';

function DeleteAccountDialog({ open, onClose }: { open: boolean; onClose: () => void }) {
  const { signOut } = useAuth();
  const [typed, setTyped] = useState('');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  async function confirm() {
    setBusy(true);
    setError(null);
    try {
      await deleteMe();
      await signOut();
    } catch (e) {
      setError(e instanceof ApiError ? e.title : 'Could not delete the account.');
      setBusy(false);
    }
  }

  return (
    <Modal open={open} onClose={onClose} title="Delete your account">
      <p>
        This permanently deletes your account and every connection, project, and
        synced issue belonging to it. This cannot be undone.
      </p>
      <Field label={`Type ${CONFIRM_WORD} to confirm`} id="confirm-delete" error={error ?? undefined}>
        <input id="confirm-delete" value={typed} onChange={(e) => setTyped(e.target.value)} />
      </Field>
      <Button variant="danger" disabled={typed !== CONFIRM_WORD} loading={busy} onClick={() => void confirm()}>
        Delete account
      </Button>
    </Modal>
  );
}
```

`AppShell.tsx` — grid layout: `SideNav` beside an `<main>` rendering `<Outlet />`, with a top bar holding `ThemeToggle` and `UserMenu`.

`Home.tsx` — an `EmptyState` titled "No projects yet" describing that connections and projects arrive in the next release.

`NotFound.tsx` — an `EmptyState` titled "Page not found" with a link back to `/`.

- [ ] **Step 6: Wire the routes in `App.tsx`**

```tsx
import { BrowserRouter, Navigate, Route, Routes } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { AuthProvider } from './auth/AuthContext';
import { RequireAuth } from './auth/RequireAuth';
import AppShell from './shell/AppShell';
import Home from './routes/Home';
import Login from './routes/Login';
import NotFound from './routes/NotFound';
import Register from './routes/Register';
import Verify from './routes/Verify';

const queryClient = new QueryClient({
  defaultOptions: { queries: { retry: false, refetchOnWindowFocus: false } },
});

export default function App() {
  return (
    <QueryClientProvider client={queryClient}>
      <BrowserRouter>
        <AuthProvider>
          <Routes>
            <Route path="/login" element={<Login />} />
            <Route path="/register" element={<Register />} />
            <Route path="/verify" element={<Verify />} />
            <Route element={<RequireAuth />}>
              <Route element={<AppShell />}>
                <Route path="/" element={<Home />} />
              </Route>
            </Route>
            <Route path="/404" element={<NotFound />} />
            <Route path="*" element={<Navigate to="/404" replace />} />
          </Routes>
        </AuthProvider>
      </BrowserRouter>
    </QueryClientProvider>
  );
}
```

`retry: false` matters: TanStack Query's default retry would re-issue requests that `apiFetch` has already resolved through the refresh path, hiding real failures behind duplicated calls.

- [ ] **Step 7: Run the full web suite**

Run: `npm run test && npm run typecheck && npm run lint && npm run build`
Expected: all pass; the build writes to `src/JiraUnpacked.Api/wwwroot/`.

- [ ] **Step 8: Manual smoke check (documented, not automated)**

Start the API (`dotnet run --project src/JiraUnpacked.Api --launch-profile https`) and the web dev server (`npm run dev`), open `http://localhost:5173`, and confirm: the app redirects to `/login`; a wrong password shows the server's message; the theme toggle persists across a reload. Record the result in the task report.

- [ ] **Step 9: Commit**

```bash
git add src/JiraUnpacked.Web/src
git commit -m "feat(web): app shell, routing, and authenticated route guard"
```

---

## Final verification (controller, after all tasks)

- [ ] `dotnet build JiraUnpacked.sln -c Release` → 0 warnings / 0 errors.
- [ ] `dotnet test JiraUnpacked.sln -c Release` → **278 passed** (276 pre-existing + 2 from WF2), 0 failed.
- [ ] From `src/JiraUnpacked.Web/`: `npm run typecheck && npm run lint && npm run test && npm run build` → all pass.
- [ ] `grep -rn "AddCors\|UseCors" src/` → no matches (same-origin is the design).
- [ ] `grep -rn "localStorage" src/JiraUnpacked.Web/src/` → matches only in `auth/session.ts` (refresh token) and `ui/useTheme.ts` (theme). **An access token in any persistent store is a Critical defect.**
- [ ] `grep -rniE "console\.(log|debug|info)" src/JiraUnpacked.Web/src/` → no matches that print a token, header, or request body.
- [ ] Confirm `tests/JiraUnpacked.Tests/Tenancy/TenantIsolationGateTests.cs` is unmodified: `git log develop..HEAD -- tests/JiraUnpacked.Tests/Tenancy/TenantIsolationGateTests.cs` is empty.
- [ ] Confirm no migration was added: `git status --porcelain` and the diff show no files under any `Migrations/` folder.
- [ ] Confirm `git status --porcelain` lists no `node_modules/` or `wwwroot/` paths (both ignored).
- [ ] Push `feat/web-ui-foundation`; PR → develop; CI green (**both** the `Build & Test` and the new `web` job); squash-merge. The `develop → main` release is a separate, user-approved step.

## Notes for execution

- **The single-flight refresh is the highest-risk logic in this plan.** If `refreshOnce` fires one request per 401 instead of sharing one promise, the app breaks in a way that only shows under concurrency: refresh rotates the token, so the second concurrent refresh presents an already-consumed token, fails, and logs the user out mid-session. WF5's `hits` assertions are the guard — never relax them.
- **WF2's rule is absolute:** if the `Program.cs` change alters any existing test's status code, that is a defect in this design to report, not a test to update.
- **The password meter is a hint, not the authority.** The server's `PasswordValidationResult` errors must be displayed verbatim; never suppress a server error because the client thought the password was fine.
- Deferred to later specs in this epic: connections/projects/semantic config/sync (Spec 2), issue list + FR-E4 filter builder + detail (Spec 3), metrics + gap-checks + admin (Spec 4). Deferred indefinitely: httpOnly-cookie auth (a backend change), Playwright e2e.
