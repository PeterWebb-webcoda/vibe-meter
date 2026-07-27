# Fix brief: cost calculation accuracy

**Raised**: 27/07/2026
**Severity**: High. The Claude weekly figure is currently overstated by roughly 2.75x.
**Scope**: `VibeMeter/Providers/Claude/ClaudeCostCalculator.cs`, `VibeMeter/Providers/Codex/CodexCostCalculator.cs`

---

## Read this first: what is NOT a bug

**The rolling 7-day cost window is correct. Do not change it.**

`weekCost` uses `now.AddDays(-7)` while the quota gauge tracks the provider's own reset window.
That difference is deliberate and right. Provider quota weeks reset erratically (ChatGPT reset
seven times in a fortnight on this account) and the user can trigger manual resets when running
low. A cost trend needs a stable window; a quota gauge needs the provider's window. They are
answering different questions and must not be unified.

If you "fix" this you have broken the tool. Leave it alone.

---

## Bug 1 (critical): Opus is billed at 3x the real rate

**File**: `Providers/Claude/ClaudeCostCalculator.cs`, in `CalculateCost`, the `opus` branch.

```csharp
if (model.Contains("opus", StringComparison.OrdinalIgnoreCase))
{
    inPrice = 15.00m; outPrice = 75.00m; cwPrice = 18.75m; crPrice = 1.50m;
}
```

Those are Opus 3-era rates. **Claude Opus 4.8 and Opus 5 are both $5 input / $25 output per
million tokens.** Anthropic priced Opus 5 at deliberate parity with 4.8 at launch on 24 July 2026.

Correct values, using Anthropic's standard cache ratios (cache write = 1.25x input, cache read =
0.1x input):

| Rate | Current (wrong) | Correct |
|---|---|---|
| input | 15.00 | **5.00** |
| output | 75.00 | **25.00** |
| cache write | 18.75 | **6.25** |
| cache read | 1.50 | **0.50** |

**Why this went unnoticed**: line ~209 prefers a transcript-native `costUSD` and only falls back to
this table. Claude Code transcripts on this machine contain **zero** `costUSD` fields, so the
fallback is doing 100% of the work while being documented as a fallback.

**Impact on the current reading** (weekly, rolling 7 days):

| Model | Reported | Corrected |
|---|---|---|
| claude-opus-4-8 | $2,775.28 | ~$925.09 |
| claude-opus-5 | $805.03 | ~$268.34 |
| **Claude total** | **$3,806.13** | **~$1,380** |

---

## Bug 2: substring matching guarantees this recurs

`model.Contains("opus")` matches every Opus that has ever existed and every one that ever will.
When a generation reprices, the table silently keeps charging the old rate. That is exactly how
bug 1 survived two model generations.

Replace the substring cascade with an explicit, dated rate table keyed on model id, with a
documented match order (longest/most-specific id first), and:

- **Each entry carries a `LastVerified` date and a source URL.**
- **An unmatched model must be loud, not silent.** Today an unknown model quietly inherits the
  Sonnet defaults. It should fall back *and* surface a visible "estimated rate, model not in table"
  marker in the cost details window, so a wrong number announces itself.

