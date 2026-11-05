# LaunchKit · server

Back end of **LaunchKit** (live demo: **https://saas.koyad.com**), a SaaS starter kit used as a portfolio demo: accounts, multi-tenant organizations, roles
and invitations, Free / Pro plans with a simulated payment, a sample "Projects" module and a super-admin back office.

| Part | Stack | Folder |
|---|---|---|
| REST API `/api/v1` | ASP.NET Core (.NET 10, minimal APIs) | `api/` |
| Background worker (e-mails, simulated payment webhook, invitation expiry) | Rust (tokio) | `worker/` |
| Database | ArangoDB 3.12 (documents + a `memberships` graph edge) | — |
| Demo inbox | Mailpit: every e-mail of the demo lands there | — |
| Texts (French / English) | exported from the design source, shared by the API and the worker | `shared/i18n/` |

## Run it locally

Requirements: Docker with Compose.

```bash
docker compose up --build
```

| Service | URL |
|---|---|
| API | http://localhost:5080 (OpenAPI: `/openapi/v1.json`, health: `/health`) |
| Mailpit (demo inbox) | http://localhost:8025 |
| ArangoDB | http://localhost:8539 |

The back office account is `admin@demo.test`, with the password set in `ADMIN_PASSWORD`
(`admin-demo-local` by default, for local use only). Use made-up `@demo.test` addresses: nothing leaves Mailpit.

## How it works

- **Multi-tenant by construction**: a user belongs to an organization through an edge that carries the role
  (owner, admin, member). Every organization route checks that edge; someone outside gets a 404.
- **Plan limits under concurrency**: Free allows 3 projects and 3 members (configuration, not code). The limit is
  checked and the counter incremented in the same atomic query, so 12 simultaneous requests still create exactly 3
  projects.
- **Revocable sessions**: opaque bearer tokens, stored as SHA-256 hashes, removed on sign-out. Passwords use
  ASP.NET Core Identity's PBKDF2 hasher. Verification and invitation links follow the same rule.
- **Jobs queue in ArangoDB**: the API never sends e-mails itself. It queues jobs that the Rust worker claims
  atomically, runs, retries with back-off (2, 4, 8, 16 s) and marks as failed after 5 attempts.
- **Simulated payment, real flow**: "Upgrade to Pro" creates a payment and a webhook job; the worker plays the
  provider, switches the organization to Pro and sends the welcome e-mail. Stripe test mode can replace it behind
  the same interface.
- **Errors**: `application/problem+json` with a stable `code` and a translated `detail` (French or English, from the
  user's language or `Accept-Language`).

## Tests

Every part has unit, end-to-end, coverage and security checks, run by GitHub Actions on every push
(`.github/workflows/ci.yml`).

| Check | API | Worker |
|---|---|---|
| Unit | `dotnet test api/tests/LaunchKit.Api.Tests` | `cargo test --lib` |
| Integration (real ArangoDB / Mailpit in containers) | `dotnet test api/tests/LaunchKit.Api.IntegrationTests` | `cargo test` |
| Coverage ≥ 80 % of lines | `./scripts/api-coverage.sh` | `cargo llvm-cov --fail-under-lines 80` |
| Security | CodeQL, gitleaks, vulnerable NuGet packages, Trivy | `cargo audit`, `cargo deny`, `clippy -D warnings` |

The full **scenario** (A signs up, hits the Free limit, invites B, B is refused billing, A upgrades through the
worker, the 4th project is accepted, the back office shows Pro) runs against the Compose stack:

```bash
docker compose up --build --detach --wait
SCENARIO_API_URL=http://localhost:5080 SCENARIO_MAILPIT_URL=http://localhost:8025 dotnet test tests/LaunchKit.Scenario.Tests
```

## Layout

```
api/src/LaunchKit.Api        API: Features/ (one file per module), Auth/, Infrastructure/ (ArangoDB client, schema)
api/tests/                   unit tests and integration tests (Testcontainers)
worker/                      Rust worker, its tests and Dockerfile
shared/i18n/                 fr.json, en.json
tests/LaunchKit.Scenario.Tests  end-to-end scenario against the running stack
docs/                        roadmap and API usage guide
```

## Method

Spec first, then recorded decisions, then tests derived from the spec, then a changelog. The specifications and
decision records live in a private design repository; comments cite them by name (`server-api.md`, ADR-0004…). See
[`docs/api_usage.md`](docs/api_usage.md) for the API itself and [`docs/roadmap.md`](docs/roadmap.md) for what comes next.

## License

MIT, see [LICENSE](LICENSE).
