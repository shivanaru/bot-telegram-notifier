using Microsoft.Extensions.Configuration;
using Azure;
using Azure.AI.Inference;
using Microsoft.Playwright;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;

class Program
{
    static async Task Main()
    {
        var config = new ConfigurationBuilder()
            .SetBasePath(Directory.GetCurrentDirectory())
            .AddJsonFile("appsettings.json")
            .AddUserSecrets<Program>(optional: true)   // used locally; ignored in CI
            .AddEnvironmentVariables()                 // picks up GitHub Actions secrets in CI
            .Build();

        var jobConfig = config.Get<JobConfig>();

        Console.WriteLine("Loaded job sources:");

        var telegramToken = config["TELEGRAM_BOT_TOKEN"]
            ?? throw new InvalidOperationException("TELEGRAM_BOT_TOKEN is not set.");
        var telegramChatId = config["TELEGRAM_CHAT_ID"]
            ?? throw new InvalidOperationException("TELEGRAM_CHAT_ID is not set.");
        var githubToken = config["LLM_TOKEN"]
            ?? throw new InvalidOperationException("LLM_TOKEN is not set.");

        var inferenceClient = new ChatCompletionsClient(
            new Uri("https://models.inference.ai.azure.com"),
            new AzureKeyCredential(githubToken));

        using var httpClient = new HttpClient();
        httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0");

        var dbPath = Path.Combine(Directory.GetCurrentDirectory(), "jobs.db");
        using var jobRepo = new JobRepository(dbPath);
        Console.WriteLine($"Using job database: {dbPath}");

        var allMatches = new List<(string Source, JobListing Job)>();

        foreach (var source in jobConfig.JobSources)
        {
            Console.WriteLine($"Fetching jobs from {source.Name}...");

            var jobs = await FetchJobsWithLlmAsync(httpClient, inferenceClient, source.Url);

            foreach (var job in jobs)
            {
                if (MatchesFilters(job.Title, jobConfig.Filters))
                {
                    if (jobRepo.IsAlreadyNotified(job.Url))
                    {
                        Console.WriteLine($"[Skip - already notified] {source.Name}: {job.Title}");
                        continue;
                    }
                    Console.WriteLine($"[Match] {source.Name}: {job.Title}\n  {job.Url}\n  {job.Description}");
                    allMatches.Add((source.Name, job));
                }
            }
        }

        if (allMatches.Count > 0)
        {
            var aggregatedMessage = string.Join("\n\n",
                allMatches.Select(m => $"💼 [{m.Source}] {m.Job.Title}\n🔗 {m.Job.Url}\n📝 {m.Job.Description}"));
            await SendTelegram(aggregatedMessage, telegramToken, telegramChatId);

            foreach (var (source, job) in allMatches)
                jobRepo.MarkAsNotified(job.Url, job.Title, source);

            Console.WriteLine($"Marked {allMatches.Count} job(s) as notified.");
        }
        else
        {
            Console.WriteLine("No matching jobs found for this run.");
        }
    }

