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

    public class GeminiOptions
    {
        public string Model = "gemini-3.5-flash-lite";
        public string Thinking = "medium";   // default | minimal | low | medium | high
        public bool Search = true, Maps = true, UrlContext = true;
        public bool SafetyOff = true, IncludeJailbreak = true;
        public double? Lat, Lng;
        public string Key => Model + "|" + Thinking + "|" + Search + Maps + UrlContext + SafetyOff;
        public GeminiOptions Clone() => (GeminiOptions)MemberwiseClone();
    }

    public class GeminiAnswer
    {
        public string Query;
        public StringBuilder Text = new StringBuilder();
        public List<GeminiSource> Sources = new List<GeminiSource>();
        public List<string> SearchQueries = new List<string>();
        public List<string> UrlsRead = new List<string>();
        public List<GeminiSource> Places = new List<GeminiSource>();
        public List<string> Notes = new List<string>();
        public GeminiOptions Used;
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

        public static GeminiAnswer Cached(GeminiOptions opt, string q)
        {
            lock (Cache) return Cache.TryGetValue(opt.Key + "|" + q.Trim(), out var a) && a.Error == null ? a : null;
        }

        static string SystemPrompt(GeminiOptions o)
        {
            var now = DateTime.Now;
            string region = "";
            try { region = RegionInfo.CurrentRegion.EnglishName; } catch { }
            return
                "You are the instant-answer engine inside a Windows search launcher (like the answer box at the top of Google results).\n" +
                "Current local date/time: " + now.ToString("dddd, MMMM d, yyyy h:mm tt", CultureInfo.InvariantCulture) +
                " (" + TimeZoneInfo.Local.DisplayName + "). User region: " + region + ".\n" +
                "Rules:\n" +
                (o.Search ? "- Use Google Search for anything that could be time-sensitive or factual.\n" : "") +
                (o.UrlContext ? "- If the query contains URLs, read them with the URL context tool.\n" : "") +
                (o.Maps ? "- For places, businesses, directions, opening hours or anything \"near me\", use Google Maps." +
                          (o.Lat.HasValue ? " The user's approximate location is " + o.Lat.Value.ToString("0.###", CultureInfo.InvariantCulture) + ", " + o.Lng.Value.ToString("0.###", CultureInfo.InvariantCulture) + "." : "") + "\n" : "") +
                "- Start with the direct answer in one or two sentences, with the key fact in **bold**.\n" +
                "- Then, only if useful, up to 5 short bullet points. No preamble, no closing remarks, no follow-up offers.\n" +
                "- If the query is just a website or brand name, give its official URL and a one-line description.\n" +
                "- If the query is a URL, summarize that page.\n" +
                "- Keep it under ~150 words unless the user explicitly asks for detail. Use simple Markdown (bold, bullets, links).\n" +
                "- Reply in the same language as the query.";
        }

        static object ThinkingConfig(string model, string level)
        {
            var m = model.ToLowerInvariant();
            level = (level ?? "medium").ToLowerInvariant();
            if (level == "default") return null;
            if (m.StartsWith("gemini-2."))
            {
                // Gemini 2.5 uses token budgets instead of levels
                int budget = level == "minimal" ? 0 : level == "low" ? 1024 : level == "medium" ? 4096 : 16384;
                if (m.Contains("pro") && budget < 128) budget = 128;
                return new Dictionary<string, object> { ["thinkingBudget"] = budget };
            }
            return new Dictionary<string, object> { ["thinkingLevel"] = level.ToUpperInvariant() };
        }

        static readonly string[] SafetyCategories =
        {
            "HARM_CATEGORY_HARASSMENT", "HARM_CATEGORY_HATE_SPEECH", "HARM_CATEGORY_SEXUALLY_EXPLICIT",
            "HARM_CATEGORY_DANGEROUS_CONTENT", "HARM_CATEGORY_JAILBREAK"
        };

        public static GeminiAnswer Ask(string apiKey, GeminiOptions opt, string query, CancellationToken ct)
        {
            var key = opt.Key + "|" + query.Trim();
            lock (Cache)
            {
                if (Cache.TryGetValue(key, out var existing) && existing.Error == null) return existing;
            }
            var ans = new GeminiAnswer { Query = query.Trim() };
            lock (Cache) { if (Cache.Count > 100) Cache.Clear(); Cache[key] = ans; }
            Task.Run(() => RunAsync(apiKey, opt.Clone(), ans, ct, 0));
            return ans;
        }

        /// <summary>Some models reject some combinations (e.g. a tool or a safety category).
        /// On HTTP 400 we drop the most likely culprit and retry, instead of failing.</summary>
        static bool Degrade(GeminiOptions o, string msg)
        {
            var m = (msg ?? "").ToLowerInvariant();
            if (o.Thinking != "default" && m.Contains("thinking")) { o.Thinking = o.Thinking == "minimal" ? "low" : "default"; return true; }
            if (o.SafetyOff && o.IncludeJailbreak && (m.Contains("jailbreak") || m.Contains("category") || m.Contains("safety") || m.Contains("threshold"))) { o.IncludeJailbreak = false; return true; }
            if (o.SafetyOff && (m.Contains("category") || m.Contains("safety") || m.Contains("threshold"))) { o.SafetyOff = false; return true; }
            if (o.Maps && (m.Contains("map") || m.Contains("latlng") || m.Contains("retrieval"))) { o.Maps = false; return true; }
            if (o.UrlContext && m.Contains("url")) { o.UrlContext = false; return true; }
            if (m.Contains("tool") || m.Contains("not supported") || m.Contains("unsupported") || m.Contains("combin"))
            {
                if (o.Maps) { o.Maps = false; return true; }
                if (o.UrlContext && o.Search) { o.UrlContext = false; return true; }
            }
            return false;
        }

        static async Task RunAsync(string apiKey, GeminiOptions o, GeminiAnswer ans, CancellationToken ct, int attempt)
        {
            try
            {
                var gen = new Dictionary<string, object> { ["temperature"] = 0.4, ["maxOutputTokens"] = 8192 };
                var tc = ThinkingConfig(o.Model, o.Thinking);
                if (tc != null) gen["thinkingConfig"] = tc;

                var tools = new List<object>();
                if (o.Search) tools.Add(new Dictionary<string, object> { ["googleSearch"] = new Dictionary<string, object>() });
                if (o.Maps) tools.Add(new Dictionary<string, object> { ["googleMaps"] = new Dictionary<string, object>() });
                if (o.UrlContext) tools.Add(new Dictionary<string, object> { ["urlContext"] = new Dictionary<string, object>() });

                var body = new Dictionary<string, object>
                {
                    ["systemInstruction"] = new Dictionary<string, object> { ["parts"] = new List<object> { new Dictionary<string, object> { ["text"] = SystemPrompt(o) } } },
                    ["contents"] = new List<object>
                    {
                        new Dictionary<string, object>
                        {
                            ["role"] = "user",
                            ["parts"] = new List<object> { new Dictionary<string, object> { ["text"] = ans.Query } }
                        }
                    },
                    ["generationConfig"] = gen,
                };
                if (tools.Count > 0) body["tools"] = tools;
                if (o.SafetyOff)
                    body["safetySettings"] = SafetyCategories.Where(c => o.IncludeJailbreak || c != "HARM_CATEGORY_JAILBREAK")
                        .Select(c => (object)new Dictionary<string, object> { ["category"] = c, ["threshold"] = "OFF" }).ToList();
                if (o.Maps)
                {
                    var rc = new Dictionary<string, object> { ["languageCode"] = CultureInfo.CurrentUICulture.Name };
                    if (o.Lat.HasValue && o.Lng.HasValue)
                        rc["latLng"] = new Dictionary<string, object> { ["latitude"] = o.Lat.Value, ["longitude"] = o.Lng.Value };
                    body["toolConfig"] = new Dictionary<string, object> { ["retrievalConfig"] = rc };
                }

                var url = "https://generativelanguage.googleapis.com/v1beta/models/" + Uri.EscapeDataString(o.Model) + ":streamGenerateContent?alt=sse";
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
                        if ((int)resp.StatusCode == 400 && attempt < 6 && Degrade(o, msg ?? err))
                        {
                            lock (ans) ans.Notes.Add(msg ?? err);
                            await RunAsync(apiKey, o, ans, ct, attempt + 1).ConfigureAwait(false);
                            return;
                        }
                        ans.Error = "Gemini API error " + (int)resp.StatusCode + ": " + (msg ?? err);
                        ans.Done = true; ans.RaiseChanged();
                        return;
                    }
                    lock (ans) ans.Used = o;

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
                    var map = ch.Get("maps");
                    if (map != null)
                    {
                        var mu = map.Str("uri");
                        var mt = map.Str("title") ?? "Place";
                        if (mu != null && !ans.Places.Any(x => x.Uri == mu)) ans.Places.Add(new GeminiSource { Title = mt, Uri = mu });
                        continue;
                    }
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
