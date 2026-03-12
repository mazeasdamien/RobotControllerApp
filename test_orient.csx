using System;
using System.Net.Http;
using System.Text.Json;
using System.Text;
using System.IO;
using System.Threading.Tasks;

var httpClient = new HttpClient();
string imgPath = @"C:\Users\QYTH4815\AppData\Local\RobotControllerApp\Library\BLACK_REMOTE_CONTROL_banana.png";
if (!File.Exists(imgPath)) { Console.WriteLine("Image not found"); return; }
byte[] imageBytes = File.ReadAllBytes(imgPath);
string dataUrl = "data:image/png;base64," + Convert.ToBase64String(imageBytes);

var imgObj = new { url = dataUrl, meta = new { _type = "gradio.FileData" } };
var payload = new { data = new object[] { imgObj, null, true } };

using var triggerReq = new HttpRequestMessage(HttpMethod.Post, "https://viglong-orient-anything-v2.hf.space/gradio_api/call/run_inference")
{
    Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
};

using var triggerRes = await httpClient.SendAsync(triggerReq);
var triggerDoc = JsonDocument.Parse(await triggerRes.Content.ReadAsStringAsync());
string eventId = triggerDoc.RootElement.GetProperty("event_id").GetString();
Console.WriteLine($"Event ID: {eventId}");

using var sseReq = new HttpRequestMessage(HttpMethod.Get, $"https://viglong-orient-anything-v2.hf.space/gradio_api/call/run_inference/{eventId}");
sseReq.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("text/event-stream"));
using var sseRes = await httpClient.SendAsync(sseReq, HttpCompletionOption.ResponseHeadersRead);

using var stream = await sseRes.Content.ReadAsStreamAsync();
using var reader = new StreamReader(stream);
string line;
string dataLine = null;

while ((line = await reader.ReadLineAsync()) != null)
{
    if (line.StartsWith("event: complete")) { dataLine = await reader.ReadLineAsync(); break; }
}

Console.WriteLine("Data Line:");
Console.WriteLine(dataLine);
