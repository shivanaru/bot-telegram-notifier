using System;
using System.Net.Http;
using System.Threading.Tasks;

class Program
{
    static async Task Main()
    {
        var token = Environment.GetEnvironmentVariable("TELEGRAM_BOT_TOKEN");
        var chatId = Environment.GetEnvironmentVariable("TELEGRAM_CHAT_ID");

        if (string.IsNullOrEmpty(token) || string.IsNullOrEmpty(chatId))
        {
            Console.WriteLine("Missing environment variables.");
            return;
        }

        var message = "✅ GitHub Actions Bot is running!";

        using var client = new HttpClient();

        var url = $"https://api.telegram.org/bot{token}/sendMessage" +
                  $"?chat_id={chatId}&text={Uri.EscapeDataString(message)}";

        var response = await client.GetAsync(url);
        Console.WriteLine(await response.Content.ReadAsStringAsync());
    }
}