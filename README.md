# DS — People & Organization

A portfolio project exploring organization management with .NET microservices and a Next.js frontend.

The backend includes organization directories, OpenIddict authentication, employee hiring and transfers, internal gRPC validation, a Kafka transactional outbox, an audit API, and an independent notification log consumer.

The frontend currently contains an early department browser. The complete English product experience is **planned**, not yet delivered.

- [Frontend experience, architecture and delivery proposal](docs/frontend/experience.md)
- [Ready-to-publish implementation issue drafts](docs/frontend/issue-drafts.md)
- [Existing frontend setup](frontend/README.md)
- [DirectoryService documentation](backend/DirectoryService/README.md)

Backend milestones: [authentication #7](https://github.com/1babii1/DS/issues/7), [employees and gRPC #9](https://github.com/1babii1/DS/issues/9), [Kafka and audit #11](https://github.com/1babii1/DS/issues/11).

Configuration follows the workspace secrets policy: `.env.example` is the only readable environment-variable contract; secret files must not be committed or copied into documentation.
