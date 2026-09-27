using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Jevdexer.Core.Remote;
using Jevdexer.Core.Search;

namespace Jevdexer.Core.Tests;

/// <summary>JevClient against a fake AI Gateway: the requests it makes and how it reads the answers (no network, no credits).</summary>
public class JevClientTests
{
    /// <summary>Answers every boolean with 0.9 when its instructions mention <paramref name="yes"/>, else 0.1; choices pick <paramref name="choice"/>.</summary>
    private sealed class FakeGateway(string yes = "", string choice = "messaging", HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        public List<(HttpRequestMessage Request, JsonObject? Body)> Calls { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? null : JsonNode.Parse(await request.Content.ReadAsStringAsync(ct))?.AsObject();
            lock (Calls) Calls.Add((request, body));
            if (status != HttpStatusCode.OK) return new HttpResponseMessage(status);
            if (request.RequestUri!.AbsolutePath == "/v1/credits")
                return Json("""{"balance":"4.20","total_used":"0.80"}""");

            var answers = new JsonObject();
            foreach (var (id, q) in body!["questions"]!.AsObject())
            {
                answers[id] = q!["type"]!.GetValue<string>() == "choice"
                    ? new JsonObject { ["type"] = "choice", ["choice"] = choice, ["probabilities"] = new JsonObject { [choice] = 0.8, ["browser"] = 0.005 } }
                    : new JsonObject { ["type"] = "boolean", ["probability"] = q["instructions"]!.GetValue<string>().Contains(yes) && yes.Length > 0 ? 0.9 : 0.1 };
            }
            return Json(new JsonObject { ["answers"] = answers, ["model"] = "typesafe-ai/jev" }.ToJsonString());
        }

        private static HttpResponseMessage Json(string s) => new(HttpStatusCode.OK) { Content = new StringContent(s, Encoding.UTF8, "application/json") };
    }

    [Fact]
    public async Task Calls_the_gateway_evaluation_endpoint_with_the_users_key()
    {
        var gateway = new FakeGateway(yes: "WhatsApp");
        using var jev = new JevClient("vck_user_key", gateway);
        var r = await jev.SearchAsync("that green messaging app", [new("w", "WhatsApp by WhatsApp"), new("t", "Telegram by Telegram")]);

        var (request, body) = gateway.Calls.First(c => c.Body!["questions"]!.AsObject().ContainsKey("a0"));
        Assert.Equal("https://ai-gateway.vercel.sh/v4/ai/evaluation-model", request.RequestUri!.ToString());
        Assert.Equal("Bearer vck_user_key", request.Headers.Authorization!.ToString());
        Assert.Equal("typesafe-ai/jev", request.Headers.GetValues("ai-model-id").Single());
        Assert.Equal("that green messaging app", body!["state"]!["search"]!.GetValue<string>());
        Assert.Equal(0.9, r.Apps["w"]);
        Assert.Equal(0.1, r.Apps["t"]);
        Assert.Equal(new Dictionary<string, double> { ["messaging"] = 0.8 }, r.QueryTags); // tiny probabilities dropped
    }

    [Fact]
    public async Task Labels_ask_every_tag_and_the_background_question()
    {
        var gateway = new FakeGateway(yes: "chatting");
        using var jev = new JevClient("k", gateway);
        var state = new AppState("WhatsApp", "WhatsApp", [], null, null, [], []);
        var r = await jev.LabelAsync([new("w", state)]);

        var questions = gateway.Calls.Single().Body!["questions"]!.AsObject();
        Assert.True(questions.ContainsKey("is_background_component"));
        Assert.Equal(TagCatalog.List.Tags.Count + 1, questions.Count);
        Assert.Equal(0.9, r.Labels.Single().Tags["messaging"]);
        Assert.Equal(TagCatalog.Version, r.TagListVersion);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, typeof(JevKeyException))]
    [InlineData(HttpStatusCode.PaymentRequired, typeof(JevCreditsException))]
    [InlineData(HttpStatusCode.TooManyRequests, typeof(JevRateLimitedException))]
    [InlineData(HttpStatusCode.BadGateway, typeof(JevException))]
    public async Task Gateway_errors_become_typed_exceptions(HttpStatusCode status, Type expected)
    {
        using var jev = new JevClient("k", new FakeGateway(status: status));
        var e = await Assert.ThrowsAnyAsync<JevException>(() => jev.SearchContentAsync("q", [new("c0", "a summary")]));
        Assert.IsType(expected, e);
    }

    [Fact]
    public async Task Checking_a_key_reads_the_balance_and_spends_nothing()
    {
        var gateway = new FakeGateway();
        var (state, balance) = await JevClient.CheckKeyAsync("k", gateway);
        Assert.Equal(JevClient.KeyState.Ok, state);
        Assert.Equal(4.20m, balance);
        Assert.Equal("/v1/credits", gateway.Calls.Single().Request.RequestUri!.AbsolutePath);

        Assert.Equal(JevClient.KeyState.Rejected, (await JevClient.CheckKeyAsync("bad", new FakeGateway(status: HttpStatusCode.Unauthorized))).State);
    }

    [Fact]
    public void Without_a_key_or_with_smart_search_off_there_is_no_client()
    {
        Assert.Null(JevClient.For(new Settings { SmartSearch = true }));
        var off = new Settings { SmartSearch = false };
        off.GatewayKey = "k";
        Assert.Null(JevClient.For(off));
        var on = new Settings { SmartSearch = true };
        on.GatewayKey = "k";
        using var client = JevClient.For(on);
        Assert.NotNull(client);
    }
}
