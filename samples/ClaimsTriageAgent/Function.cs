// ═══════════════════════════════════════════════════════════════════════════════
// ClaimPilot — FNOL claims-triage agent on AWS Lambda (Jacquard.NET, NativeAOT)
// ═══════════════════════════════════════════════════════════════════════════════
//
// Lambda entry point. Published as a NativeAOT binary for the provided.al2023
// custom runtime: no JIT, no reflection, ~90ms cold starts on arm64 Graviton2 —
// which is what lets this agent absorb catastrophe-event claim surges
// (a hailstorm can produce thousands of FNOLs in an hour) on scale-to-zero
// economics, with no provisioned concurrency.
//
// Two ways to run:
//   AWS Lambda:  payload ClaimEvent JSON  ->  TriageResult JSON
//   Local CLI:   dotnet run -- --local events/claim-fasttrack.json
//
// Model provider selection (set MODEL_PROVIDER environment variable):
//   bedrock   — Amazon Bedrock (default, uses AWS credentials)
//   anthropic — Anthropic direct API (requires ANTHROPIC_API_KEY)
//   openai    — OpenAI or compatible endpoint (requires OPENAI_API_KEY)
//   gemini    — Google Gemini (requires GEMINI_API_KEY)
// ═══════════════════════════════════════════════════════════════════════════════

using System.Text;
using System.Text.Json;
using Amazon.Lambda.Core;
using Amazon.Lambda.RuntimeSupport;
using Amazon.Lambda.Serialization.SystemTextJson;
using ClaimsTriageAgent;
using ClaimsTriageAgent.Models;

// ── Local CLI mode ────────────────────────────────────────────────────────────
if (args.Length >= 2 && args[0] == "--local")
{
    var payload = await File.ReadAllTextAsync(args[1]);
    var claim = JsonSerializer.Deserialize(payload, ClaimsJsonContext.Default.ClaimEvent)
        ?? throw new InvalidOperationException($"Could not parse a ClaimEvent from '{args[1]}'.");

    var result = await TriageHandler.HandleAsync(claim, Console.WriteLine);

    Console.WriteLine();
    Console.WriteLine(PrettyPrint(
        JsonSerializer.Serialize(result, ClaimsJsonContext.Default.TriageResult)));
    return;
}

// ── AWS Lambda mode ───────────────────────────────────────────────────────────
// SourceGeneratorLambdaJsonSerializer + ClaimsJsonContext: AOT-safe payload
// (de)serialization with zero runtime reflection.
var handler = async (ClaimEvent claim, ILambdaContext context) =>
    await TriageHandler.HandleAsync(claim, context.Logger.LogInformation);

await LambdaBootstrapBuilder
    .Create(handler, new SourceGeneratorLambdaJsonSerializer<ClaimsJsonContext>())
    .Build()
    .RunAsync();

// AOT-safe pretty printer (JsonDocument + Utf8JsonWriter — no reflection).
static string PrettyPrint(string json)
{
    using var doc = JsonDocument.Parse(json);
    using var stream = new MemoryStream();
    using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
    {
        doc.WriteTo(writer);
    }
    return Encoding.UTF8.GetString(stream.ToArray());
}
