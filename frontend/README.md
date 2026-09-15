# DS frontend

The People & Organization workspace is a Next.js application for the DS platform. It presents organization data in an accessible, responsive application shell and is designed to grow with the platform services.

## Stack

- Next.js App Router and React with TypeScript
- Tailwind CSS with semantic design tokens
- shadcn/ui primitives and Lucide icons
- TanStack Query for server state
- `next-themes` for persisted dark and light themes

## Structure

```text
app/             Route composition and page metadata
entities/        Business entities and their API contracts
features/        User-facing capabilities
widgets/         Composite page sections
shared/          Reusable UI, hooks, utilities, and infrastructure
```

`shared/ui` is the shadcn/ui destination. Its aliases are defined in `components.json`, so newly generated components follow the same structure.

## Run locally

Use Node.js 20.9 or newer.

```bash
npm install
npm run dev
```

Open `http://localhost:3000`.

## Quality checks

```bash
npm run lint
npm run build
```

The protected directory endpoints will use the server-side authentication boundary introduced in the authentication workstream. Until that connection is enabled, the Organization route reports an unavailable-data state rather than displaying placeholder records.
