# ProductZero: Hello World

Status: Draft specification. This document does not authorize implementation or
start production-line missions.

## Goal

Build a small TanStack Start application named **ProductZero** in this repository.
Deliver it in three independently reviewable parts:

1. The base TanStack Start application.
2. The root page.
3. A sample application page demonstrating a TanStack Start React Server Component.

The parts are cumulative: Part 2 requires Part 1, and Part 3 requires Part 2.
Each part should be a separate work item and pull request. Later parts must
preserve the behavior and checks introduced by earlier parts.

## Shared requirements

- Use React, TypeScript, TanStack Start and its file-based routing, with Vite.
- Keep the application at the repository root. Preserve the existing
  `productionLine` directory and repository documentation.
- Use **ProductZero** as the display name and `productzero` as the private npm
  package name.
- Use npm and commit `package-lock.json`. Document the supported Node.js version
  and choose package versions compatible with it and with each other.
- Keep styling small and local. Use semantic HTML, keyboard-accessible links and
  readable layouts on narrow and wide screens.
- Do not add authentication, a database, external APIs, deployment infrastructure,
  a component library or an application-wide state framework.
- Do not introduce React Context as dependency injection.

### Experimental server-component support

TanStack Start's React Server Components support is experimental and is not
enabled by default. Part 1 must choose dependencies that can support Part 3
without replacing the application scaffold.

The current official documentation requires React 19+ and Vite 8+ for the Vite
integration, with `@vitejs/plugin-rsc` and Start's RSC option enabled. Recheck
compatibility when implementing, record the actual versions in the lockfile,
and document the experimental dependency in the application README.

Part 3 must demonstrate the actual RSC integration. Ordinary server-side
rendering, or a server function returning only JSON for a client component to
render, does not satisfy this specification. If compatible RSC support cannot be
established, report the blocker rather than silently substituting another design.

Reference: [TanStack Start React Server Components guide](https://tanstack.com/start/latest/docs/framework/react/guide/server-components).

## Part 1: Base TanStack application

### Scope

Create the smallest working ProductZero scaffold. Include the root route document
and a temporary index page so the application can be started and checked before
Part 2 supplies its home-page content. Remove unrelated scaffold demo features.

Provide npm scripts for development (`npm run dev`), production build
(`npm run build`) and TypeScript checking (`npm run typecheck`). Document how to
install dependencies, run those commands and start the production build.

### Acceptance criteria

- A fresh checkout can install dependencies using `npm ci` on the documented
  Node.js version, without a globally installed application tool.
- The package is named `productzero`, is private and has a committed lockfile.
- `npm run dev` serves a working page at `/` displaying **ProductZero**.
- `npm run typecheck` and `npm run build` succeed.
- The built application starts using the documented command and serves `/`.
- Application configuration and dependencies support the experimental RSC
  integration required by Part 3; no sample feature is required in this part.
- The README contains reproducible development and production instructions.
- Existing production-line files are preserved; generated build output,
  dependencies and local environment files are not committed.

### Verification

Record clean-install, typecheck and build results. Start both the development
server and the built application, check `/`, and stop the verification processes.

## Part 2: Root page

### Scope

Replace the placeholder index page with ProductZero's home page at `/`.

The page contains:

- Browser title **ProductZero**.
- One primary heading: **ProductZero**.
- Greeting: **Hello, world!**
- A short description identifying this as a TanStack Start sample application.

The home page must be complete on its own. Part 3 will add navigation to the
sample page; do not introduce a broken or placeholder sample link in this part.

### Acceptance criteria

- Opening `/` directly shows the specified title, heading, greeting and
  description.
- Refreshing `/` preserves the page and does not produce a route error.
- The content is readable and usable on mobile-width and desktop-width screens.
- The page produces no application errors or hydration warnings in the browser.
- Part 1's install, typecheck, build and production-start behavior still works.

### Verification

Add a focused automated page check using the repository's test tooling, adding
minimal tooling if none exists. Check the home-page content and title. Record
browser checks for layout and hydration in addition to the shared build checks.

## Part 3: Server-component sample page

### Scope

Add `/sample`, with browser title **Server Component Sample | ProductZero** and
primary heading **Server Component Sample**.

Enable the documented TanStack Start RSC integration. Render a small server
component that displays:

- **Hello from the server!**
- A server-generated UTC timestamp, labelled **Generated on the server**.

Generate the timestamp during server-component rendering, not at build time or
in the browser. It represents that render, not a live clock. Router caching may
reuse it during navigation; a changed value on every navigation is not required.

Use the simplest supported RSC path: a server function creates a renderable
server component, and the route loader supplies it to the page. The documented
`renderServerComponent` helper is suitable; slots and composite components are
not required.

Include a short explanation on the page that this is an experimental React
Server Component rendered by TanStack Start. Add a **Server component sample**
link from `/` to `/sample`, and a **Home** link back to `/`, using router links.

### Acceptance criteria

- Opening `/sample` directly or refreshing it renders the sample successfully.
- Navigation from `/` to `/sample` and back works without route or hydration
  errors.
- The sample has the specified browser title, heading, greeting and timestamp
  label, and displays a valid UTC timestamp.
- The greeting and timestamp are produced by an actual RSC render through
  TanStack Start's supported RSC transport, not a client-side imitation.
- Server-only timestamp generation remains outside the browser bundle. Do not
  use secrets or machine-identifying information to demonstrate this boundary.
- A server-rendering failure is surfaced as an error, not replaced with a
  fabricated successful sample.
- The README identifies the relevant route, server-component code, integration
  configuration and experimental API used.
- All earlier acceptance criteria remain satisfied.

### Verification

Add focused automated checks for direct access, home/sample navigation and
sample content, including timestamp validity without asserting an exact time.
Verify those behaviors against the production build as well as development.

In the pull request, identify the RSC configuration and server-rendering code,
and provide build or bundle evidence for the server-only boundary. A screenshot
of the greeting alone is not evidence that RSC is being used.

## Production-line prerequisites

This specification describes the desired product, not capabilities already
available in Factory's issue intake.

Before queuing Part 1, replace the package-only calibration check with appropriate
application verification. The current
`productionLine/Verify-Package.sh` explicitly rejects dependencies,
devDependencies and scripts, all of which this application needs. Do not simply
disable verification, and do not bundle Factory verification redesign into these
application parts.

The current issue intake requires inline acceptance bullets and does not resolve
a referenced specification or select a part automatically. Until that capability
exists, each work item must carry the required inline acceptance criteria and
identify this file, its applicable part and the exact repository revision being
used. Keep those criteria consistent with the referenced specification.

Create and queue implementation work items only after those prerequisites are
resolved and the user approves execution. Writing this document alone does not
change the workload, create issues or launch missions.
