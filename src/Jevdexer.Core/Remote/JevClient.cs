using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Jevdexer.Core.Search;

namespace Jevdexer.Core.Remote;

/// <summary>
/// Jev, called directly through the Vercel AI Gateway with the user's own key. The app's only
/// network code for Smart search: discovery has none, and without a key or with Smart search off
/// this is never constructed. Requests go to one host, <see cref="Gateway"/>, and carry app names,
/// the query and scrubbed summaries only.
///
/// The Gateway's evaluation endpoint is what the AI SDK's `experimental_evaluate` calls:
/// POST /v4/ai/evaluation-model with {state, questions}; answers per question id.
/// The questions are written here, never taken from file content.
/// </summary>
public sealed class JevClient : IDisposable
{
    public const string Gateway = "https://ai-gateway.vercel.sh";
    public const string Model = "typesafe-ai/jev";
    private const int LabelConcurrency = 8;
    /// <summary>Booleans per request at query time: 700 measured at 529 ms; stay well under.</summary>
    private const int SearchChunk = 500;

    private readonly HttpClient _http;
    private readonly string _key;

    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public JevClient(string gatewayKey, HttpMessageHandler? handler = null)
    {
        _key = gatewayKey;
        _http = handler is null ? new HttpClient() : new HttpClient(handler);
        _http.BaseAddress = new Uri(Gateway + "/");
        _http.Timeout = TimeSpan.FromSeconds(60);
    }

    /// <summary>A client for these settings, or null: Smart search off, or no key (then nothing goes online).</summary>
    public static JevClient? For(Settings settings) =>
        settings.SmartSearch && settings.GatewayKey.Length > 0 ? new JevClient(settings.GatewayKey) : null;

    // ---------------------------------------------------------------- the key

    public enum KeyState { Ok, Rejected, NoCredits, Unreachable }

