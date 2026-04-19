# URL Shortener

A high-throughput URL shortening service in .NET 9 with Postgres + Redis. Sub-50ms cache-hit redirects, async analytics, distributed token-bucket rate limiting.

> Built April 2026 as a portfolio project. See [tradeoffs.md](tradeoffs.md) for design decisions and [concepts.md](concepts.md) for technical notes.

---

## Architecture

```
                ┌──────────┐
                │  Client  │
                └─────┬────┘
                      │ HTTP
                      ▼
        ┌─────────────────────────────┐
        │   ASP.NET Core Web API      │
        │   (Minimal APIs, .NET 9)    │
        │                             │
        │  POST /shorten              │──── rate-limited (token bucket)
        │  GET  /{code}    (302)      │──── cache-aside reads
        │  GET  /analytics/{code}     │
        └──────┬──────────────┬───────┘
               │              │
               │              │
   ┌───────────▼─────┐   ┌───▼──────────────────┐
   │     Redis       │   │     PostgreSQL       │
   │                 │   │                      │
   │  url:{code}     │   │  short_codes table   │
   │  ratelimit:{ip} │   │  clicks table        │
   │  (TTL 1h)       │   │  UNIQUE(code)        │
   └─────────────────┘   └──────────────────────┘
                                    ▲
                                    │ bulk INSERT every 5s
                                    │
                   ┌────────────────┴────────────────┐
                   │  Channel<ClickEvent>            │
                   │  + ClickFlushService            │
                   │  (BackgroundService)            │
                   └─────────────────────────────────┘
```

---

## Tech stack

- **.NET 9** (ASP.NET Core, Minimal APIs)
- **PostgreSQL 16** (source of truth)
- **Redis 7** (cache + rate-limit state)
- **Entity Framework Core 9** (ORM, migrations)
- **StackExchange.Redis** (Redis client)
- **Docker Compose** (local infra)

---

## Run locally

Prerequisites: .NET 9 SDK, Docker Desktop.

```bash
# 1. Clone + cd
git clone <repo-url>
cd url-shortener

# 2. Start Postgres + Redis
docker compose up -d

# 3. Apply DB schema
cd src/UrlShortener.Api
dotnet ef database update

# 4. Run the API
dotnet run --launch-profile https
```

App listens on `https://localhost:7101` and `http://localhost:5292`.

Open `https://localhost:7101/` in a browser for the **minimal UI** (single-page form to shorten URLs). API endpoints below also work directly via curl/Postman.

> **Windows note:** if you have native Postgres installed, port 5432 will conflict. We mapped Docker Postgres to host port **5433** in `docker-compose.yml`.

---

## API

### `POST /shorten`

Shorten a long URL. Rate-limited per IP (100 capacity, 10/sec refill).

```bash
curl -k -X POST https://localhost:7101/shorten \
  -H "Content-Type: application/json" \
  -d '{"longUrl": "https://example.com/some/path"}'
```

Optional: provide your own slug via `customCode` (3–10 base62 chars, not a reserved word):

```bash
curl -k -X POST https://localhost:7101/shorten \
  -H "Content-Type: application/json" \
  -d '{"longUrl": "https://example.com/promo", "customCode": "promo2026"}'
```

Response:
```json
{ "shortCode": "Iyldfko", "shortUrl": "https://localhost:7101/Iyldfko" }
```

| Status | Meaning |
|--------|---------|
| 201 | Created |
| 400 | Invalid URL (not http/https, too long, empty) OR invalid customCode (length / chars / reserved word) |
| 409 | `customCode` already taken |
| 429 | Rate limit exceeded (check `Retry-After` header) |
| 500 | Could not generate unique random code after 5 retries |

### `GET /{code}`

Resolve a short code → 302 redirect to the long URL. Reads cache-aside from Redis (1h TTL); falls back to Postgres on miss. Logs the click asynchronously.

```bash
curl -k -i https://localhost:7101/Iyldfko
# HTTP/1.1 302 Found
# Location: https://example.com/some/path
```

### `GET /analytics/{code}`

```bash
curl -k https://localhost:7101/analytics/Iyldfko
# {"code":"Iyldfko","totalClicks":7,"lastClickedAt":"2026-04-19T16:12:26Z"}
```

### `DELETE /{code}`

Removes the mapping AND invalidates the Redis cache entry immediately (so a 404 is served right away, not after the 1-hour TTL).

**Requires JWT auth and the caller must be the owner** (the user who created the code).

```bash
curl -k -X DELETE https://localhost:7101/Iyldfko \
  -H "Authorization: Bearer <jwt>"
```

| Status | Meaning |
|--------|---------|
| 204 | Deleted (cache invalidated) |
| 401 | No JWT / invalid JWT |
| 403 | Authenticated but not the code's owner |
| 404 | Code doesn't exist |

### `POST /auth/register`