    static async Task<List<JobListing>> FetchJobsWithLlmAsync(
        HttpClient httpClient,
        ChatCompletionsClient inferenceClient,
        string pageUrl)
    {
        var jobs = new List<JobListing>();
        try
        {
            var html = await FetchHtmlWithPlaywrightAsync(pageUrl)
                       ?? await httpClient.GetStringAsync(pageUrl);

            // Strip scripts, styles and tag attributes to remove noise before sending to LLM.
            // Raw HTML is ~80% noise (scripts/styles/attributes); cleaning it keeps input tokens
            // well within the GitHub Models free tier limit of 8,000 input tokens per request.
            var cleanedHtml = StripHtmlNoise(html);

            // Pre-extract all candidate job hrefs from the full raw HTML before truncating.
            // This prevents the LLM from hallucinating URLs when the trim cuts off the links.
            var knownUrls = ExtractJobHrefs(html, pageUrl);
            var knownUrlsHint = knownUrls.Count > 0
                ? $"\n\nKnown job URLs extracted directly from the page (use ONLY these for the \"url\" field, do not invent others):\n{string.Join("\n", knownUrls)}"
                : string.Empty;

            // 5,000 chars of cleaned HTML ≈ ~1,250 tokens, leaving headroom for the prompt
            // and output within the free tier (8k input / 1k output tokens per request).
            var trimmedHtml = cleanedHtml.Length > 5000 ? cleanedHtml[..5000] : cleanedHtml;

            var prompt = $$"""
                You are a job listing parser. Given the cleaned HTML from a jobs page, extract all job listings.
                Return a JSON array with this exact structure:
                [{"title": "Job Title", "url": "https://...", "description": "One sentence summary"}]

                Rules:
                - Only include actual job postings, not navigation or unrelated links.
                - Resolve relative URLs (e.g. /jobs/123) against the base URL: {{pageUrl}}
                - Keep description to one short sentence (max 15 words).
                - Return ONLY the JSON array, no markdown, no explanation.
                - If a list of Known job URLs is provided below, you MUST use only those exact URLs.

                HTML:
                {{trimmedHtml}}{{knownUrlsHint}}
                """;

            var response = await inferenceClient.CompleteAsync(new ChatCompletionsOptions
            {
                Model = "gpt-4o-mini",
                Messages = { new ChatRequestUserMessage(prompt) },
                MaxTokens = 1000,   // enough for ~25 job listings; keeps output tokens low
                Temperature = 0f
            });

            var json = response.Value.Content?.Trim();

            if (!string.IsNullOrWhiteSpace(json))
            {
                var parsed = JsonSerializer.Deserialize<List<JobListing>>(json,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                    ?? [];

                var baseUri = new Uri(pageUrl);
                jobs = parsed
                    .Select(j =>
                    {
                        // Resolve relative or malformed URLs against the source page URL
                        var resolvedUrl = j.Url;
                        if (!string.IsNullOrWhiteSpace(j.Url) &&
                            Uri.TryCreate(j.Url, UriKind.RelativeOrAbsolute, out var parsedUri))
                        {
                            if (!parsedUri.IsAbsoluteUri)
                                resolvedUrl = new Uri(baseUri, parsedUri).ToString();
                        }
                        // Workday URLs: the LLM emits /job/{title}/{id} but the correct form
                        // is /job/{title}_{id}  (e.g. Linux-Kernel-Engineer_JR0283350)
                        resolvedUrl = FixWorkdayUrl(resolvedUrl);
                        // If we have pre-extracted known URLs and the LLM returned something
                        // that isn't in that set, fall back to the source page URL so we never
                        // surface a hallucinated link.
                        if (knownUrls.Count > 0 && !knownUrls.Contains(resolvedUrl))
                            resolvedUrl = pageUrl;
                        return j with { Url = resolvedUrl };
                    })
                    .Where(j => Uri.TryCreate(j.Url, UriKind.Absolute, out var u)
                                && (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps))
                    .ToList();
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to fetch or parse {pageUrl}: {ex.Message}");
        }
        return jobs;
    }

    static async Task<string?> FetchHtmlWithPlaywrightAsync(string url)
    {
        try
        {
            using var playwright = await Playwright.CreateAsync();
            await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
            {
                Headless = true
            });
            var context = await browser.NewContextAsync(new BrowserNewContextOptions
            {
                UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36"
            });
            var page = await context.NewPageAsync();
            await page.GotoAsync(url, new PageGotoOptions
            {
                WaitUntil = WaitUntilState.NetworkIdle,
                Timeout = 30_000
            });
            return await page.ContentAsync();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Playwright fetch failed for {url}: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Extracts all absolute job-detail hrefs from the raw HTML before any truncation.
    /// This gives the LLM a ground-truth URL list so it cannot hallucinate job links.
    /// </summary>
    static HashSet<string> ExtractJobHrefs(string html, string pageUrl)
    {
        var baseUri = new Uri(pageUrl);
        var results = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Match href="..." (double or single quotes)
        foreach (Match m in Regex.Matches(html, @"href=[""']([^""'\s>]+)[""']", RegexOptions.IgnoreCase))
        {
            var raw = m.Groups[1].Value;
            if (!Uri.TryCreate(raw, UriKind.RelativeOrAbsolute, out var uri))
                continue;

            // Resolve relative URLs
            if (!uri.IsAbsoluteUri)
                uri = new Uri(baseUri, uri);

            // Only keep links on the same host that look like job detail pages
            // (contain a path segment that is not the root search/listing page itself)
            if (!string.Equals(uri.Host, baseUri.Host, StringComparison.OrdinalIgnoreCase))
                continue;

            var path = uri.AbsolutePath;
            // Skip the source listing page and trivial paths
            if (path == "/" || path == baseUri.AbsolutePath)
                continue;

            results.Add(uri.GetLeftPart(UriPartial.Path).TrimEnd('/') + "/");
        }

        return results;
    }

    static string FixWorkdayUrl(string url)
    {
        // Workday job URLs have the form:
        //   .../job/{title-slug}_{jobId}
        // The LLM sometimes emits a / instead of _ before the job ID segment.
        // Pattern: /job/{slug}/{jobId}  →  /job/{slug}_{jobId}
        // Job IDs on Workday typically look like JR followed by digits (e.g. JR0283350).
        return Regex.Replace(
            url,
            @"(myworkdayjobs\.com/.+?/job/[^/]+)/([A-Z]{1,4}\d+)",
            "$1_$2");
    }

    static string StripHtmlNoise(string html)
    {
        // Remove <script> and <style> blocks entirely — biggest source of token waste
        html = Regex.Replace(html, @"<(script|style)[^>]*>.*?</\1>", "", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        // Remove all HTML attributes except href (needed for job links)
        html = Regex.Replace(html, @"<(\w+)\s+[^>]*?(href=""([^""]*)"")[^>]*>", "<$1 href=\"$3\">");
        // Remove remaining tags that still have attributes (no href)
        html = Regex.Replace(html, @"<(\w+)\s[^>]*>", "<$1>");
        // Collapse whitespace
        html = Regex.Replace(html, @"\s{2,}", " ").Trim();
        return html;
    }

    static bool MatchesFilters(string text, JobFilters filters)
    {
        var lower = text.ToLower();

        if (filters.ExcludeKeywords.Any(k => lower.Contains(k.ToLower())))
            return false;

        if (filters.IncludeKeywords.Any(k => lower.Contains(k.ToLower())))
            return true;

        return false;
    }

    static async Task SendTelegram(string message, string token, string chatId)
    {
        using var client = new HttpClient();

        var url = $"https://api.telegram.org/bot{token}/sendMessage" +
                  $"?chat_id={chatId}&text={Uri.EscapeDataString(message)}";

        await client.GetAsync(url);
    }
}

record JobListing(string Title, string Url, string Description);