    /// <summary>Checks a key without spending anything (the Gateway's credit balance). Never throws.</summary>
    public static async Task<(KeyState State, decimal? Balance)> CheckKeyAsync(string key, HttpMessageHandler? handler = null, CancellationToken ct = default)
    {
        try
        {
            using var http = handler is null ? new HttpClient() : new HttpClient(handler);
            http.Timeout = TimeSpan.FromSeconds(10);
            using var request = new HttpRequestMessage(HttpMethod.Get, Gateway + "/v1/credits");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key.Trim());
            request.Headers.Add("ai-gateway-auth-method", "api-key");
            using var res = await http.SendAsync(request, ct);
            if (res.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) return (KeyState.Rejected, null);
            if (!res.IsSuccessStatusCode) return (KeyState.Unreachable, null);
            var body = await res.Content.ReadFromJsonAsync<JsonObject>(ct);
            var balance = decimal.TryParse(body?["balance"]?.ToString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var b) ? b : (decimal?)null;
            return (balance is <= 0 ? KeyState.NoCredits : KeyState.Ok, balance);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException)
        {
            return (KeyState.Unreachable, null);
        }
    }

    // ---------------------------------------------------------------- labeling (index time)

    public sealed record LabelRequestApp(string Id, AppState State);
    public sealed record LabelResult(string Id, double Background, Dictionary<string, double> Tags);
    public sealed record LabelResponse(string Model, string TagListVersion, List<LabelResult> Labels);

    /// <summary>One request per app: "is it a background component?" plus one question per tag.</summary>
    public async Task<LabelResponse> LabelAsync(IReadOnlyList<LabelRequestApp> apps, CancellationToken ct = default)
    {
        var tags = TagCatalog.List.Tags;
        var questions = new JsonObject
        {
            ["is_background_component"] = Boolean("Is this a background component, helper, updater, driver or service rather than something a person opens and uses?"),
        };
        foreach (var (id, tag) in tags) questions[id] = Boolean(TagCatalog.Question(tag.Meaning));

        var results = new LabelResult[apps.Count];
        using var gate = new SemaphoreSlim(LabelConcurrency);
        await Task.WhenAll(apps.Select(async (app, i) =>
        {
            await gate.WaitAsync(ct);
            try
            {
                var answers = await EvaluateAsync(JsonSerializer.SerializeToNode(app.State, Json)!, questions.DeepClone().AsObject(), ct);
                results[i] = new LabelResult(app.Id, Probability(answers, "is_background_component"),
                    tags.Keys.ToDictionary(t => t, t => Probability(answers, t)));
            }
            finally
            {
                gate.Release();
            }
        }));
        return new LabelResponse(Model, TagCatalog.Version, [.. results]);
    }

    // ---------------------------------------------------------------- search (query time)

    public sealed record SearchRequestApp(string Id, string Line);
    public sealed record SearchResponse(string Model, string TagListVersion, Dictionary<string, double> QueryTags, Dictionary<string, double> Apps);

    /// <summary>Layer 2 (a choice: which tag the query means) and layer 3 (one yes/no per app), in parallel.</summary>
    public async Task<SearchResponse> SearchAsync(string query, IReadOnlyList<SearchRequestApp> apps, CancellationToken ct = default)
    {
        var state = new JsonObject { ["search"] = query };
        var criteria = new JsonObject();
        foreach (var (id, tag) in TagCatalog.List.Tags) criteria[id] = tag.Meaning;
        var tagsCall = EvaluateAsync(state.DeepClone(), new JsonObject
        {
            ["tag"] = new JsonObject { ["type"] = "choice", ["instructions"] = "Which kind of app is the user searching for?", ["criteria"] = criteria },
        }, ct);

        var appCalls = apps.Chunk(SearchChunk).Select(async chunk =>
        {
            var questions = new JsonObject();
            for (var i = 0; i < chunk.Length; i++) questions[$"a{i}"] = Boolean($"Would \"{chunk[i].Line}\" be a good result for this search?");
            var answers = await EvaluateAsync(state.DeepClone(), questions, ct);
            return chunk.Select((a, i) => (a.Id, P: Probability(answers, $"a{i}")));
        }).ToList();

        var tagAnswer = (await tagsCall)["tag"]?.AsObject();
        var queryTags = new Dictionary<string, double>();
        if (tagAnswer?["probabilities"] is JsonObject probabilities)
        {
            foreach (var (id, p) in probabilities)
                if (p?.GetValue<double>() is >= 0.01 and var v) queryTags[id] = v;
        }
        else if (tagAnswer?["choice"]?.GetValue<string>() is { } choice) queryTags[choice] = 1;

        var scored = (await Task.WhenAll(appCalls)).SelectMany(x => x).ToDictionary(x => x.Id, x => x.P);
        return new SearchResponse(Model, TagCatalog.Version, queryTags, scored);
    }

    /// <summary>One scrubbed summary to judge (a folder, a file, a photo), with a positional id: never a path.</summary>
    public sealed record FolderRequest(string Id, string Summary);
    public sealed record FolderSearchResponse(string Model, Dictionary<string, double> Folders);
    public sealed record ContentSearchResponse(string Model, Dictionary<string, double> Items);

    /// <summary>Opt-in folder Smart search: up to 120 scrubbed summaries, never paths.</summary>
    public async Task<FolderSearchResponse> SearchFoldersAsync(string query, IReadOnlyList<FolderRequest> folders, CancellationToken ct = default) =>
        new(Model, await JudgeAsync(query, folders.Take(120).ToList(), s => $"Is this the folder the user is looking for: {s}?", ct));

    /// <summary>Ultra Search: up to 40 scrubbed content summaries, never text passages or paths.</summary>
    public async Task<ContentSearchResponse> SearchContentAsync(string query, IReadOnlyList<FolderRequest> items, CancellationToken ct = default) =>
        new(Model, await JudgeAsync(query, items.Take(40).ToList(), s => $"Is this what the user is looking for: {s}?", ct));

    private async Task<Dictionary<string, double>> JudgeAsync(string query, IReadOnlyList<FolderRequest> items, Func<string, string> question, CancellationToken ct)
    {
        if (items.Count == 0) return [];
        var questions = new JsonObject();
        for (var i = 0; i < items.Count; i++) questions[$"q{i}"] = Boolean(question(Clip(items[i].Summary, 400)));
        var answers = await EvaluateAsync(new JsonObject { ["search"] = Clip(query, 200) }, questions, ct);
        return items.Select((x, i) => (x.Id, P: Probability(answers, $"q{i}"))).ToDictionary(x => x.Id, x => x.P);
    }

    // ---------------------------------------------------------------- the Gateway

    private async Task<JsonObject> EvaluateAsync(JsonNode state, JsonObject questions, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "v4/ai/evaluation-model")
        {
            Content = JsonContent.Create(new JsonObject { ["state"] = state, ["questions"] = questions }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _key);
        request.Headers.Add("ai-gateway-protocol-version", "0.0.1");
        request.Headers.Add("ai-gateway-auth-method", "api-key");
        request.Headers.Add("ai-evaluation-model-specification-version", "4");
        request.Headers.Add("ai-model-id", Model);

        using var res = await _http.SendAsync(request, ct);
        if (res.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) throw new JevKeyException();
        if (res.StatusCode == HttpStatusCode.PaymentRequired) throw new JevCreditsException();
        if (res.StatusCode == HttpStatusCode.TooManyRequests)
            throw new JevRateLimitedException(res.Headers.RetryAfter?.Delta ?? TimeSpan.FromMinutes(1));
        // Status only: the body can echo the request.
        if (!res.IsSuccessStatusCode) throw new JevException($"Jev returned {(int)res.StatusCode}");
        var body = await res.Content.ReadFromJsonAsync<JsonObject>(ct) ?? throw new JevException("empty answer");
        return body["answers"]?.AsObject() ?? throw new JevException("no answers");
    }

    private static JsonObject Boolean(string instructions) => new() { ["type"] = "boolean", ["instructions"] = instructions };

    private static double Probability(JsonObject answers, string id) =>
        answers[id]?["probability"]?.GetValue<double>() ?? 0;

    private static string Clip(string s, int max) => s.Length <= max ? s : s[..max];

    public void Dispose() => _http.Dispose();
}

public class JevException(string message) : Exception(message);

/// <summary>The Gateway refused the key (wrong, revoked, or for another team).</summary>
public sealed class JevKeyException() : JevException("the AI Gateway key was rejected");

/// <summary>The key's Vercel account has no AI Gateway credits left.</summary>
public sealed class JevCreditsException() : JevException("no AI Gateway credits left");

/// <summary>The Gateway's limit was reached; Smart search resumes after <see cref="RetryAfter"/>.</summary>
public sealed class JevRateLimitedException(TimeSpan retryAfter) : JevException($"rate limited for {retryAfter.TotalSeconds:0} s")
{
    public TimeSpan RetryAfter { get; } = retryAfter;
}
