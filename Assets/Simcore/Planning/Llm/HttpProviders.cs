using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net.Http;
using System.Text;
using System.Threading;
using Newtonsoft.Json.Linq;
using SimCore.Config;

namespace SimCore.Planning.Llm
{
    /// <summary>Synchronous JSON POST. Synchronous is deliberate: the Thinking Phase is paused sim time, and headless runs are sequential.</summary>
    internal static class HttpJson
    {
        private static readonly HttpClient Client = new();

        public static (string body, string error, double seconds) Post(string url, IDictionary<string, string> headers, string json, int timeoutSeconds)
        {
            var sw = Stopwatch.StartNew();
            try
            {
                using var msg = new HttpRequestMessage(HttpMethod.Post, url);
                foreach (var kv in headers) msg.Headers.TryAddWithoutValidation(kv.Key, kv.Value);
                msg.Content = new StringContent(json, Encoding.UTF8, "application/json");
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Max(5, timeoutSeconds)));
                using var response = Client.SendAsync(msg, cts.Token).GetAwaiter().GetResult();
                string body = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                sw.Stop();
                if (!response.IsSuccessStatusCode)
                    return (body, $"HTTP {(int)response.StatusCode}: {Truncate(body)}", sw.Elapsed.TotalSeconds);
                return (body, null, sw.Elapsed.TotalSeconds);
            }
            catch (Exception ex)
            {
                sw.Stop();
                return (null, $"{ex.GetType().Name}: {ex.Message}", sw.Elapsed.TotalSeconds);
            }
        }

        private static string Truncate(string s) => string.IsNullOrEmpty(s) ? "" : (s.Length > 300 ? s.Substring(0, 300) + "…" : s);
    }

    public sealed class ClaudeProvider : ILlmProvider
    {
        public string Name => "claude";
        private readonly string _key;
        public ClaudeProvider(string apiKey = null) { _key = apiKey ?? LlmKeys.Get(Name); }

        public LlmResponse Complete(LlmRequest r)
        {
            if (string.IsNullOrEmpty(_key)) return LlmResponse.Fail($"missing API key: set {LlmKeys.EnvVarFor(Name)}");
            var body = new JObject
            {
                ["model"] = r.Model,
                ["max_tokens"] = r.MaxTokens,
                ["temperature"] = r.Temperature,
                ["system"] = r.SystemPrompt,
                ["messages"] = new JArray { new JObject { ["role"] = "user", ["content"] = r.UserMessage } }
            };
            var (resp, err, secs) = HttpJson.Post("https://api.anthropic.com/v1/messages",
                new Dictionary<string, string> { ["x-api-key"] = _key, ["anthropic-version"] = "2023-06-01" },
                body.ToString(), r.TimeoutSeconds);
            if (err != null) return LlmResponse.Fail(err, secs);
            try
            {
                var j = JObject.Parse(resp);
                var sb = new StringBuilder();
                foreach (var block in (JArray)j["content"]) if ((string)block["type"] == "text") sb.Append((string)block["text"]);
                return LlmResponse.Ok(sb.ToString(), (int?)j["usage"]?["input_tokens"] ?? 0, (int?)j["usage"]?["output_tokens"] ?? 0,
                                      (string)j["model"] ?? r.Model, secs);
            }
            catch (Exception ex) { return LlmResponse.Fail($"claude response parse: {ex.Message}", secs); }
        }
    }

    public sealed class OpenAiProvider : ILlmProvider
    {
        public string Name => "openai";
        private readonly string _key;
        public OpenAiProvider(string apiKey = null) { _key = apiKey ?? LlmKeys.Get(Name); }

        public LlmResponse Complete(LlmRequest r)
        {
            if (string.IsNullOrEmpty(_key)) return LlmResponse.Fail($"missing API key: set {LlmKeys.EnvVarFor(Name)}");
            var body = new JObject
            {
                ["model"] = r.Model,
                ["temperature"] = r.Temperature,
                ["max_tokens"] = r.MaxTokens,
                ["messages"] = new JArray
                {
                    new JObject { ["role"] = "system", ["content"] = r.SystemPrompt },
                    new JObject { ["role"] = "user", ["content"] = r.UserMessage }
                }
            };
            var (resp, err, secs) = HttpJson.Post("https://api.openai.com/v1/chat/completions",
                new Dictionary<string, string> { ["Authorization"] = "Bearer " + _key }, body.ToString(), r.TimeoutSeconds);
            if (err != null) return LlmResponse.Fail(err, secs);
            try
            {
                var j = JObject.Parse(resp);
                string text = (string)j["choices"]?[0]?["message"]?["content"] ?? "";
                return LlmResponse.Ok(text, (int?)j["usage"]?["prompt_tokens"] ?? 0, (int?)j["usage"]?["completion_tokens"] ?? 0,
                                      (string)j["model"] ?? r.Model, secs);
            }
            catch (Exception ex) { return LlmResponse.Fail($"openai response parse: {ex.Message}", secs); }
        }
    }

    public sealed class GeminiProvider : ILlmProvider
    {
        public string Name => "gemini";
        private readonly string _key;
        public GeminiProvider(string apiKey = null) { _key = apiKey ?? LlmKeys.Get(Name); }

        public LlmResponse Complete(LlmRequest r)
        {
            if (string.IsNullOrEmpty(_key)) return LlmResponse.Fail($"missing API key: set {LlmKeys.EnvVarFor(Name)}");
            var body = new JObject
            {
                ["system_instruction"] = new JObject { ["parts"] = new JArray { new JObject { ["text"] = r.SystemPrompt } } },
                ["contents"] = new JArray { new JObject { ["role"] = "user", ["parts"] = new JArray { new JObject { ["text"] = r.UserMessage } } } },
                ["generationConfig"] = new JObject { ["temperature"] = r.Temperature, ["maxOutputTokens"] = r.MaxTokens }
            };
            string url = $"https://generativelanguage.googleapis.com/v1beta/models/{r.Model}:generateContent";
            var (resp, err, secs) = HttpJson.Post(url, new Dictionary<string, string> { ["x-goog-api-key"] = _key }, body.ToString(), r.TimeoutSeconds);
            if (err != null) return LlmResponse.Fail(err, secs);
            try
            {
                var j = JObject.Parse(resp);
                var sb = new StringBuilder();
                var parts = j["candidates"]?[0]?["content"]?["parts"] as JArray;
                if (parts != null) foreach (var p in parts) sb.Append((string)p["text"]);
                return LlmResponse.Ok(sb.ToString(), (int?)j["usageMetadata"]?["promptTokenCount"] ?? 0,
                                      (int?)j["usageMetadata"]?["candidatesTokenCount"] ?? 0, r.Model, secs);
            }
            catch (Exception ex) { return LlmResponse.Fail($"gemini response parse: {ex.Message}", secs); }
        }
    }

    public static class LlmProviders
    {
        public static ILlmProvider Create(LlmPlannerConfig cfg, string apiKeyOverride = null) => cfg.Provider?.ToLowerInvariant() switch
        {
            "claude" => new ClaudeProvider(apiKeyOverride),
            "openai" => new OpenAiProvider(apiKeyOverride),
            "gemini" => new GeminiProvider(apiKeyOverride),
            "mock"   => throw new ArgumentException("provider 'mock': construct a ScriptedProvider directly."),
            _ => throw new ArgumentException($"unknown provider '{cfg.Provider}'")
        };
    }
}