# Bot Telegram Notifier

A .NET 10 automation bot that scrapes job listings from multiple career pages daily, filters them using keyword rules, summarizes them with an LLM, and delivers matched results directly to a **Telegram chat** — all driven by GitHub Actions on a scheduled pipeline.

---

## ⚙️ What It Does

1. **Scrapes** configured job board URLs using [Microsoft Playwright](https://playwright.dev/dotnet/) (headless Chromium) with a plain HTTP fallback.
2. **Cleans** raw HTML by stripping scripts, styles, and tag attributes to reduce LLM token usage.
3. **Parses** job listings using [GitHub Models](https://github.com/marketplace/models) (`gpt-4o-mini` via Azure AI Inference) — the LLM extracts title, URL, and a one-sentence description for each posting.
4. **Filters** results against configurable include/exclude keyword lists.
5. **Deduplicates** against a SQLite database (`jobs.db`) to avoid re-notifying about the same listing.
6. **Sends** a single aggregated message to a **Telegram bot** for every new match found.
7. **Persists** the updated `jobs.db` back to the repository via an automated `git commit` at the end of each CI run.

---

## 📬 Telegram Integration

Telegram delivery is handled through the [Telegram Bot API](https://core.telegram.org/bots/api) using a simple HTTP `GET` request to the `sendMessage` endpoint:

```
GET https://api.telegram.org/bot{TOKEN}/sendMessage?chat_id={CHAT_ID}&text={message}
```

Each notification message is a single aggregated text block containing all new matched jobs for that run, formatted as:

```
💼 [Intel] Linux Kernel Engineer
🔗 https://intel.wd1.myworkdayjobs.com/...
📝 Develops low-level kernel drivers for Intel storage products.

💼 [DocuSign] Backend Engineer
🔗 https://careers.docusign.com/...
📝 Builds scalable backend services for the DocuSign platform.
```

### Setting Up Your Telegram Bot

1. Message [@BotFather](https://t.me/BotFather) on Telegram and create a new bot — copy the **bot token**.
2. Start a chat with your bot (or add it to a group), then retrieve your **chat ID** via:
   ```
   https://api.telegram.org/bot{TOKEN}/getUpdates
   ```
3. Add both values as GitHub Actions secrets (see [Configuration](#-configuration)).

---

## 🔧 Configuration

### `appsettings.json`

Defines the job sources to scrape and the keyword filters to apply:

```json
{
  "JobSources": [
    { "Name": "Intel",    "Url": "https://intel.wd1.myworkdayjobs.com/..." },
    { "Name": "DocuSign", "Url": "https://careers.docusign.com/..." }
  ],
  "Filters": {
    "IncludeKeywords": ["Engineer", "Backend", "Infrastructure", "DevOps", "C#"],
    "ExcludeKeywords": ["Senior", "Lead", "Manager"]
  }
}
```

### GitHub Actions Secrets

| Secret | Description |
|---|---|
| `TELEGRAM_BOT_TOKEN` | Your Telegram bot token from BotFather |
| `TELEGRAM_CHAT_ID` | The Telegram chat or user ID to send notifications to |
| `LLM_TOKEN` | A GitHub personal access token with access to [GitHub Models](https://github.com/marketplace/models) |

---

## 🗄️ Deduplication

A local SQLite database (`jobs.db`) tracks every URL that has already been notified. On each run, any job whose URL exists in the database is silently skipped. After the run, the updated database is committed back to the repository by the CI workflow, ensuring state is preserved across scheduled runs without any external storage dependency.

---

## 🚀 GitHub Actions Workflow

The bot runs automatically via `.github/workflows/bot-telegram.yml`:

- **Daily** at 8:00 AM PST (UTC 15:00) via `cron` schedule
- **On every push** to `main`
- **Manually** via `workflow_dispatch`

```
Build → Install Playwright → Run Bot → Commit jobs.db
```

---

## 🛠️ Tech Stack

| Component | Technology |
|---|---|
| Language & Runtime | C# / .NET 10 |
| Web Scraping | Microsoft Playwright (headless Chromium) |
| LLM Parsing | GitHub Models — `gpt-4o-mini` via Azure AI Inference SDK |
| Notifications | Telegram Bot API |
| Deduplication | SQLite |
| CI/CD | GitHub Actions |

---

## 📁 Project Structure

```
📄 Program.cs               # Main entrypoint — orchestration, scraping, LLM parsing, Telegram send
📂 Models/
│   📄 JobConfig.cs         # Configuration models (JobSource, JobFilters)
📂 Data/
│   📄 JobRepository.cs     # SQLite-backed deduplication store
📄 appsettings.json         # Job sources and keyword filters
📄 jobs.db                  # Auto-managed SQLite database (committed by CI)
📂 .github/workflows/
    📄 bot-telegram.yml     # Scheduled GitHub Actions workflow
```
