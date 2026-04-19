# URL Shortener — Design Trade-offs

## Decision 1: Short code generation strategy

**Picked:** Random base62, length 7

**Why:** The whole thing works like this — we take the URL, we generate and generate doesn't look at the url its purely random , we geenearte 7 random base62 chars it might be possible that even after this randomness we can get two same records. But for that handling we put the unique constraint on the DB, so if we get a duplicate error we try to insert the entry with a new fresh code.

**Why not (a) auto-increment:** It's very guessable, not very safe — because what you're doing is just increasing the last digit and the hacker gets all the info of the records. Whole security is compromised. From the hacker's POV he will put GET requests to `aaaa1`, then `aaaa2`, and by this he gets which short URL is pointing to which. Then he can just see the password reset links, magic links containing tokens, Google Docs links, etc. — targeted attacks.

**Why not (c) UUID truncated:** UUIDs introduce the real uniqueness constraint which actually prevents broken insertion, but as we have to shorten the URL we truncate to 7 chars of the UUID — and right there the whole purpose of UUID uniqueness gets lost. We now don't have either uniqueness, and got even uglier code. I collapsed the difference down to just the encoding choice, and the encoding choice favors base62 on both density and aesthetics. (base62 has 62 possible chars per slot, hex has only 16.)

**Why not (d) hash of URL:** Three reasons. First, anyone can generate the hash (except when using a private key like HMAC). Second, it introduces broken data if we get the same hash for two URLs → trapped collisions. Third: predictable codes and forced dedup.

---

## Decision 2: Database choice (PostgreSQL)

**Picked:** PostgreSQL

**Why:** For the URL shortener we need durability, isolation, unique constraints, and fast lookups — Postgres provides every one of these.

**Why not Redis-only:** It's just the cache, not the DB. It stores data in RAM, and RAM costs more — roughly 10× more expensive per GB than disk, so storing 100M URLs in RAM forever isn't economical.

**Why not MongoDB:** MongoDB is more applicable when the schema changes often, or for nested data — say a blog → post → comment → comment reply. Our schema is fixed and flat.

**Why not MySQL:** Same as Postgres — but Postgres wins on better concurrent handling (MVCC — Multi-Version Concurrency Control).

---

## Decision 3: Redis cache strategy (cache-aside, 1-hour TTL)

**Picked:** Cache-aside with 1-hour TTL

**Why cache-aside:** The read request goes to Redis first. But the write goes to the DB directly — the write request doesn't touch Redis at all.

**Why not write-through:** In one operation, data is written to both Redis and DB. If either one fails, it doesn't return success — so if any one of them is down, damn, we failed the operation.

**Why not write-behind:** All operations are on Redis, and after a certain time interval the data is flushed (background process) to the DB. The main con: if Redis crashes / fails before the flush, all that data is gone.

**Why 1-hour TTL:** TTL = time to live. When the TTL is over for a Redis entry, the cache fails (entry expires) and the request falls back to the DB. 1 hour is the sweet spot — we don't want Redis to reset too early (we'd lose the whole advantage of Redis), and not too long either (the data would get stale in the cache). And if the TTL is very long, memory is bloated as well (more expensive).

---

## Decision 4: Rate limiting (Token bucket in Redis)

**Picked:** Token bucket per-IP, capacity 100, refill 10 req/sec, stored in Redis. Applied only to POST /shorten.

**Why token bucket:** It gives 100 requests of capacity with refill speed 10 req/sec, so it's great for rate limiting — it survives bursts as well.

**Why not leaky bucket:** Funnel that consumes requests at a fixed rate. Say the funnel limit is 10 req/sec — if 11 requests come in a second, it takes the first 10 and rejects the rest. Too strict for human bursts.

**Why not fixed window counter:** Counts requests per IP per minute. Downside: say one user hits 100 req in one minute and 100 more in the next minute → that's 200 requests across ~1 minute around the boundary → whole purpose defeated.

**Why not sliding window:** Keep the record for the last 60 seconds — at every moment, count the requests in the last 60 seconds. Old requests fall off the back of the window as time moves forward — no fixed reset boundaries to game. Overkill for our needs; token bucket already handles bursts + abuse more simply.

**Why Redis (not app RAM):** Say we have 3 app servers behind a load balancer and we put rate limiting at the server (RAM) level → the user effectively gets `(no. of servers) × rate_limit`. App servers must be stateless, so we keep the rate-limit state in shared memory (Redis).

---

## Decision 5: Async analytics writes (in-memory Channel + BackgroundService, batched 5s)

**Picked:** Async — enqueue click events to an in-memory C# Channel, BackgroundService bulk-flushes to Postgres every 5 seconds.

**Why async over sync:** Sync stops the process — the user won't redirect to the URL until it gets some response. Async flow is non-blocking, so the latency is reduced — we want this as minimum as possible (ideally). Latency = how fast we get output for any input.

**Why this risk is acceptable for clicks but not for POST /shorten:** When executing the POST /shorten request, it's necessary the record stays durable — we're ensuring the user that yes, his record will stay in the DB. For clicks tracking, it's not that important — latency is more important, and from the business use case it's also fine: no major diff between 1000 & 1007 clicks.

**Why batch every 5s:** Say we got 10 click requests — the background service has the option to either go one-by-one or bulk insert. One-by-one doesn't make sense (analogy: buy one thing, bring it home, come again — overhead). It's better to pick items together. But we can't take too many items at once either, as the DB can overwhelm if we insert too much at once.

**Why C# Channel + BackgroundService (not Kafka):** We are doing this in a single repo/service, so we don't need any third-party queue like Kafka or RabbitMQ.

**Why graceful shutdown matters:** Generally all the requests in the queue would get lost — but in .NET it's automatic via `StopAsync()`, which handles the graceful shutdown.

---

## Concept Reference: ACID Properties

- **Atomicity** — either an operation happens in the DB or not (no mid-state)
- **Consistency** — DB obeys all the constraints like UNIQUE and all
- **Isolation** — two requests at the same time don't affect each other
- **Durability** — if a request has reached the DB but server crashed, the operation must still be done in the DB
