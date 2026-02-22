using System;
using System.Net.Http;
using System.Threading.Tasks;

class Program
{
    static async Task Main()
    {
        using var client = new HttpClient();

        using var response = await client.GetAsync(
            "https://speed.cloudflare.com/__down?bytes=1000000",
            HttpCompletionOption.ResponseHeadersRead);

        Console.WriteLine($"Status: {response.StatusCode}");

        using var stream = await response.Content.ReadAsStreamAsync();
        byte[] buf = new byte[65536];
        long totalBytes = 0;
        int read;
        while ((read = await stream.ReadAsync(buf)) > 0)
            totalBytes += read;

        Console.WriteLine($"Bytes: {totalBytes}");
    }
}
