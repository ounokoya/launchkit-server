using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;

namespace LaunchKit.Api.Infrastructure;

public sealed class ArangoOptions
{
    public string Url { get; set; } = "http://localhost:8529";
    public string Database { get; set; } = "launchkit";
    public string User { get; set; } = "root";
    public string Password { get; set; } = "";

    /// <summary>Create the database, collections and indexes at startup (ADR-0002).</summary>
    public bool InitializeSchema { get; set; } = true;
}

/// <summary>Error returned by ArangoDB (ADR-0008). <see cref="ErrorNum"/> is ArangoDB's own error number.</summary>
public sealed class ArangoException(int errorNum, int httpCode, string message) : Exception(message)
{
    public const int Conflict = 1200;
    public const int UniqueConstraintViolated = 1210;
    public const int DuplicateName = 1207;

    public int ErrorNum { get; } = errorNum;
    public int HttpCode { get; } = httpCode;
    public bool IsConflict => ErrorNum == Conflict;
    public bool IsUniqueViolation => ErrorNum == UniqueConstraintViolated;
}

/// <summary>
/// Minimal client on ArangoDB's HTTP API (ADR-0008): parameterised AQL cursors, stream transactions and the few
/// administration calls needed to create the schema. Every business query binds its values (<c>@name</c>).
/// </summary>
public sealed class ArangoClient
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;
    private readonly string _db;

    public ArangoClient(HttpClient http, IOptions<ArangoOptions> options)
    {
        var o = options.Value;
        _http = http;
        _http.BaseAddress ??= new Uri(o.Url.TrimEnd('/') + "/");
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{o.User}:{o.Password}")));
        _db = o.Database;
    }

    public async Task<List<T>> QueryAsync<T>(string aql, object? bindVars = null, string? trxId = null,
        CancellationToken ct = default)
    {
        var results = new List<T>();
        var body = new JsonObject
        {
            ["query"] = aql,
            ["bindVars"] = JsonSerializer.SerializeToNode(bindVars ?? new { }, Json),
            ["batchSize"] = 1000,
        };
        var page = await SendAsync(HttpMethod.Post, $"_db/{_db}/_api/cursor", body, trxId, ct);
        while (true)
        {
            foreach (var item in page["result"]!.AsArray())
                results.Add(item.Deserialize<T>(Json)!);
            if (page["hasMore"]?.GetValue<bool>() != true)
                return results;
            page = await SendAsync(HttpMethod.Post, $"_db/{_db}/_api/cursor/{page["id"]!.GetValue<string>()}", null,
                trxId, ct);
        }
    }

    public async Task<T?> FirstAsync<T>(string aql, object? bindVars = null, string? trxId = null,
        CancellationToken ct = default)
    {
        var list = await QueryAsync<T>(aql, bindVars, trxId, ct);
        return list.Count > 0 ? list[0] : default;
    }

    public Task ExecuteAsync(string aql, object? bindVars = null, string? trxId = null, CancellationToken ct = default)
        => QueryAsync<JsonNode?>(aql, bindVars, trxId, ct);

    /// <summary>Starts a stream transaction on the given collections; dispose without commit aborts it.</summary>
    public async Task<ArangoTransaction> BeginAsync(string[] write, string[]? read = null, CancellationToken ct = default)
    {
        var body = new JsonObject
        {
            ["collections"] = new JsonObject
            {
                ["write"] = JsonSerializer.SerializeToNode(write, Json),
                ["read"] = JsonSerializer.SerializeToNode(read ?? [], Json),
            },
        };
        var res = await SendAsync(HttpMethod.Post, $"_db/{_db}/_api/transaction/begin", body, null, ct);
        return new ArangoTransaction(this, res["result"]!["id"]!.GetValue<string>());
    }

    internal Task EndTransactionAsync(string id, bool commit)
        => SendAsync(commit ? HttpMethod.Put : HttpMethod.Delete, $"_db/{_db}/_api/transaction/{id}", null, null,
            CancellationToken.None);

    /// <summary>Runs <paramref name="action"/> again when two writers touched the same document (error 1200).</summary>
    public static async Task<T> RetryOnConflictAsync<T>(Func<Task<T>> action, int attempts = 8)
    {
        for (var i = 1; ; i++)
        {
            try
            {
                return await action();
            }
            catch (ArangoException e) when (e.IsConflict && i < attempts)
            {
                await Task.Delay(Random.Shared.Next(5, 20 * i));
            }
        }
    }

    // ----- schema (ADR-0002) -----

    public async Task EnsureDatabaseAsync(CancellationToken ct = default)
    {
        try
        {
            await SendAsync(HttpMethod.Post, "_db/_system/_api/database", new JsonObject { ["name"] = _db }, null, ct);
        }
        catch (ArangoException e) when (e.ErrorNum == ArangoException.DuplicateName)
        {
        }
    }

    public async Task EnsureCollectionAsync(string name, bool edge, CancellationToken ct = default)
    {
        try
        {
            await SendAsync(HttpMethod.Post, $"_db/{_db}/_api/collection",
                new JsonObject { ["name"] = name, ["type"] = edge ? 3 : 2 }, null, ct);
        }
        catch (ArangoException e) when (e.ErrorNum == ArangoException.DuplicateName)
        {
        }
    }

    /// <summary>Creating an index that already exists is a no-op in ArangoDB.</summary>
    public Task EnsureIndexAsync(string collection, JsonObject index, CancellationToken ct = default)
        => SendAsync(HttpMethod.Post, $"_db/{_db}/_api/index?collection={collection}", index, null, ct);

    public async Task<bool> PingAsync(CancellationToken ct = default)
    {
        try
        {
            await SendAsync(HttpMethod.Get, $"_db/{_db}/_api/version", null, null, ct);
            return true;
        }
        catch (Exception e) when (e is HttpRequestException or ArangoException or TaskCanceledException)
        {
            return false;
        }
    }

    private async Task<JsonNode> SendAsync(HttpMethod method, string path, JsonNode? body, string? trxId,
        CancellationToken ct)
    {
        using var req = new HttpRequestMessage(method, path);
        if (body is not null) // ArangoDB refuses chunked bodies: send the length up front
            req.Content = new StringContent(body.ToJsonString(Json), Encoding.UTF8, "application/json");
        if (trxId is not null)
            req.Headers.Add("x-arango-trx-id", trxId);
        using var res = await _http.SendAsync(req, ct);
        var node = res.Content.Headers.ContentLength == 0
            ? null
            : await res.Content.ReadFromJsonAsync<JsonNode>(Json, ct);
        if (node?["error"]?.GetValue<bool>() == true || !res.IsSuccessStatusCode)
        {
            throw new ArangoException(
                node?["errorNum"]?.GetValue<int>() ?? 0,
                (int)res.StatusCode,
                node?["errorMessage"]?.GetValue<string>() ?? res.ReasonPhrase ?? "ArangoDB error");
        }
        return node ?? new JsonObject();
    }
}

/// <summary>An ArangoDB stream transaction: every query sends its id; <see cref="DisposeAsync"/> aborts if not committed.</summary>
public sealed class ArangoTransaction(ArangoClient client, string id) : IAsyncDisposable
{
    private bool _done;

    public Task<List<T>> QueryAsync<T>(string aql, object? bindVars = null, CancellationToken ct = default)
        => client.QueryAsync<T>(aql, bindVars, id, ct);

    public Task<T?> FirstAsync<T>(string aql, object? bindVars = null, CancellationToken ct = default)
        => client.FirstAsync<T>(aql, bindVars, id, ct);

    public Task ExecuteAsync(string aql, object? bindVars = null, CancellationToken ct = default)
        => client.ExecuteAsync(aql, bindVars, id, ct);

    public async Task CommitAsync()
    {
        await client.EndTransactionAsync(id, commit: true);
        _done = true;
    }

    public async ValueTask DisposeAsync()
    {
        if (_done)
            return;
        _done = true;
        try
        {
            await client.EndTransactionAsync(id, commit: false);
        }
        catch (Exception e) when (e is ArangoException or HttpRequestException)
        {
            // The transaction already ended on the server (e.g. after a conflict): nothing left to abort.
        }
    }
}
