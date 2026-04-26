using Microsoft.Extensions.Configuration;
using Azure;
using Azure.AI.Inference;
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

        var githubToken = config["LLM_TOKEN"]
            ?? throw new InvalidOperationException("LLM_TOKEN is not set.");

        var inferenceClient = new ChatCompletionsClient(
            new Uri("https://models.inference.ai.azure.com"),
            new AzureKeyCredential(githubToken));

        using var httpClient = new HttpClient();
        httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0");

        foreach (var source in jobConfig.JobSources)
        {
            Console.WriteLine($"Fetching jobs from {source.Name}...");

            var jobs = await FetchJobsWithLlmAsync(httpClient, inferenceClient, source.Url);

            foreach (var job in jobs)
            {
                if (MatchesFilters(job.Title, jobConfig.Filters))
                {
                    Console.WriteLine($"[Match] {source.Name}: {job.Title}\n  {job.Url}\n  {job.Description}");
                    var message = $"💼 [{source.Name}] {job.Title}\n🔗 {job.Url}\n📝 {job.Description}";
                    await SendTelegram(message);
                }
            }
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
            var html = await httpClient.GetStringAsync(pageUrl);

            // Strip scripts, styles and tag attributes to remove noise before sending to LLM.
            // Raw HTML is ~80% noise (scripts/styles/attributes); cleaning it keeps input tokens
            // well within the GitHub Models free tier limit of 8,000 input tokens per request.
            var cleanedHtml = StripHtmlNoise(html);

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

                HTML:
                {{trimmedHtml}}
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
                jobs = JsonSerializer.Deserialize<List<JobListing>>(json,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                    ?? [];
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to fetch or parse {pageUrl}: {ex.Message}");
        }
        return jobs;
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

    static async Task SendTelegram(string message)
    {
        var token = Environment.GetEnvironmentVariable("TELEGRAM_BOT_TOKEN");
        var chatId = Environment.GetEnvironmentVariable("TELEGRAM_CHAT_ID");

        using var client = new HttpClient();

        var url = $"https://api.telegram.org/bot{token}/sendMessage" +
                  $"?chat_id={chatId}&text={Uri.EscapeDataString(message)}";

        await client.GetAsync(url);
    }
}

record JobListing(string Title, string Url, string Description);