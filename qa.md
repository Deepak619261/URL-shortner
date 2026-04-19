# Interview Q&A — URL Shortener

> **How to use this doc:** Fill in EVERY answer in your own words. 2–4 sentences each. Don't copy-paste from tradeoffs.md or concepts.md — rewriting helps you internalize. Rehearse out loud before interviews. If you blank on any question, that's the one to drill again.

---

## A. Design choices (the most-asked category)

### A1. Why random base62 codes instead of auto-increment?
**A:** _your answer here (2-4 sentences)_

### A2. Why 7 characters specifically — not 5, not 10?
**A:** _your answer here_

### A3. Why PostgreSQL and not MongoDB / DynamoDB / MySQL?
**A:** _your answer here_

### A4. Why Redis as a cache? What's the cost if it goes down?
**A:** _your answer here_

### A5. Why cache-aside instead of write-through or write-behind?
**A:** _your answer here_

### A6. Why token bucket rate limiting? Why not fixed window?
**A:** _your answer here_

### A7. Why async click logging? What's the trade-off?
**A:** _your answer here_

---

## B. Scale questions

### B8. How would you handle 10× more traffic?
**A:** _your answer here_

### B9. If traffic hit 100k RPS, what breaks first? How would you fix it?
**A:** _your answer here_

### B10. How would you shard the database if `short_codes` grew to 10 billion rows?
**A:** _your answer here_

### B11. What's your expected cache hit rate, and why does it matter?
**A:** _your answer here_

---

## C. Correctness questions

### C12. What happens if two requests try to insert the same random code at the same time?
**A:** _your answer here_

### C13. If the same long URL is shortened twice, do users get the same code or different codes? Why?
**A:** _your answer here_

### C14. How do you prevent malicious URLs (phishing, malware) from being shortened?
**A:** _your answer here_

### C15. What's the failure mode if Postgres is down but Redis is up?
**A:** _your answer here_

---

## D. Operational questions

### D16. How would you monitor this in production? What metrics matter most?
**A:** _your answer here_

### D17. A user reports "sometimes the redirect is slow" — how do you debug it?
**A:** _your answer here_

### D18. How would you support custom short codes (user-chosen slugs)?
**A:** _your answer here_

### D19. How would you add user authentication without breaking the existing schema?
**A:** _your answer here_

### D20. What's the biggest weakness of your current design — honestly?
**A:** _your answer here. **Have a real answer here. Interviewers love hearing self-aware critique. Don't fake humble; pick a genuine limitation and propose how you'd fix it.**_

---

## Bonus — questions specific to YOUR implementation

### E21. Why did you pick a Lua script for the rate limiter instead of WATCH/MULTI/EXEC?
**A:** _your answer here_

### E22. Why did you use `Channel<T>` + `BackgroundService` instead of Kafka or RabbitMQ?
**A:** _your answer here_

### E23. Why is `ConnectionMultiplexer` registered as a singleton?
**A:** _your answer here_

### E24. Walk me through what happens when a user POSTs to /shorten.
**A:** _step-by-step from request hitting the server to the 201 response. Touch on: rate limit check → URL validation → code generation → DB insert with retry on UNIQUE → response with full short URL._

### E25. Walk me through what happens when a user clicks a short URL.
**A:** _step-by-step: GET → cache check (hit/miss) → DB fallback if miss → cache populate → 302 redirect → enqueue ClickEvent → BackgroundService bulk-flushes every 5s._

---

## Practice schedule

- **First week:** answer 5 questions per day (3 days = all 25 done)
- **Week 2:** rehearse out loud, fix anything that sounds shaky
- **Day before each interview:** read top to bottom in the morning. Don't memorize — just refresh

**Test yourself:** can you answer Q1 cold without looking at this doc? If not, drill it. The questions you blank on are exactly the ones interviewers will sense weakness on.
