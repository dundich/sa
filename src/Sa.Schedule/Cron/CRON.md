# Cron Timing in Sa.Schedule

`Sa.Schedule` computes the next firing time of cron expressions through `CronTiming`.

## Overview

- Format: **5 fields** — `minute hour day-of-month month day-of-week`
- Interface: `IJobTiming`
- Method: `GetNextOccurrence(DateTimeOffset dateTime)`
- Behavior: **exclusive** — when `dateTime` exactly matches the schedule, the **next** occurrence (> dateTime) is returned, truncated to the minute.
- Search horizon: **28 years** ahead. If no matching time is found within 28 years, `null` is returned (the expression can never fire, e.g. `0 0 30 2 *`). An input too close to the end of the representable range (year 9971 and later) also returns `null` instead of throwing, because the 28-year window no longer fits a `DateTimeOffset`.
- DOM ∩ DOW semantics: **OR** (Vixie cron). When both day-of-month and day-of-week are restricted, a day matches if **either** field matches.
- Day of week: `0` and `7` both mean **Sunday**; `1–6` are Monday–Saturday.

## Field format

| Field | Range | Description |
|---|---|---|
| Minute | 0–59 | `*`, `,`, `-`, `/` |
| Hour | 0–23 | `*`, `,`, `-`, `/` |
| Day of month (DOM) | 1–31 | `*`, `,`, `-`, `/` |
| Month | 1–12 | `*`, `,`, `-`, `/` |
| Day of week (DOW) | 0–7 (0 and 7 = Sunday) | `*`, `,`, `-`, `/` |

## Supported syntax

| Syntax | Example | Description |
|---|---|---|
| `*` | `* * * * *` | Every minute |
| Single value | `0 9 * * *` | At 09:00 every day |
| List | `10,30,50 * * * *` | At minutes 10, 30 and 50 |
| Range | `30 14 * * 1-5` | At 14:30, Monday–Friday |
| Step | `*/15 * * * *` | Every 15 minutes (0, 15, 30, 45) |
| Range with step | `0 9-17/2 * * *` | Every 2 hours from 9 to 17 (9, 11, 13, 15, 17) |
| Step from a value | `0 8-17/3 * * *` | Every 3 hours: 8, 11, 14, 17 |
| Last day of month | `0 0 L * *` | At midnight on the last day of the month |
| Last day with offset | `0 0 L-2 * *` | Two days before the end of the month |
| Last weekday | `0 0 LW * *` | The last day of the month if it is a weekday (Mon–Fri); never shifted |
| Nearest weekday | `0 0 15W * *` | The weekday nearest to the 15th (never leaves the month) |
| Last given weekday | `0 0 * * 5L` | The last Friday of the month |
| Last Sunday | `0 0 * * 0L` | The last Sunday of the month |

## Examples

| Expression | Description |
|---|---|
| `* * * * *` | Every minute |
| `0 * * * *` | Every hour at minute 0 |
| `0 0 * * *` | Daily at midnight |
| `0 0 * * 0` | Weekly, Sunday at midnight |
| `0 0 * * 7` | Same (Sunday) |
| `0 9 * * 1-5` | Monday–Friday at 09:00 |
| `0 0 1 * *` | The 1st of every month at midnight |
| `0 0 1 1 *` | January 1 at midnight (New Year) |
| `*/15 * * * *` | Every 15 minutes |
| `0 */2 * * *` | Every 2 hours |
| `10,30,50 * * * *` | At minutes 10, 30, 50 of every hour |
| `0 12 1-15 * *` | At 12:00, from the 1st to the 15th of every month |
| `0 0 29 2 *` | February 29 (leap years only; the next leap year is searched within the 28-year horizon) |
| `0 0 15 * 1` | The 15th **or** Monday (DOM∩DOW OR semantics) |
| `0 12 15 * 3` | At 12:00 on the 15th **or** on a Wednesday |

## Day specials: L, L-n, LW, dW

The day-of-month field supports the four Quartz-style specials:

| Special | Example | What it does |
|---|---|---|
| `L` | `0 0 L * *` | The last day of the month |
| `L-n` | `0 0 L-2 * *` | The day `n` days before the last day of the month |
| `LW` | `0 0 LW * *` | The last day of the month if it is a weekday (Mon–Fri) |
| `dW` | `0 0 15W * *` | The weekday nearest to day `d`, without leaving the month |

Concrete next occurrences (searched exclusively from the given instant, > dateTime):

| Expression | From | Next | Note |
|---|---|---|---|
| `0 0 L * *` | 2026-06-25 | 2026-06-30 | Last day of June (the 30th) |
| `0 0 L-2 * *` | 2026-06-25 | 2026-06-28 | 30 − 2 = the 28th |
| `0 0 LW * *` | 2026-06-25 | 2026-06-30 | The month ends on a Tuesday → the 30th |
| `0 0 15W * *` | 2026-06-01 | 2026-06-15 | The 15th is a Monday → matches as is |
| `0 0 15W * *` | 2026-07-20 | 2026-08-14 | Aug 15 is a Saturday → shifted to Friday the 14th |

