using System;
using System.Collections.Generic;

namespace SimCore.Planning.Llm
{
    public sealed class LlmRequest
    {
        public string SystemPrompt;
        public string UserMessage;
        public string Model;
        public double Temperature;
        public int MaxTokens;
        public int TimeoutSeconds;
        public int TripNumber;
        public int Attempt;
    }

    public sealed class LlmResponse
    {
        public string Text;
        public int InputTokens;
        public int OutputTokens;
        public string Model;
        public string Error;
        public double LatencySeconds;
        public bool IsError => Error != null;

        public static LlmResponse Ok(string text, int inTok, int outTok, string model, double seconds)
            => new() { Text = text, InputTokens = inTok, OutputTokens = outTok, Model = model, LatencySeconds = seconds };
        public static LlmResponse Fail(string error, double seconds = 0)
            => new() { Error = error, LatencySeconds = seconds };
    }

    /// <summary>D18: a provider turns (system, user) into text. It knows nothing about drones, plans, or tools.</summary>
    public interface ILlmProvider
    {
        string Name { get; }
        LlmResponse Complete(LlmRequest request);
    }

    /// <summary>Replays canned model outputs (fences, prose and all) so the full LLM path runs without a network.</summary>
    public sealed class ScriptedProvider : ILlmProvider
    {
        public string Name => "mock";
        private readonly Queue<(string text, string error)> _script = new();
        public int Calls { get; private set; }

        public ScriptedProvider Then(string text) { _script.Enqueue((text, null)); return this; }
        public ScriptedProvider ThenError(string error) { _script.Enqueue((null, error)); return this; }

        public LlmResponse Complete(LlmRequest request)
        {
            Calls++;
            if (_script.Count == 0) return LlmResponse.Fail("SCRIPT_EXHAUSTED");
            var (text, error) = _script.Dequeue();
            if (error != null) return LlmResponse.Fail(error);
            return LlmResponse.Ok(text, TokenEstimate.Of(request.SystemPrompt) + TokenEstimate.Of(request.UserMessage),
                                  TokenEstimate.Of(text), request.Model, 0.0);
        }
    }

    /// <summary>Keys come from environment variables only. Nothing key-like is ever written to a file in the repo.</summary>
    public static class LlmKeys
    {
        public static string EnvVarFor(string provider) => provider?.ToLowerInvariant() switch
        {
            "claude" => "ANTHROPIC_API_KEY",
            "openai" => "OPENAI_API_KEY",
            "gemini" => "GEMINI_API_KEY",
            _ => null
        };

        public static string Get(string provider)
        {
            var name = EnvVarFor(provider);
            return name == null ? null : Environment.GetEnvironmentVariable(name);
        }
    }
}