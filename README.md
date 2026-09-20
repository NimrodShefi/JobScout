# JobScout

A private, single-user job-search assistant. It scans the careers pages of companies you
care about, discovers new ones from job boards, scores every listing against your CV with
an AI model, and tracks the applications you make.

**It never applies to jobs, never sends email, and never modifies your mailbox.** The email
integration is read-only by design.

---

## What it does

Three jobs run on a schedule inside the app:

| Job | Default time | What it does |
|---|---|---|
| **Morning scan** | 07:00 | Fetches every `USE` company's careers page, extracts the adverts with the AI, drops anything outside your cities or below your minimum salary, upserts by URL, then scores everything new or unscored. |
| **Email check** | 19:00 | Reads new mail, matches each message to an open application and classifies it. Confident matches update the status; unsure ones become suggestions for you to confirm. |
| **Discovery** | 19:30 | Queries each enabled job board for every active industry × city. New companies land as `REVIEW` with a board link and no careers URL. Their listings are saved but **not scored** until you move the company to `USE`. |

Each job has a **Run now** button on the *Job runs* page and writes one summary row to
`JobRunLogs`. Runs never overlap.

---

## Privacy and security

- **Binds to `http://localhost:5179` only.** Kestrel is configured with a loopback endpoint
  and `AllowedHosts` is restricted to localhost, so nothing on your network can reach it.
- **No authentication**, because nothing but you can connect. Do not put this behind a
  public reverse proxy without adding auth first.
- **Secrets never touch the database.** API keys and the mailbox password come from
  `appsettings.json` / user secrets / environment variables only.
- **Logs never contain CV text, email bodies, or API keys.** The code that handles those
  logs counts and identifiers (a UID, a listing id) instead.
- **Email bodies are never stored.** A truncated snippet goes to the AI for classification
  and is then discarded. Only the sender address, subject and a one-line rationale are kept,
  and only on a suggestion you have yet to resolve.

---

