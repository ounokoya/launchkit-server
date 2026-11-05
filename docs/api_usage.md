# Using the LaunchKit API

Base URL: `http://localhost:5080/api/v1` with the Compose stack. The OpenAPI document is served at
`/openapi/v1.json`.

## Conventions

- JSON in `camelCase`; dates are ISO 8601 UTC strings (`2026-11-04T09:30:00.000Z`).
- Authentication: `Authorization: Bearer <token>`. A token comes from e-mail verification or sign-in and is
  valid 14 days, until sign-out.
- Language of messages: the signed-in user's language, otherwise `Accept-Language` (`fr` or `en`), otherwise French.
- Errors are `application/problem+json`: test the `code`, show the `detail`.

```json
{ "status": 409, "code": "plan_limit_reached",
  "detail": "You’ve reached the Free plan limit: 3 of 3 projects. Upgrade to Pro to create more." }
```

| Code | HTTP | When |
|---|---|---|
| `invalid_request` | 400 | missing or wrong fields; `errors` lists translated messages per field |
| `unauthorized` | 401 | no token, unknown or expired token |
| `login_failed` | 401 | wrong e-mail or password (same answer for both) |
| `email_not_verified` | 403 | sign-in or organization routes before the verification link |
| `forbidden` | 403 | role too low (e.g. a member opening billing) |
| `owner_protected` | 403 | changing or removing the owner |
| `wrong_account` | 403 | accepting an invitation sent to another address |
| `not_found` | 404 | unknown resource, or an organization you do not belong to |
| `email_taken`, `already_member`, `already_invited`, `already_pro` | 409 | duplicates |
| `plan_limit_reached` | 409 | Free limit (3 projects, 3 members including pending invitations) |
| `invalid_link`, `invitation_unavailable` | 410 | used or expired link |

## Sign-up and sign-in

```bash
API=http://localhost:5080/api/v1
curl -X POST $API/auth/register -H 'Content-Type: application/json' \
  -d '{"name":"Marie Koné","email":"marie@demo.test","password":"motdepasse-1","locale":"fr"}'
# 201: the verification e-mail is in Mailpit (http://localhost:8025); its link carries ?token=…
curl -X POST $API/auth/verify-email -H 'Content-Type: application/json' -d '{"token":"<token from the e-mail>"}'
# 200: { "token": "…", "expiresAt": "…", "user": { … } }
curl -X POST $API/auth/login -H 'Content-Type: application/json' \
  -d '{"email":"marie@demo.test","password":"motdepasse-1"}'
curl -X POST $API/auth/logout -H "Authorization: Bearer $TOKEN"
```

`POST /auth/verify-email/resend {email}` always answers 202, whether or not the account exists.

## Organizations, members, invitations

```bash
curl -X POST $API/orgs -H "Authorization: Bearer $TOKEN" -H 'Content-Type: application/json' -d '{"name":"Acme"}'
curl $API/orgs/$ORG/dashboard -H "Authorization: Bearer $TOKEN"
curl -X POST $API/orgs/$ORG/invitations -H "Authorization: Bearer $TOKEN" -H 'Content-Type: application/json' \
  -d '{"email":"jean@demo.test","role":"member"}'
# Jean signs in, opens the link of the e-mail (/invitations/<token>):
curl $API/invitations/$INVITATION_TOKEN -H "Authorization: Bearer $JEAN"
curl -X POST $API/invitations/$INVITATION_TOKEN/accept -H "Authorization: Bearer $JEAN"
```

Roles: `owner` (one per organization, created with it), `admin`, `member`. Admins invite, change roles
(`PATCH /orgs/{org}/members/{user}`), remove members and manage billing; a member can leave on their own
(`DELETE /orgs/{org}/members/{own id}`).

## Billing (simulated payment)

```bash
curl $API/orgs/$ORG/billing -H "Authorization: Bearer $TOKEN"
curl -X POST $API/orgs/$ORG/billing/checkout -H "Authorization: Bearer $TOKEN" -H 'Content-Type: application/json' \
  -d '{"plan":"pro"}'
# 202 { "paymentId": "…" } — poll GET …/billing every 2 s until "plan": "pro" (the Rust worker does the switch)
```

## Projects

`GET|POST /orgs/{org}/projects`, `GET|PATCH|DELETE /orgs/{org}/projects/{project}`. Fields: `name` (required, ≤ 80),
`description` (≤ 2000), `status` (`active`, `in_progress`, `draft`), `leadId` (a member), `dueDate` (`YYYY-MM-DD`).
The project detail includes the organization's members and the project history.

## Back office

`GET /admin/stats`, `/admin/organizations`, `/admin/users`, for the super admin only (`admin@demo.test`).

## Configuration

Environment variables (ASP.NET Core syntax, `__` separates sections):

| Variable | Default | Role |
|---|---|---|
| `Arango__Url`, `Arango__Database`, `Arango__User`, `Arango__Password` | `http://localhost:8529`, `launchkit`, `root`, empty | database |
| `Arango__InitializeSchema` | `true` | create collections and indexes at startup |
| `Plans__Free__MaxProjects`, `Plans__Free__MaxMembers` | 3, 3 | Free limits (Pro: unlimited) |
| `Plans__ProPriceCents`, `Plans__Currency` | 2900, `EUR` | displayed price (simulated) |
| `App__WebBaseUrl` | `http://localhost:5173` | web app, used in e-mail links |
| `App__SessionLifetimeDays`, `App__VerifyEmailHours`, `App__InvitationDays` | 14, 24, 7 | lifetimes |
| `Seed__AdminEmail`, `Seed__AdminPassword` | `admin@demo.test`, empty | demo super admin (not created when empty) |