Suggested shape (adapt to the codebase's conventions):

```csharp
readonly record struct ModelRate(
    decimal Input, decimal Output, decimal CacheWrite, decimal CacheRead,
    string LastVerified, string Source);
```

---

## Bug 3: token counts are not comparable across providers

Claude, line ~89:
```csharp
long totalTokens = r.Input + r.Output + r.CacheWrite + r.CacheRead;
```

Codex, line ~106:
```csharp
long totalTokens = r.Input + r.Output;   // cached input excluded
```

Claude's total includes cache reads. Codex's does not. Cache reads are the cheapest tokens there
are and they come in bulk, so the Claude figure is inflated with near-free tokens while the Codex
figure isn't. The UI then puts "540.3M" and "77.3M" next to each other as if they measure the same
thing, which invites exactly the wrong conclusion about relative efficiency.

Pick one and apply it consistently. Either is defensible, but it must be the same on both sides:

- **Option A (recommended)**: exclude cache reads from the headline token count on both providers,
  and show cached reads as a separate secondary line. Headline tokens then roughly track spend.
- **Option B**: include cached input on both, and label the headline "tokens processed" rather than
  implying billable volume.

Whichever you choose, **label the metric in the UI** so the two provider panels can be compared
honestly.

---

## Bug 4: unverified rates should say so

Two rate sets in the code are guesses and one admits it in a comment:

- **Fable**: `// Fable pricing is currently roughly the same as sonnet in general, assuming defaults here.`
- **Codex frontier tier** (`gpt-5.6-sol/terra/luna`, `gpt-5.5`) at $5 in / $15 out: not verified.
- **Sonnet 5** at $3 / $15: not verified against current published rates.

Verify each against the provider's own pricing page, record the `LastVerified` date, and mark any
that stay unverified in the UI rather than presenting them at the same confidence as known rates.

> **Resolved 27/07/2026.** All three verified against
> [OpenAI's rate card](https://developers.openai.com/api/docs/pricing) and Anthropic's model docs.
> Fable was wrong, not merely unverified ($10/$50, not Sonnet-priced). The Codex rates were wrong
> in two compounding ways, both worse than the brief assumed:
>
> - **`"5.6"` was matched as a substring**, collapsing three variants whose input rates differ by
>   5x (sol $5.00, terra $2.50, luna $1.00). Every Luna turn was priced as a Sol turn.
> - **Cached input was charged at 50% of input; OpenAI's discount is 90%** (cached input is 0.1x).
>   Cached tokens are ~98.5% of Codex input volume here, so this was the dominant error.
>
> Output rates were also understated 2x on sol and 5.5 ($30, not $15). Net on a fixed corpus, the
> Codex weekly figure moves **$4,625.79 → $1,031.14** (sol $2,021 → $620, terra $1,675 → $226,
> luna $476 → $30, 5.5 $454 → $155).
>
> Two published OpenAI rules are deliberately not modelled, and are recorded at `CalculateCost`:
> cache writes (1.25x input — but `cache_write_input_tokens` is zero across all 24,472 turns on
> this machine) and the >272K-token premium tier (2x input / 1.5x output, affecting 1.27% of
> turns; how cached tokens are treated under it isn't documented clearly enough to implement
> without guessing).

---

## Bug 5 (minor): cache write has only one tier

Anthropic bills a 5-minute cache write at 1.25x input and a 1-hour cache write at 2x input. The
calculator models a single `cwPrice`. If the transcripts distinguish the two, price them
separately; if they don't, note the assumption in the code comment so the next reader knows it's a
simplification rather than an oversight.

---

## Acceptance criteria

- [ ] Opus 4.8 and Opus 5 bill at 5 / 25 / 6.25 / 0.50
- [ ] Rates live in one dated table keyed on explicit model ids, not substring `Contains`
- [ ] An unknown model still produces a number, but the UI marks it as estimated
- [ ] Claude and Codex headline token counts use the same definition, and the UI names it
- [ ] Every rate entry has a `LastVerified` date and source URL
- [ ] The rolling 7-day cost window is unchanged
- [ ] Existing per-file mtime caching still works; a rate-table change should invalidate cached
      cost aggregates, since cached records currently store a computed `Cost`

⚠️ **That last point is a real trap.** `FileCache` stores parsed records including their computed
`Cost`. Changing the rate table will not recompute cached entries until each file's mtime changes.
Either store raw token counts and compute cost at fold time, or version the cache key on the rate
table so a pricing change busts it. **Without this, the fix appears not to work.**

## Verification

> **Resolved 27/07/2026 in `25f6bb2`.** The original targets in this section were wrong — they
> were derived from the buggy cost basis described in Bug 6 below, which was not known when this
> brief was written. They predicted the weekly figure dropping to ~$1,380. **Do not "fix" the
> calculator to hit that number.** Corrected figures follow.

Measured before/after on a fixed corpus (by running the pre-fix code against the same transcripts,
not by comparing against an earlier snapshot — the rolling window moves as you work, so a
wall-clock comparison is not a valid test):

| | before | after |
|---|---|---|
| weekly total | $3,917.80 | $3,780.16 |
| `claude-opus-4-8` | $2,788.18 | $2,194.84 |
| `claude-opus-5` | $928.61 | $801.09 |
| `claude-sonnet-5` | $113.70 | $467.97 |
| `claude-fable-5` | $42.73 | $251.81 |

The rate fix on its own would have produced roughly the originally-predicted numbers
(Opus 4.8 ≈ $929). Correctly charging cache reads — Bug 6 — fills the difference back in, and
Sonnet 5 rises rather than falls because its cache-read volume is large (1.96B tokens) relative
to its other usage.

**If the numbers do not move at all**, you have hit the cache trap above: `FileEntry` must not
store a computed cost.

---

## Bug 6 (found while implementing; not in the original brief): cache reads were never billed

The pre-fix `CalculateCost` contained:

```csharp
decimal inputCost = ((input - cacheRead) / 1_000_000m) * inPrice + (cacheRead / 1_000_000m) * crPrice;
if (inputCost < 0) inputCost = (input / 1_000_000m) * inPrice; // fallback if cacheRead > input logic is weird
```

This assumed `input_tokens` was inclusive of cache reads. It is not — in Anthropic's usage block
`input_tokens` is already the **uncached remainder**, and `cache_read_input_tokens` /
`cache_creation_input_tokens` are reported separately (total prompt size is the sum of all three).
So the subtraction went hugely negative — 1.7M − 2,531M on `claude-opus-4-8` — the negative guard
fired, and **every cent of cache-read cost was silently discarded**.

That mattered more than any rate error in the brief: cache reads are by far the largest token
volume (2.53B read vs 110M written vs 1.7M uncached input on Opus 4.8 alone), and Anthropic bills
them at 0.1× input. On that one model it was $1,265.45 of invisible cost.

Fixed by pricing each bucket independently against its own rate, with no subtraction. The
comment at `CalculateCost` documents the token semantics so the assumption isn't reintroduced.