## Requirements

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- **Required:** an API key for one AI provider (Anthropic, OpenAI, Azure OpenAI) — or a local
  [Ollama](https://ollama.com) instance, which needs no key. **JobScout refuses to start
  without a working AI configuration** (see [Fail-fast configuration](#fail-fast-configuration)).
- Optional: an Adzuna API account (free tier) for discovery
- Optional: an IMAP mailbox for the email check
- Optional: Playwright browsers for JavaScript-rendered careers pages

---

## Quick start

```bash
git clone <your-remote> JobScout
cd JobScout
dotnet build
```

Set your AI key — this is not optional, the app will not start without it
(see [AI provider](#ai-provider) for the other providers):

```bash
dotnet user-secrets set "JobScout:Ai:ApiKey" "sk-ant-..." --project src/JobScout.Web
```

Run it:

```bash
dotnet run --project src/JobScout.Web
```

Open <http://localhost:5179>. The database, the `data/` folder and the `logs/` folder are
created on first run, and migrations are applied automatically.

### First-run checklist

1. **Settings** → upload your CV (PDF or DOCX). Check the extracted text looks readable —
   nothing can be scored without it.
2. **Settings** → set your minimum salary, cities and whether remote is acceptable.
3. **Companies** → add a company with its careers page URL and set it to `USE`.
4. **Job runs** → *Run now* on the morning scan.
5. **Industries** → add a few search terms, then *Run now* on discovery to find more
   companies from the job boards.

---

## Configuration

Everything lives under the `JobScout` section of `src/JobScout.Web/appsettings.json`.
Put **secrets** in user secrets rather than that file:

```bash
dotnet user-secrets set "<key>" "<value>" --project src/JobScout.Web
dotnet user-secrets list --project src/JobScout.Web
```

Environment variables work too, with `__` for `:` —
`JobScout__Ai__ApiKey=sk-ant-...`.

Precedence, lowest to highest: `appsettings.json` → `appsettings.{Environment}.json` →
user secrets → environment variables → command line. So an environment variable always
overrides a stored secret, which is what makes one-off runs easy to test.

### Fail-fast configuration

The AI is not an optional extra — without it nothing can be scored — so **JobScout validates
its AI settings at start-up and refuses to run if they cannot work**. The check happens
before the database is touched and before any port is opened, so a bad configuration can
never leave you with a half-running app that silently fails its first scan hours later.

A rejected configuration exits with code `1` and prints what is wrong, with no stack trace:

```
[FTL] JobScout cannot start because its configuration is not valid:
[FTL]   - 'JobScout:Ai:ApiKey' is required for the Anthropic provider. Set it with:
          dotnet user-secrets set "JobScout:Ai:ApiKey" "<your key>" --project src/JobScout.Web
[FTL] Fix the settings above and start JobScout again. See the README for examples.
```

Every problem is reported at once rather than one per restart, and no message ever echoes
your key back.

What is rejected:

| Setting | Rejected when |
|---|---|
| `Provider` | Blank or not one of `Anthropic`, `OpenAI`, `AzureOpenAI`, `Ollama`. Removing the key entirely is fine — that means the default, `Anthropic`. Leaving it blank is not the same thing. |
| `ApiKey` | Missing for any provider except Ollama, which authenticates nobody. Also rejected when it has leading or trailing whitespace, which otherwise surfaces much later as an unexplained `401`. |
| `Endpoint` | Missing for `AzureOpenAI`; not an absolute `http(s)` URL; a full operation URL where Azure wants the resource root; or set at all for `Anthropic`, which has no custom endpoint and would silently ignore it. |
| `DeploymentName` | Set on any provider other than `AzureOpenAI`, which would silently ignore it. |
| `TimeoutSeconds`, `MaxDescriptionChars`, `MaxCvChars` | Zero or negative. |
| `MaxJsonRetries` | Negative. Zero is fine and means a single attempt. |

The last two endpoint rules exist because a half-finished switch between providers is the
easiest mistake to make and the hardest to diagnose: an Azure endpoint left behind on
`Provider: "Anthropic"` would start cleanly, then call Anthropic with an Azure key and fail
on the first scan with an authentication error that explains nothing.

Beyond the settings themselves, the provider client is actually constructed at start-up, so
anything the SDK rejects also surfaces immediately. No network call is made — a key that is
present but wrong is only discovered on first use, and shows up as a failed run.

Email and job boards are **not** treated this way. They are genuinely optional: leave them
unconfigured and the relevant job records "not configured" in its run summary and moves on.

### AI provider

The matcher is built on `Microsoft.Extensions.AI`, so any chat provider works. Switch by
changing `Provider`; the rest of the app is unaffected.

<details open>
<summary><b>Anthropic</b> (default)</summary>

```json
"Ai": {
  "Provider": "Anthropic",
  "Model": "claude-sonnet-5"
}
```
```bash
dotnet user-secrets set "JobScout:Ai:ApiKey" "sk-ant-..." --project src/JobScout.Web
```
</details>

<details>
<summary><b>OpenAI</b></summary>

```json
"Ai": {
  "Provider": "OpenAI",
  "Model": "gpt-4.1-mini"
}
```
```bash
dotnet user-secrets set "JobScout:Ai:ApiKey" "sk-..." --project src/JobScout.Web
```
</details>

<details>
<summary><b>Azure OpenAI</b></summary>

```json
"Ai": {
  "Provider": "AzureOpenAI",
  "Model": "gpt-4.1-mini",
  "DeploymentName": "my-deployment",
  "Endpoint": "https://my-resource.openai.azure.com/"
}
```
```bash
dotnet user-secrets set "JobScout:Ai:ApiKey" "<azure key>" --project src/JobScout.Web
```

`DeploymentName` is only needed when it differs from the model id.
</details>

<details>
<summary><b>Ollama</b> (local, no key)</summary>

Reached through Ollama's OpenAI-compatible endpoint.

```json
"Ai": {
  "Provider": "Ollama",
  "Model": "llama3.1",
  "Endpoint": "http://localhost:11434"
}
```

Then `ollama pull llama3.1`. Small local models are noticeably worse at returning strict
JSON; raise `MaxJsonRetries` if you see scoring failures in the run log.
</details>

Other AI settings:

| Key | Default | Meaning |
|---|---|---|
| `MaxJsonRetries` | `2` | Re-asks when the model returns unusable JSON. |
| `TimeoutSeconds` | `120` | Per-request timeout. |
| `MaxDescriptionChars` | `12000` | Job text is truncated to this before being sent. |
| `MaxCvChars` | `20000` | CV text is truncated to this before being sent. |

### Scoring cost control

```json
"Scoring": {
  "MaxListingsPerRun": 50,
  "MaxConcurrency": 2
}
```

`MaxListingsPerRun` is a hard cap on AI calls per run — the rest wait for the next one. A
listing whose title, location and description have not changed since it was scored is
skipped entirely (tracked by a content hash), so re-running a scan costs almost nothing.

### Job boards

Adzuna is implemented. [Register for free keys](https://developer.adzuna.com/).

```json
"Boards": {
  "Adzuna": {
    "Enabled": true,
    "Country": "gb",
    "MaxResultsPerQuery": 50
  }
}
```
```bash
dotnet user-secrets set "JobScout:Boards:Adzuna:AppId"  "<app id>"  --project src/JobScout.Web
dotnet user-secrets set "JobScout:Boards:Adzuna:AppKey" "<app key>" --project src/JobScout.Web
```

**Adding another board** means one new class implementing `IJobBoardProvider` plus its own
options section — register it in `AddJobScoutBoards` and discovery picks it up automatically,
because it resolves `IEnumerable<IJobBoardProvider>` and asks every enabled one.

### Email (read-only)

Generic IMAP via MailKit. The folder is opened with `FolderAccess.ReadOnly`, so the server
itself refuses any write.

```json
"Email": {
  "Provider": "Imap",
  "Imap": {
    "Host": "imap.gmail.com",
    "Port": 993,
    "UseSsl": true,
    "Folder": "INBOX"
  },
  "BodySnippetChars": 1500,
  "AutoApplyConfidence": 0.8,
  "MaxMessagesPerRun": 100,
  "InitialLookbackDays": 14
}
```
```bash
dotnet user-secrets set "JobScout:Email:Imap:Username" "you@gmail.com"     --project src/JobScout.Web
dotnet user-secrets set "JobScout:Email:Imap:Password" "<app password>"    --project src/JobScout.Web
```

> **Gmail:** use an [App Password](https://myaccount.google.com/apppasswords), not your
> account password, and make sure IMAP is enabled in Gmail settings.
> **Outlook/Microsoft 365:** `outlook.office365.com:993`. Basic auth is disabled on many
> tenants — if so, a Graph provider is the route (see below).

`AutoApplyConfidence` is the threshold: at or above it the status changes automatically and
a history row is written; below it a **suggestion** appears on the *Applications* page for
you to accept or dismiss. An email can never drag an application backwards (a late
acknowledgement after an interview is booked becomes a suggestion), and nothing moves out of
`Rejected` or `Withdrawn` automatically.

**Adding Gmail API or Microsoft Graph** means one new class implementing `IEmailProvider`
(`MailboxKey`, `IsConfigured`, `FetchSinceAsync`) and a new value on the `EmailProviderKind`
enum. Nothing else changes.

### Page fetching

Plain `HttpClient` first. If the result looks JavaScript-rendered — very little visible text,
SPA mount markers like `<div id="root">`, no job-like words — it retries once with headless
Chromium via Playwright.

```json
"Fetching": {
  "TimeoutSeconds": 30,
  "MaxConcurrency": 3,
  "PolitenessDelayMs": 1500,
  "RespectRobotsTxt": true,
  "EnableBrowserFallback": true,
  "RetryBlockedPagesWithBrowser": true,
  "MaxCrawlDelaySeconds": 30,
  "JsHeuristicMinTextLength": 600,
  "MaxPageTextChars": 60000
}
```

`robots.txt` is fetched, cached for 12 hours and honoured: `Allow`/`Disallow`, wildcards,
`$` anchors, a group naming `JobScout` taking precedence over `*`, and the host's declared
`Crawl-delay`. Where a host asks for a longer delay than `PolitenessDelayMs`, its request
wins (capped by `MaxCrawlDelaySeconds` so one large value cannot stall a whole run). A host
that does not publish robots.txt is treated as allowing everything, which is what RFC 9309
says. Disallowed pages are skipped and counted in the run summary.

#### Sites that refuse automated clients

Large careers sites often sit behind bot protection that answers anything not shaped like a
browser with **HTTP 403** — frequently including `robots.txt` itself. When that happens:

- **robots.txt is re-read through the headless browser.** Treating an unreadable robots.txt
  as "no rules" would mean ignoring real `Disallow` rules, which is worse than useless. If it
  still cannot be read, the run says so rather than pretending the host has no policy.
- **The page is retried through the headless browser**, which is how an ordinary visitor
  reaches it. Set `RetryBlockedPagesWithBrowser: false` to disable.
- **robots.txt remains the authority.** The browser changes *which client* fetches a page,
  never *whether a disallowed path may be fetched*. A path under `Disallow` is refused no
  matter which client is used.

This needs the Playwright browsers installed. Without them, the run summary reports the
company as having refused automated access and points you at the job-board route instead.

Consent banners (OneTrust, Cookiebot, TrustArc and friends) are stripped from the page text
before it reaches the AI. On one real careers page that was 63% of the text — pure cost, and
enough to push the actual adverts past `MaxDescriptionChars`.

### Scheduling

```json
"Scheduling": {
  "Enabled": true,
  "TimeZone": "Europe/London",
  "MorningScanCron": "0 0 7 * * ?",
  "EmailCheckCron": "0 0 19 * * ?",
  "DiscoveryCron": "0 30 19 * * ?"
}
```

Six-field expressions include seconds; five-field standard cron also works. `?` is accepted
as a synonym for `*`. `TimeZone` takes an IANA id and is honoured across daylight-saving
changes, so 07:00 stays 07:00 all year. An invalid expression is logged, that job is shown
as `invalid cron` in the UI, and it remains runnable by hand — it does not stop the app.

Set `Enabled: false` to run everything manually.

### Logging

Serilog writes rolling daily files to `src/JobScout.Web/logs/jobscout-<date>.log`, keeping
30 days.

```json
"Serilog": {
  "WriteTo": [
    { "Name": "File",
      "Args": { "path": "logs/jobscout-.log", "rollingInterval": "Day", "retainedFileCountLimit": 30 } }
  ]
}
```

Change `retainedFileCountLimit` to keep more or fewer days. `MinimumLevel.Default: "Debug"`
gives per-page-fetch and per-listing detail.

The `JobRunLogs` table deliberately holds **one summary row per run** for the UI; everything
diagnostic is in the files.

---

## Playwright browser install

Only needed for careers pages that render their job list in the browser. Skip it and set
`Fetching:EnableBrowserFallback: false` if you do not want it — the app detects a missing
browser, logs a clear message once, and carries on with plain HTTP.

After a build, run the generated install script:

```bash
pwsh src/JobScout.Web/bin/Debug/net10.0/playwright.ps1 install chromium
```

If `pwsh` is not installed: `dotnet tool install --global PowerShell`.

Chromium alone is enough — that is the only browser this app launches.

---

## Development

```bash
dotnet build                      # build everything
dotnet test                       # run the unit tests
dotnet run --project src/JobScout.Web
```

### Migrations

```bash
dotnet tool install --global dotnet-ef
dotnet ef migrations add <Name> --project src/JobScout.Infrastructure --startup-project src/JobScout.Web --output-dir Data/Migrations
```

Migrations are applied automatically at startup.

### Project layout

```
src/JobScout.Core             entities, enums, provider interfaces, options, pure domain rules
src/JobScout.Infrastructure   EF Core + SQLite, migrations, every provider implementation
src/JobScout.Web              Blazor Server UI, scheduled jobs, host
tests/JobScout.Tests          unit tests against fake providers - no network, no API key
```

`Core` has no dependency on EF, HTTP or any provider SDK. The pure decision rules
(`ListingFilter`, `ApplicationStatusRules`, `CompanyNameNormaliser`) live there, which is why
they are cheap to test.

### The provider seams

| Interface | Implementation | Swap by |
|---|---|---|
| `IJobMatcher` | `AiJobMatcher` over `IChatClient` | changing `Ai:Provider` |
| `IPageFetcher` | `PoliteHttpPageFetcher` → `PlaywrightPageFetcher` | — |
| `IEmailProvider` | `ImapEmailProvider` | new class + `EmailProviderKind` value |
| `IJobBoardProvider` | `AdzunaJobBoardProvider` | new class + options section |
| `ICvTextExtractor` | `CvTextExtractor` (PdfPig / OpenXml) | — |

### Database notes

SQLite with **WAL** enabled, so the UI reads while a background job writes. Background
services take short-lived contexts from `IDbContextFactory` rather than sharing the Blazor
circuit's scoped context, and transactions are kept to a single unit of work.

`DateTimeOffset` is stored as UTC ticks (`INTEGER`) through a value converter — SQLite has no
date type and refuses to `ORDER BY` a `DateTimeOffset`, so this keeps sorting and range
filters in the database rather than forcing client-side evaluation.

---

## Troubleshooting

| Symptom | Cause and fix |
|---|---|
| The app exits immediately with *cannot start because its configuration is not valid* | Read the lines underneath it — each names the exact setting and how to fix it. This is by design: see [Fail-fast configuration](#fail-fast-configuration). |
| Morning scan reads 0 pages | Companies need `Status = USE` **and** a careers URL. The dashboard flags any that are missing one. |
| Nothing gets scored | No CV uploaded, or the companies are `REVIEW`/`NOT_USE`. Only `USE` companies are scored. |
| Discovery finds nothing | Check the board is `Enabled` with valid keys, and that at least one industry is active. |
| A careers page yields no jobs | Likely JavaScript-rendered. Install the Playwright browsers, or check the log for a `robots.txt disallows` line. |
| A company reports **HTTP 403 Forbidden** | The site blocks non-browser clients. Install the Playwright browsers and it is retried automatically — see [Sites that refuse automated clients](#sites-that-refuse-automated-clients). If the path is genuinely under `Disallow`, use the job-board route instead. |
| CV text comes out garbled | The PDF is probably a scan with no text layer. Re-export it as a text PDF, or upload the DOCX. |
| Email check says *No mailbox configured* | Set `JobScout:Email:Provider` to `Imap` and fill in the host, username and password. |
| Scoring costs more than expected | Lower `Scoring:MaxListingsPerRun`, or use a cheaper model. |

---

## Out of scope, permanently

JobScout does not apply to jobs, does not send email, and does not modify your mailbox.
These are deliberate constraints, not missing features.