Details:

- `L-2` means two days before the last day of the month. The exact date depends on the month length: the 29th in a 31-day month, the 28th in a 30-day month, the 26th in a 28-day February, the 27th in a 29-day February (leap year).
- The `L-n` offset is limited to `0`–`30` (`L-0` is exactly `L`); a larger offset is a parse error. Because the offset is resolved per month, `L-30` has no occurrence in a month shorter than 31 days.
- `LW` here means **"the last day of the month, if it is a weekday"** — if the month ends on a Saturday or Sunday, there is no occurrence in that month (in 2026, `0 0 LW * *` skips Jan 31, Feb 28, May 31 and Oct 31). This deliberately differs from Quartz, whose `LW` always fires and shifts backwards to the preceding Friday. There is no single expression that reproduces the Quartz rule for every month length.
- `dW` never crosses the month boundary. A Saturday shifts to the previous Friday (except a Saturday on the 1st, which shifts to Monday the 3rd); a Sunday shifts to the following Monday (except a Sunday on the last day, which shifts to the previous Friday).
- `dL` (day-of-week field only): `0 0 * * 5L` fires on the last Friday of the month, `0 0 * * 0L` on the last Sunday.
- `L`/`L-n`/`LW` cannot be combined with a separate `dW` element in the same list (`0 0 15W,L * *` is rejected): the two have no defined precedence. `LW` itself is the supported combination.

## Special cases

### DOM ∩ DOW (OR)
When both fields are set and neither is a wildcard, the day matches if **either** field matches. For example `0 0 15 * 1` fires on the **15th** (whatever the weekday) **and** on **every Monday**.

A field whose values cover the whole range (`*`, `*/1`, `1-31`, `0-7`) is treated as a wildcard, exactly as the literal `*` is.

### February 29
`0 0 29 2 *` does not match in a non-leap year — the search continues and returns February 29 of the **next leap year** (within the 28-year horizon).

### Unsatisfiable expressions
Expressions that can never fire (for example `0 0 30 2 *` — February 30 never exists) return `null`.

### Time zone
`CronTiming` works with `DateTimeOffset`. The input offset is preserved when the next occurrence is computed: the fields are matched against the input's wall clock, and a DST transition is **not** re-evaluated. The scheduler always passes UTC, and `CronTimingAdapter` (used by `WithCron` / `WithTimeZone`) re-derives the offset for each occurrence from the configured zone. When using `CronTiming` directly, pass a UTC `DateTimeOffset`.

### Minute precision
Computations are minute-precise. When the input `dateTime` has seconds/milliseconds, the search starts at the next minute (exclusive behavior).

## Usage

```csharp
var timing = new CronTiming("0 9 * * 1-5");
var next = timing.GetNextOccurrence(DateTimeOffset.UtcNow);
```

```csharp
var timing = CronTiming.For("*/15 * * * *", name: "every-15-min");
```

## Strict calendar validation

By default (`new CronTiming(...)`) parsing does not check calendar validity: an impossible day+month combination (for example `31 4` or `30 2`) simply never fires, and `GetNextOccurrence` returns `null`.

To catch these at construction time, use `CronTiming.Strict(...)`:

```csharp
var timing = CronTiming.Strict("0 0 31 4 *", name: "bad-april-31");
```

`CronTiming.Strict(...)` checks whether the expression can ever fire. A fixed day of month that does not exist in the selected month (for example `31 4`, `30 2`), with no day-of-week restriction, throws `CronParseException` at construction time.
Day `29 2` is allowed, because the 28-year search horizon contains leap years.

The same check applies to the day specials: `31W 2` (February has no 31st) and `L-30 2` (shorter than any February) are rejected, while `L 2`, `29W 2` and `1W 2` are accepted.

The job's `Cron` option in `ScheduleOptions` — whether it comes from the configuration section or from `b.Options(...)` — is always validated strictly: an expression that can never fire is a configuration error (`OptionsValidationException`) rather than a job that silently never runs. `WithCron(...)`, `new CronTiming(...)` and `CronTiming.For(...)` stay lenient (they reject malformed syntax only), so a timing built imperatively can still be probed for `null`.

## Not supported

These are rejected with `CronParseException` — they are never silently ignored:

- `?` (Quartz "no specific value") — use `*` instead.
- `#` (nth weekday of the month, for example `5#3`).
- Month and weekday names (`JAN`, `MON`) and `@daily`-style macros.
- A seconds field: the expression is strictly 5 fields.
- Lowercase specials (`l`, `w`): only uppercase `L` and `W` are recognized.

## Exceptions

- `CronParseException` — the cron expression cannot be parsed (wrong number of fields, invalid ranges, steps, out-of-range values) or, under `Strict`, can never fire. It derives from `FormatException`; `Field` and `Token` name the offending part.

## Notes

- The 28-year horizon is chosen for the leap-year cycle (it guarantees the next leap-year February 29 is found for `0 0 29 2 *`).
- The algorithm uses bit flags and jump tables (optimized for next-occurrence search).
