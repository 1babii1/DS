# Frontend implementation issue drafts

Status: not published. GitHub issue creation through the connected integration returned HTTP 403 (`Resource not accessible by integration`). These are ready-to-publish bodies, not existing issue references.

See [the experience and architecture proposal](experience.md) for source evidence and decisions.

## 1. docs: define the international frontend portfolio experience

### Context
The backend now includes DirectoryService, AuthService, EmployeeService, AuditService and NotificationService. The existing Next.js frontend still has the starter page and department requests targeting pre-gateway URLs. We need an English portfolio experience grounded in real backend capabilities and the author's education-platform frontend conventions.

### Scope
- Record source-backed frontend/backend inventory and integration gaps.
- Define product journeys, visual direction, motion, accessibility and a proposed stack.
- Describe FSD boundaries, state ownership, OIDC considerations and API adaptation.
- Break implementation into independently verifiable issues; include architecture and hiring-flow diagrams.

### Acceptance criteria
- [ ] English product and engineering brief committed.
- [ ] Existing capabilities clearly distinguished from proposed additions.
- [ ] Implementation issues linked with dependencies and verification criteria.
- [ ] Documentation links and diff checked.

This issue covers discovery and planning; it does not represent delivery of the frontend.

## 2. feat(frontend): build the application shell and design system

### Outcome
Replace the starter page and current sidebar with an English People & Organization workspace. Follow education-platform conventions: Next.js App Router, strict TypeScript, FSD, Tailwind v4, shadcn/Radix and Lucide.

### Scope
- Semantic design tokens; light/dark themes; responsive navigation and page headers.
- Separate public presentation/auth routes from authenticated application routes.
- Overview, People, Organization, Positions, Locations and Activity navigation added with their actual feature delivery; avoid dead controls.
- QueryClient lifecycle in a client provider, route loading/error/not-found boundaries.
- Purposeful CSS/Motion transitions with reduced-motion support.
- Compatible supported Node runtime, committed lockfile, formatting/lint/typecheck/build commands and frontend CI.

### Acceptance criteria
- [ ] Desktop and 320px/mobile navigation verified in browser.
- [ ] Keyboard focus, dialog focus return and light/dark contrast checked.
- [ ] Reduced-motion behavior checked.
- [ ] Lint, typecheck and production build pass.
- [ ] PR includes desktop/mobile screenshots and exact verification commands.

Depends on the frontend discovery brief. Add dependencies only where a implemented feature needs them.

## 3. feat(frontend): integrate OpenIddict sessions and typed API errors

### Outcome
A user can register/sign in, reach protected data, and sign out without stale private cache. Viewer and editor experiences match backend authorization.

### Scope
- Verify configured OIDC client type, callback, issuer and scopes using the allowed configuration contract; document missing contract names in .env.example without secret values.
- Integrate authorization-code + PKCE using maintained OIDC/session tooling; evaluate Auth.js against the actual server instead of copying the reference project's beta configuration.
- Prefer server-held tokens through a narrowly scoped BFF; specify CSRF/origin protection, session expiry, refresh concurrency and logout.
- Use gateway-relative API URLs; adapt Directory EndpointResult, raw read DTOs, Employee error arrays and ProblemDetails into one frontend error model.
- Dates remain ISO strings at transport boundaries; nullable parent/children accurately typed; abort obsolete requests.
- English field and form errors, 401/403/503 states, session-scoped Query cache.

### Acceptance criteria
- [ ] Real login/callback/refresh/logout journey passes.
- [ ] Invalid state/PKCE and expired sessions fail safely.
- [ ] Viewer reads data but cannot execute editor actions; backend denial tested directly.
- [ ] Logout/account change clears private query cache.
- [ ] Network/server failures never become successful empty arrays.
- [ ] No application secrets inspected or exposed; use secrets-run only for the narrow application process.

Depends on application foundation; can precede visual feature screens.

## 4. feat(directory): provide position lookup and reliable location lists for the UI

### Problem
PositionController exposes POST only, so a hire form cannot select existing valid positions. Location queries join department_locations without DISTINCT and exclude unattached locations, which can duplicate rows and hide newly created entries in a management list.

### Scope
- Add authenticated position read/lookup endpoint filtered by department, using existing architecture and read DTO conventions.
- Define pagination/order/search behavior and active filtering for usable selectors.
- Make a location management read include unattached locations and avoid duplicate entities before pagination; preserve department filtering.
- Verify wire shapes for nested command requests/value objects and document concrete contracts consumed by the frontend.
- Keep gRPC internal; browser uses REST only.

### Acceptance criteria
- [ ] Position lookup returns only valid department assignments.
- [ ] Empty, invalid and unauthorized cases covered by integration tests.
- [ ] A location linked to two departments appears once in the management list.
- [ ] A newly created unattached location is discoverable.
- [ ] Stable pagination and existing reads verified.

Needed before complete hire/transfer and location management screens. Do not manufacture select options from mock data in live mode.

## 5. feat(frontend): deliver organization, people and audit workflows

### Outcome
A reviewer can create an organization, hire an employee, transfer them and find the resulting audit event in a coherent English UI.

### Scope
- Organization: paginated/lazy tree, department detail, create/reparent, location assignment and soft-delete confirmation.
- Positions: create with department assignments and list/search using the lookup contract.
- Locations: list/search/filter and create; timezone display.
- People: directory, detail panel, hire and transfer forms with RHF/Zod and inline backend errors.
- Activity: paginated audit feed and aggregate filter; details reveal source, event timestamps and payload on demand.
- Honest overview metrics computed only from complete data, with scope labels where necessary.
- URL filters, TanStack Query invalidation, no duplicate mutation submits, pending/error/empty states.

### Acceptance criteria
- [ ] Create location → department → position → hire → transfer → audit journey passes against real services.
- [ ] Viewer/editor controls and server enforcement agree.
- [ ] Invalid assignment and DirectoryService 503 preserve form input.
- [ ] Audit uses bounded polling and describes eventual consistency; no invented realtime delivery.
- [ ] Prefetched tree children, lazy children and pagination do not lose or duplicate nodes.
- [ ] Mobile/keyboard/reduced-motion behavior checked.
- [ ] Vitest/Testing Library behavior tests and focused Playwright journeys pass.

Depends on the foundation, auth/API integration and directory read contracts. NotificationService currently logs events; an inbox is outside this issue.

## 6. feat(portfolio): add a guided demo and engineering walkthrough

### Outcome
An international reviewer understands the product and its engineering decisions in a short, reproducible walkthrough.

### Scope
- Engineering view with service architecture, hiring sequence, outbox delivery/dedup explanation and links to source/PRs.
- Reproducible fictional seed scenario; isolated writable demo or explicitly labeled local simulation with reset. Never silently substitute mock data when live APIs fail.
- Show hire/transfer/audit and viewer/editor behavior; English copy and project README.
- Desktop/mobile screenshots and a short walkthrough recording after implementation.
- Proportional accessibility/performance checks and documented results.

### Acceptance criteria
- [ ] Demo mode and live mode are unmistakable; demo writes cannot affect real data.
- [ ] Reset scope is isolated and explicit.
- [ ] A reviewer can complete the documented walkthrough.
- [ ] Diagrams distinguish existing services from proposed features and logs from user notifications.
- [ ] CI/build/E2E and measured browser checks linked from PR.

Depends on working product workflows. Optional subsequent work: notification persistence/inbox, SSE delivery, history-backed analytics, or keyboard-accessible org-chart drag/drop; each requires its own justification and issue.

