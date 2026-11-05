# launchkit-worker

Background worker of the LaunchKit demo, in Rust. It runs the jobs the .NET API queues in the
ArangoDB `jobs` collection, so HTTP requests never wait for them:

- `send_email`: renders `verify_email`, `invitation` or `pro_welcome` (HTML + plain text,
  inline logo and illustration) from the shared i18n texts, sends it over SMTP (Mailpit), then
  replaces `payload.params.link` with `"[envoyé]"` so the clear-text token does not stay in the
  database.
- `payment_webhook`: plays the payment provider (simulated payment). In one idempotent AQL
  statement: payment `succeeded`, organization `pro`, `plan_upgraded` activity, `pro_welcome`
  e-mail job for the owner. A replay changes nothing.
- Every `EXPIRE_INVITATIONS_EVERY_SECS`: pending invitations past `expiresAt` become `expired`
  and `usage.pendingInvitations` of their organization goes down in the same statement. The same
  tick releases jobs left `running` by a stopped worker.

Contract: the specifications `server-taches.md` and `server-donnees.md` (private design repository); decisions ADR-0005,
ADR-0006, ADR-0008, ADR-0009.

## Job lifecycle

- Claim: one AQL `UPDATE` takes the oldest `pending` job with `runAt <= now`, sets `running`,
  `attempts + 1`, `lockedAt`. Two workers racing for the same job get a write-write conflict
  (ArangoDB error 1200); the loser claims again and gets the next job.
- Success: `done`, `finishedAt`, `lastError: null`.
- Failure: back to `pending` with `runAt = now + 2^attempts s` (2, 4, 8, 16 s) and `lastError`,
  or `failed` once `attempts >= maxAttempts` (5).
- A job `running` for more than `STALE_JOB_TIMEOUT_SECS` goes back to `pending` (or `failed` if it
  has no attempt left). A worker that finishes after that is ignored: results are only saved if
  `lockedAt` still matches.
- SIGTERM or Ctrl-C: the current job is finished and recorded, then the process exits.

## Configuration

Environment variables only. Empty values count as unset (except `ARANGO_PASSWORD`).

| Variable | Default | Role |
|---|---|---|
| `ARANGO_URL` | `http://localhost:8529` | ArangoDB server |
| `ARANGO_DATABASE` | `launchkit` | database |
| `ARANGO_USER` | `root` | user |
| `ARANGO_PASSWORD` | *(empty)* | password |
| `SMTP_HOST` | `localhost` | SMTP server (plain SMTP, no TLS nor auth: Mailpit) |
| `SMTP_PORT` | `1025` | SMTP port |
| `MAIL_FROM` | `LaunchKit <noreply@demo.test>` | sender |
| `APP_BASE_URL` | `http://localhost:5173` | web app, used in e-mail links (`/app/<orgKey>`) |
| `I18N_DIR` | `/app/i18n` | folder with `fr.json`, `en.json` (`server/shared/i18n`, copied into the image) |
| `POLL_INTERVAL_MS` | `1000` | wait when the queue is empty |
| `EXPIRE_INVITATIONS_EVERY_SECS` | `60` | invitation expiry and stale-job check period |
| `STALE_JOB_TIMEOUT_SECS` | `300` | a `running` job older than this is released |
| `RUST_LOG` | `info` | log filter (`tracing-subscriber` syntax) |

## Run

```sh
# Local, against a running ArangoDB and Mailpit
ARANGO_PASSWORD=… I18N_DIR=../shared/i18n cargo run --release

# Docker: build context is server/ (same as compose.yaml and CI)
cd .. && docker build -f worker/Dockerfile -t launchkit-worker .
docker run --rm \
  -e ARANGO_URL=http://arangodb:8529 -e ARANGO_PASSWORD=… \
  -e SMTP_HOST=mailpit -e APP_BASE_URL=http://localhost:5173 \
  launchkit-worker
```

The worker does not create the database, collections or indexes: the API owns the schema.

## Test

Needs Docker for the integration tests (ArangoDB `arangodb:3.12` and `axllent/mailpit`, started
by Testcontainers on random ports and removed at the end).

```sh
cargo test                                  # unit + integration
cargo test --lib                            # unit tests only (no Docker)
cargo test --test integration -- payment    # integration scenarios matching "payment"
cargo fmt --check
cargo clippy --all-targets -- -D warnings
cargo llvm-cov --all-targets --fail-under-lines 80
cargo deny check
cargo audit
```

The integration tests use a custom harness (`harness = false`): the two containers start once,
each scenario creates its own database with the collections and indexes of `server-donnees.md`.

## Layout

| File | Content |
|---|---|
| `src/arango.rs` | HTTP client: AQL cursors with bind variables, error 1200 → retryable conflict |
| `src/jobs.rs` | claim / finish / retry / stale release queries, backoff |
| `src/email.rs` | payload, rendering, MIME message, SMTP, link redaction |
| `src/i18n.rs` | flat JSON texts and `{placeholder}` replacement |
| `src/payment.rs` | `payment_webhook` upgrade query |
| `src/invitations.rs` | invitation expiry query |
| `src/worker.rs` | main loop, dispatch, maintenance, graceful shutdown |
| `src/config.rs` | environment variables and defaults |
| `assets/` | PNGs embedded in the binary and attached inline (`cid:`) |
