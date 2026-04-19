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

Response:
```json
{ "shortCode": "Iyldfko", "shortUrl": "https://localhost:7101/Iyldfko" }
```

| Status | Meaning |
|--------|---------|
| 201 | Created |
| 400 | Invalid URL (not http/https, too long, empty) |
| 429 | Rate limit exceeded (check `Retry-After` header) |
| 500 | Could not generate unique code after 5 retries |

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

### `GET /health`

Returns `OK` (200).

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
- ✅ GET /{code} with Redis cache-aside + 302
- ✅ Async click logging (Channel + BackgroundService, batched bulk INSERT)
- ✅ Graceful shutdown drains queue (no click loss on normal restarts)
- ✅ Token bucket rate limiter (Redis Lua, per-IP)
- ✅ GET /analytics/{code}
- ✅ URL validation (scheme allowlist)
- ✅ EF Core migrations
- ✅ Docker Compose for local infra

## What's NOT implemented (and why I'd add next)

- **User accounts / auth** — out of scope for portfolio. Would add JWT-based auth and per-user rate limit tier.
- **Custom short codes** — users picking their own slugs (e.g., "promo2026"). Easy add: validate length, check existing.
- **Malicious URL detection** — integrate Google Safe Browsing API before persisting.
- **Cache eviction on URL deletion** — currently TTL-bounded (1h). For instant invalidation, publish a Redis pub/sub event.
- **Production deployment** — Dockerfile + GitHub Actions CI not included. README focuses on local dev.
- **Tests** — limited test coverage; would add xUnit + Testcontainers for integration tests.
