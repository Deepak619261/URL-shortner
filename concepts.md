# Concepts Notes — URL Shortener Build

> ⚠️ This is a starting draft. Read through and rewrite anything that doesn't sound like you. Interviewers can smell AI-flavored writing from a mile away.

---

## 1. system.random vs RandomNumberGenerator (CSPRNG)

system.random -> uses system time as seed, the algo is deterministic so if attacker observes a few outputs they can predict the next ones (security hole for codes, tokens etc)

CSPRNG -> cryptographically secure random, uses the OS-level entropy (cpu noise, timing jitter, hardware entropy etc), unpredictable

so for our shortener we went with CSPRNG -> if we used random the attacker can guess upcoming short codes (same enumeration attack as in option a, auto-increment)

modern .net 8+ one-liner -> `RandomNumberGenerator.GetString(alphabet, length)`, that's it

---

## 2. static methods vs instance + interface + DI

static method -> belongs to the class itself, no instance needed. call as `ClassName.MethodName()`, no `new ClassName()`

use static when -> the function is pure (no internal state to remember), no dependencies to inject, no reason to swap the implementation (say for tests)

switch to instance + interface + DI when ->
- you need to inject dependencies (logger, config, other services)
- you need to mock for tests (say a FakeCodeGenerator that returns predictable codes like "AAAA111" so tests don't depend on randomness)
- you need lifecycle management (singleton, scoped, transient via DI)

pattern looks like ->
```csharp
public interface ICodeGenerator { string Generate(int length); }
public class RandomCodeGenerator : ICodeGenerator { ... }
public class FakeCodeGenerator : ICodeGenerator { ... }   // for tests
```

---

## 3. code space math — why 7 chars

`alphabet_size ^ length` = number of possible codes

62^7 = ~3.5 trillion possible codes

| length | possible codes | when to use |
|---|---|---|
| 5 | 916M | too small, fast collisions at scale |
| 6 | 56.8B | bit.ly's scale would push it |
| **7** | **3.5T** | industry sweet spot |
| 10 | 839 quadrillion | overkill |
| 11 | 52 quintillion | youtube uses this (legacy) |

per-insert collision probability = `existing_codes / total_codes`

| existing rows | collision chance per insert |
|---|---|
| 1M | 1 in 3.5M |
| 100M | 1 in 35,000 |
| 1B | 1 in 3,500 |

even at billions of rows collisions are rare, retry handles the few that happen -> zero broken inserts

---

## 4. changing code length later — what's a "migration"

the db column has a fixed max width set in `AppDbContext`:
```csharp
entity.Property(s => s.Code).HasMaxLength(10);   // column = 10-char wide box
```

| switching from 7 to | what's needed |
|---|---|
| 10 | just call `Generate(10)`, column already fits, no db change |
| 15 | app code change + schema change -> migration needed |

### how a schema change works (EF core)

1. edit DbContext -> `HasMaxLength(10)` becomes `HasMaxLength(15)`
2. generate the migration file ->
   ```bash
   dotnet ef migrations add IncreaseCodeLength
   ```
   EF writes SQL like `ALTER TABLE short_codes ALTER COLUMN "Code" TYPE varchar(15);`
3. apply migration ->
   ```bash
   dotnet ef database update
   ```
   runs the SQL against postgres

EF tracks applied migrations in `__EFMigrationsHistory` table

---

## 5. port conflicts on windows (postgres native vs docker)

if you have native postgres installed on your machine AND run docker postgres, both compete for port 5432, and the native one usually wins on `localhost`

symptom -> `password authentication failed for user "X"` (postgres returns this even when the user doesn't exist, for security so attackers can't enumerate users)

fix -> map docker to a different host port:
```yaml
ports:
  - "5433:5432"   # host_port:container_port
```
update connection string to `Port=5433`, done

verify with `netstat -ano | grep ":5432"` -> if 2 PIDs are listening you have a conflict

---

## 6. docker compose lifecycle

| command | what it does | destructive? |
|---|---|---|
| `docker compose up -d` | start (or recreate) containers, preserve volumes | no |
| `docker compose down` | stop + remove containers, KEEP volumes | no, data safe |
| `docker compose down -v` | stop + remove containers, DELETE volumes | YES, wipes data |
| `docker compose ps` | list containers + health | no |
| `docker compose logs <service>` | print container logs | no |

postgres env vars (`POSTGRES_USER`, `POSTGRES_PASSWORD`, `POSTGRES_DB`) only apply on FIRST init of a fresh volume. to change them later -> either nuke the volume (`down -v`) or use `ALTER USER` against the running container

---

## 7. modern .net — minimal APIs vs controllers

minimal APIs (default since .net 6) -> endpoints are defined inline, no controller classes
```csharp
app.MapPost("/shorten", async (ShortenRequest req, AppDbContext db) => { ... });
```

controllers (older style) -> separate `XxxController.cs` classes with `[HttpGet]` attrs, still works fine

for a 3-endpoint service like ours, minimal APIs are cleaner. most new .net projects use them today

---

## 8. EF core "code-first" workflow

1. define POCO entity classes (ShortCode, Click)
2. define `DbContext` with `DbSet<T>` properties + `OnModelCreating` for constraints/indexes
3. register in DI -> `services.AddDbContext<AppDbContext>(opts => opts.UseNpgsql(...))`
4. generate migration -> `dotnet ef migrations add InitialCreate`
5. apply -> `dotnet ef database update`

key EF mappings (from our project) ->
- `entity.HasIndex(s => s.Code).IsUnique()` -> `CREATE UNIQUE INDEX`
- `entity.Property(s => s.Code).HasMaxLength(10)` -> `varchar(10)`
- `entity.ToTable("short_codes")` -> snake_case postgres table name

---

## 9. ConnectionMultiplexer (StackExchange.Redis) — singleton pattern

```csharp
builder.Services.AddSingleton<IConnectionMultiplexer>(sp =>
    ConnectionMultiplexer.Connect(connectionString));
```

why singleton ->
- `ConnectionMultiplexer` is thread-safe (designed for sharing across threads)
- it's expensive to create (TCP connect + handshake)
- reuse one for the entire app's lifetime
- anti-pattern -> creating one per request, wastes connections, kills perf

interview line -> *"the connection multiplexer is thread-safe and expensive to create, so we register it as a singleton and let DI hand it out, not create per request"*

---

## 10. DTOs (Data Transfer Objects) — why not return the DB entity directly

DTO = a separate small class that defines the shape of the JSON, NOT the DB entity itself

```csharp
record ShortenRequest(string LongUrl);
record ShortenResponse(string ShortCode, string ShortUrl);
```

why use DTOs ->

1. **security** -> say later you add an `InternalAdminNotes` field to ShortCode for moderation. if your endpoint returns the entity directly, that field NOW LEAKS in every API response. DTOs prevent this -> only fields you explicitly add show up

2. **API stability** -> say you rename `LongUrl` to `OriginalUrl` in the DB. if you return the entity, every client breaks. with a DTO you map old name -> new name internally, clients don't notice

3. **different shapes for different audiences** -> admin api might need 10 fields, public api needs 3. same db row, two different DTOs

4. **validation** -> DTOs can have their own validation attributes ([Required], URL regex, etc.) without polluting the entity class

interview line -> *"DTOs decouple the API shape from the DB shape. DB can evolve without breaking clients, internal fields don't accidentally leak, different endpoints can expose different shapes from the same entity"*

---

## 11. Retry loop on UNIQUE constraint violation

when our random base62 code collides (rare but possible), the DB rejects with UNIQUE violation. we catch it and retry with a new code.

pseudocode ->
```
MAX_RETRIES = 5
for attempt in 1..MAX_RETRIES:
    code = CodeGenerator.Generate()
    entity = new ShortCode { Code = code, LongUrl = req.LongUrl }
    db.Add(entity)
    try:
        await db.SaveChangesAsync()
        return 201 Created with response
    catch UNIQUE_VIOLATION:
        continue   // try again with fresh random code
return 500 Internal Server Error  // all retries exhausted
```

EF + postgres specifics -> the unique violation comes wrapped:
- `DbUpdateException` is the EF wrapper
- inside is `Npgsql.PostgresException` with `SqlState == "23505"` (postgres standard code for "unique_violation")

clean catch using C# exception filter ->
```csharp
catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: "23505" })
{
    // retry with new code
}
```

why 5 retries -> collision odds are ~1 in millions (with 7-char base62 + low fill rate). 5 retries = ~1 in trillions of failing. practically impossible to exhaust.

---

## 12. HTTP status codes — cheat sheet

**big rule -> 4xx = client did something wrong, 5xx = server did something wrong**

### 2xx success
| code | meaning |
|---|---|
| 200 OK | generic success |
| **201 Created** | POST created a new resource (use this, not 200, for creation endpoints) |
| 204 No Content | success but no body (e.g., DELETE) |

### 3xx redirects
| code | meaning |
|---|---|
| 301 Moved Permanently | browsers cache forever (we DON'T want this for shortener — kills analytics) |
| **302 Found** | temporary redirect, not cached (what we use for GET /{code}) |

### 4xx client errors
| code | meaning |
|---|---|
| **400 Bad Request** | malformed input (invalid URL, missing field, bad JSON) |
| 401 Unauthorized | not authenticated |
| 403 Forbidden | authenticated but not allowed |
| **404 Not Found** | resource doesn't exist (e.g., short code lookup miss) |
| 409 Conflict | your action conflicts with current state |
| **429 Too Many Requests** | rate limited (we'll use this for token bucket) |

### 5xx server errors
| code | meaning |
|---|---|
| **500 Internal Server Error** | generic server bug |
| 502 Bad Gateway | proxy issue |
| 503 Service Unavailable | overloaded or maintenance |

### codes for our endpoints
| endpoint | success | failures |
|---|---|---|
| POST /shorten | 201 Created | 400 (bad URL), 500 (retries exhausted), 429 (rate limited) |
| GET /{code} | 302 Found | 404 (code doesn't exist) |
| GET /analytics/{code} | 200 OK | 404 (code doesn't exist) |

**key gotcha -> 400 vs 404 confusion.** 400 = "your input is bad, fix it." 404 = "the thing you want doesn't exist on the server." totally different problems.