Create a new user account. Returns a JWT.

```bash
curl -k -X POST https://localhost:7101/auth/register \
  -H "Content-Type: application/json" \
  -d '{"username":"deepak","email":"deepak@x.com","password":"password123"}'
# {"token":"eyJ...","username":"deepak","email":"deepak@x.com"}
```

Validation: username 3–50 chars, valid email, password ≥ 8 chars.

| Status | Meaning |
|--------|---------|
| 201 | Created, JWT returned |
| 400 | Invalid input |
| 409 | Username or email already taken |

### `POST /auth/login`

```bash
curl -k -X POST https://localhost:7101/auth/login \
  -H "Content-Type: application/json" \
  -d '{"username":"deepak","password":"password123"}'
# {"token":"eyJ...","username":"deepak","email":"deepak@x.com"}
```

| Status | Meaning |
|--------|---------|
| 200 | Token returned |
| 401 | Bad credentials |

### `GET /health`

Returns `OK` (200).

---

## Authentication

The API supports optional JWT auth. Endpoints work in two modes:

| Endpoint | Anonymous | Authenticated |
|----------|-----------|---------------|
| `POST /shorten` | ✅ works (`UserId = null` in DB) | ✅ ties code to user (for ownership) |
| `GET /{code}` | ✅ public | ✅ public |
| `GET /analytics/{code}` | ✅ public | ✅ public |
| `DELETE /{code}` | ❌ 401 | ✅ if owner; 403 if not |

JWT secret is configured in `appsettings.json` under `Jwt:Secret`. **For production, override with the `Jwt__Secret` environment variable** — never commit a real secret.

---

## Key design decisions

Full reasoning in [tradeoffs.md](tradeoffs.md). Summary:

| # | Decision | Why |
|---|----------|-----|
| 1 | **Random base62** codes (length 7) | Not enumerable; unique constraint catches rare collisions, retry on duplicate |
| 2 | **PostgreSQL** as source of truth | Durability + native UNIQUE constraint + indexed lookups |
| 3 | **Redis cache-aside** with 1h TTL | Reads >> writes; cache only for reads, writes go straight to DB |
| 4 | **Token bucket** rate limiter, in **Redis** | Allows bursts; Redis-backed = stateless app, distributed across servers |
| 5 | **Async click logging** via `Channel<T>` + `BackgroundService` | Redirect stays sub-50ms; bulk INSERT every 5s amortizes DB cost |

---

## What's implemented

- ✅ POST /shorten with random base62 + retry on UNIQUE
- ✅ POST /shorten with optional `customCode` (3–10 chars, base62, non-reserved)
- ✅ POST /shorten checks URLhaus for known malicious URLs before persisting (fail-open if URLhaus is down)
- ✅ GET /{code} with Redis cache-aside + 302
- ✅ DELETE /{code} with JWT auth + ownership check + instant cache invalidation + Redis pub/sub broadcast
- ✅ Async click logging (Channel + BackgroundService, batched bulk INSERT)
- ✅ Graceful shutdown drains queue (no click loss on normal restarts)
- ✅ Token bucket rate limiter (Redis Lua) — **tiered**: per-IP (100/10) for anonymous, per-user (500/50) for authenticated
- ✅ GET /analytics/{code}
- ✅ URL validation (scheme allowlist)
- ✅ EF Core migrations
- ✅ Docker Compose for local infra
- ✅ Multi-stage Dockerfile for production deploys
- ✅ GitHub Actions CI (build + test + Docker image)
- ✅ JWT-based user accounts (register / login / ownership)
- ✅ Distributed cache invalidation pattern (Redis pub/sub publisher + subscriber)
- ✅ Testcontainers integration tests — real Postgres + Redis containers spun up per test run
- ✅ xUnit test suite: **42 tests** (32 unit + 10 integration) — all passing

## What's NOT implemented (and why I'd add next)

- **Self-service deletion of anonymous codes** — codes created without auth (UserId = null) currently can't be deleted by anyone. Would add an admin role / API key for moderation cleanup
- **Refresh tokens** — JWT TTL is 24h; would add refresh-token rotation for better security
- **List-my-codes endpoint** — `GET /my/codes` for authenticated users to see what they've shortened

## Configuration

### URLhaus Auth-Key

URLhaus (the malicious URL check) requires a free Auth-Key. Without it, all checks fail-open (URL is allowed through with a warning logged).

1. Register a free key at **https://auth.abuse.ch/**
2. Set the key:
   - **Local dev:** `appsettings.Development.json` → `"UrlHaus": { "AuthKey": "..." }`
   - **Production:** `UrlHaus__AuthKey` env var (the `__` maps to nested config)

### JWT secret

For production, override `Jwt:Secret` with the `Jwt__Secret` env var. Generate a 32+ char random string:

```bash
openssl rand -base64 48
```
