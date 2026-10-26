using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace GeminiLauncher
{
    public static class Http
    {
        public static readonly HttpClient Client;
        static Http()
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            ServicePointManager.DefaultConnectionLimit = 8;
            var h = new HttpClientHandler { AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate };
            Client = new HttpClient(h) { Timeout = TimeSpan.FromSeconds(90) };
            Client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) GeminiLauncher/1.0");
        }

        /// <summary>Opens TLS connections ahead of time so the first keystroke is fast.</summary>
        public static void Warmup()
        {
            Task.Run(async () =>
            {
                try { using (await Client.SendAsync(new HttpRequestMessage(HttpMethod.Head, "https://suggestqueries.google.com/"))) { } } catch { }
                try { using (await Client.SendAsync(new HttpRequestMessage(HttpMethod.Head, "https://generativelanguage.googleapis.com/"))) { } } catch { }
            });
        }
    }

    /// <summary>Google's public autocomplete endpoint (the same one browsers use).</summary>
    public static class GoogleSuggest
    {
        static readonly Dictionary<string, List<string>> Cache = new Dictionary<string, List<string>>();

        public static async Task<List<string>> GetAsync(string q, CancellationToken ct)
        {
            q = q.Trim();
            if (q.Length == 0) return new List<string>();
            lock (Cache) if (Cache.TryGetValue(q, out var c)) return c;
            var hl = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
            var url = "https://suggestqueries.google.com/complete/search?client=firefox&ie=utf-8&oe=utf-8&hl=" + hl + "&q=" + Uri.EscapeDataString(q);
            using (var resp = await Http.Client.GetAsync(url, ct).ConfigureAwait(false))
            {
                var bytes = await resp.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
                var text = Encoding.UTF8.GetString(bytes);
                var list = Json.Parse(text).At(1).Arr().OfType<string>().ToList();
                lock (Cache) { if (Cache.Count > 300) Cache.Clear(); Cache[q] = list; }
                return list;
            }
        }
    }

    /// <summary>Google Trends daily trending searches RSS (no key needed).</summary>
    public static class Trends
    {
        static List<string> _cache;
        static DateTime _at;
        static string _geo;

        public static async Task<List<string>> GetAsync(string geo)
        {
            if (_cache != null && _geo == geo && (DateTime.Now - _at).TotalMinutes < 60) return _cache;
            try
            {
                var xml = await Http.Client.GetStringAsync("https://trends.google.com/trending/rss?geo=" + Uri.EscapeDataString(geo)).ConfigureAwait(false);
                var doc = XDocument.Parse(xml);
                _cache = doc.Descendants("item").Select(i => (string)i.Element("title")).Where(t => !string.IsNullOrWhiteSpace(t)).Take(10).ToList();
                _at = DateTime.Now; _geo = geo;
            }
            catch { if (_cache == null) _cache = new List<string>(); }
            return _cache;
        }
    }

    public class GeminiSource { public string Title, Uri; }

    public class GeminiAnswer
    {
        public string Query;
        public StringBuilder Text = new StringBuilder();
        public List<GeminiSource> Sources = new List<GeminiSource>();
        public List<string> SearchQueries = new List<string>();
        public List<string> UrlsRead = new List<string>();
        public volatile bool Done;
        public string Snapshot() { lock (this) return Text.ToString(); }
        public string Error;
        public event Action Changed;
        public void RaiseChanged() => Changed?.Invoke();
    }

    /// <summary>
    /// Gemini API client: streamGenerateContent (SSE) with the Google Search grounding tool and
    /// the URL context tool enabled together.
    /// </summary>
    public static class Gemini
    {
        static readonly Dictionary<string, GeminiAnswer> Cache = new Dictionary<string, GeminiAnswer>(StringComparer.OrdinalIgnoreCase);

        public static GeminiAnswer Cached(string model, string q)
        {
            lock (Cache) return Cache.TryGetValue(model + "|" + q.Trim(), out var a) && a.Error == null ? a : null;
        }

        static string SystemPrompt()
        {
            var now = DateTime.Now;
            string region = "";
            try { region = RegionInfo.CurrentRegion.EnglishName; } catch { }
            return
                "You are the instant-answer engine inside a Windows search launcher (like the answer box at the top of Google results).\n" +
                "Current local date/time: " + now.ToString("dddd, MMMM d, yyyy h:mm tt", CultureInfo.InvariantCulture) +
                " (" + TimeZoneInfo.Local.DisplayName + "). User region: " + region + ".\n" +
                "Rules:\n" +
                "- Use Google Search for anything that could be time-sensitive or factual. If the query contains URLs, read them with the URL context tool.\n" +
                "- Start with the direct answer in one or two sentences, with the key fact in **bold**.\n" +
                "- Then, only if useful, up to 5 short bullet points. No preamble, no closing remarks, no follow-up offers.\n" +
                "- If the query is just a website or brand name, give its official URL and a one-line description.\n" +
                "- If the query is a URL, summarize that page.\n" +
                "- Keep it under ~150 words unless the user explicitly asks for detail. Use simple Markdown (bold, bullets, links).\n" +
                "- Reply in the same language as the query.";
        }

        static object ThinkingConfig(string model)
        {
            var m = model.ToLowerInvariant();
            if (m.StartsWith("gemini-2.5"))
                return m.Contains("pro") ? null : new Dictionary<string, object> { ["thinkingBudget"] = 0 };
            if (m.Contains("flash-lite")) return new Dictionary<string, object> { ["thinkingLevel"] = "minimal" };
            return new Dictionary<string, object> { ["thinkingLevel"] = "low" };
        }

        public static GeminiAnswer Ask(string apiKey, string model, string query, CancellationToken ct)
        {
            var key = model + "|" + query.Trim();
            lock (Cache)
            {
                if (Cache.TryGetValue(key, out var existing) && existing.Error == null) return existing;
            }
            var ans = new GeminiAnswer { Query = query.Trim() };
            lock (Cache) { if (Cache.Count > 100) Cache.Clear(); Cache[key] = ans; }
            Task.Run(() => RunAsync(apiKey, model, ans, ct, true));
            return ans;
        }

        static async Task RunAsync(string apiKey, string model, GeminiAnswer ans, CancellationToken ct, bool withThinkingCfg)
        {
            try
            {
                var gen = new Dictionary<string, object> { ["temperature"] = 0.4, ["maxOutputTokens"] = 1500 };
                var tc = withThinkingCfg ? ThinkingConfig(model) : null;
                if (tc != null) gen["thinkingConfig"] = tc;

                var body = new Dictionary<string, object>
                {
                    ["systemInstruction"] = new Dictionary<string, object> { ["parts"] = new List<object> { new Dictionary<string, object> { ["text"] = SystemPrompt() } } },
                    ["contents"] = new List<object>
                    {
                        new Dictionary<string, object>
                        {
                            ["role"] = "user",
                            ["parts"] = new List<object> { new Dictionary<string, object> { ["text"] = ans.Query } }
                        }
                    },
                    ["tools"] = new List<object>
                    {
                        new Dictionary<string, object> { ["googleSearch"] = new Dictionary<string, object>() },
                        new Dictionary<string, object> { ["urlContext"] = new Dictionary<string, object>() },
                    },
                    ["generationConfig"] = gen,
                };

                var url = "https://generativelanguage.googleapis.com/v1beta/models/" + Uri.EscapeDataString(model) + ":streamGenerateContent?alt=sse";
                var req = new HttpRequestMessage(HttpMethod.Post, url)
                {
                    Content = new StringContent(Json.Write(body), Encoding.UTF8, "application/json")
                };
                req.Headers.Add("x-goog-api-key", apiKey);

                using (var resp = await Http.Client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
                {
                    if (!resp.IsSuccessStatusCode)
                    {
                        var err = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                        string msg = null;
                        try { msg = Json.Parse(err).Get("error").Str("message"); } catch { }
                        if (withThinkingCfg && (int)resp.StatusCode == 400 && (msg ?? err).IndexOf("thinking", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            await RunAsync(apiKey, model, ans, ct, false).ConfigureAwait(false);
                            return;
                        }
                        ans.Error = "Gemini API error " + (int)resp.StatusCode + ": " + (msg ?? err);
                        ans.Done = true; ans.RaiseChanged();
                        return;
                    }

                    using (var stream = await resp.Content.ReadAsStreamAsync().ConfigureAwait(false))
                    using (var reader = new StreamReader(stream, Encoding.UTF8))
                    {
                        string line;
                        var data = new StringBuilder();
                        while ((line = await reader.ReadLineAsync().ConfigureAwait(false)) != null)
                        {
                            if (ct.IsCancellationRequested) break;
                            if (line.StartsWith("data:")) { data.Append(line.Substring(5).TrimStart()); continue; }
                            if (line.Length == 0 && data.Length > 0)
                            {
                                HandleChunk(ans, data.ToString());
                                data.Clear();
                            }
                        }
                        if (data.Length > 0) HandleChunk(ans, data.ToString());
                    }
                }
                if (ans.Text.Length == 0 && ans.Error == null) ans.Error = ct.IsCancellationRequested ? "Cancelled" : "Gemini returned an empty answer.";
            }
            catch (OperationCanceledException) { ans.Error = "Cancelled"; }
            catch (Exception ex) { ans.Error = "Couldn't reach Gemini: " + ex.Message; }
            ans.Done = true;
            ans.RaiseChanged();
        }

        static void HandleChunk(GeminiAnswer ans, string json)
        {
            lock (ans) HandleChunkCore(ans, json);
            ans.RaiseChanged();
        }

        static void HandleChunkCore(GeminiAnswer ans, string json)
        {
            object o;
            try { o = Json.Parse(json); } catch { return; }
            if (o.Get("error") != null) { ans.Error = o.Get("error").Str("message"); return; }
            var cand = o.Get("candidates").At(0);
            if (cand == null) return;
            foreach (var p in cand.Get("content").Get("parts").Arr())
            {
                if (p.Bool("thought")) continue;
                var t = p.Str("text");
                if (!string.IsNullOrEmpty(t)) ans.Text.Append(t);
            }
            var gm = cand.Get("groundingMetadata");
            if (gm != null)
            {
                foreach (var ch in gm.Get("groundingChunks").Arr())
                {
                    var web = ch.Get("web");
                    var uri = web.Str("uri");
                    if (uri == null) continue;
                    var title = web.Str("title") ?? uri;
                    if (!ans.Sources.Any(s => s.Title == title)) ans.Sources.Add(new GeminiSource { Title = title, Uri = uri });
                }
                foreach (var q in gm.Get("webSearchQueries").Arr().OfType<string>())
                    if (!ans.SearchQueries.Contains(q)) ans.SearchQueries.Add(q);
            }
            foreach (var um in cand.Get("urlContextMetadata").Get("urlMetadata").Arr())
            {
                var u = um.Str("retrievedUrl");
                if (u != null && !ans.UrlsRead.Contains(u)) ans.UrlsRead.Add(u);
            }
        }
    }
}
