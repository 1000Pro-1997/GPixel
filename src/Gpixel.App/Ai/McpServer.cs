using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Gpixel.App.Localization;

namespace Gpixel.App.Ai;

/// <summary>
/// 앱 안에서 도는 최소 MCP 서버(Streamable HTTP, <see cref="HttpListener"/>). 127.0.0.1의 빈 포트에서
/// <c>POST /mcp</c>로 JSON-RPC 2.0 메시지(하나 또는 묶음)를 받고, Bearer 토큰이 맞아야 응답한다.
/// <para>
/// 요청마다 백그라운드 작업에서 처리하고, <c>tools/call</c>만 UI 스레드로 넘겨 <see cref="PixelTools.CallAsync"/>를 부른다. 오래 걸리는 도구(그림 생성)는
/// 클라이언트가 받으면 SSE로 답해 기다리는 동안 신호·진행을 보낸다. 어떤 예외도 받기 루프 밖으로 나가지 않는다. 응답이 필요 없는 메시지(알림·응답)는 202, GET은 405, DELETE는 200.
/// </para>
/// </summary>
public sealed class McpServer : IDisposable
{
    public const string ServerName = "gpixel";

    private const string DefaultProtocolVersion = "2025-06-18";
    private const int MaxBodyBytes = 16 * 1024 * 1024;

    private readonly PixelTools _tools;
    private readonly object _gate = new();
    private readonly string _sessionId = Guid.NewGuid().ToString("N");
    private HttpListener? _listener;
    private string _host = "127.0.0.1";
    private int _port;
    private readonly HashSet<string> _configPaths = new(StringComparer.Ordinal);
    private volatile bool _disposed;
    private long _requests, _unauthorized, _toolLists;

    public McpServer(PixelTools tools)
    {
        _tools = tools;
        Token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
    }

    /// <summary>편집기 도구(차례마다 편집 범위를 정하려고 채팅 창이 씀).</summary>
    public PixelTools Tools => _tools;

    /// <summary>"http://127.0.0.1:포트/mcp"(시작 전이면 빈 문자열).</summary>
    public string Url => _port == 0 ? "" : $"http://{_host}:{_port}/mcp";

    /// <summary>요청 헤더 <c>Authorization: Bearer …</c>에 넣어야 하는 무작위 토큰(32바이트 16진수).</summary>
    public string Token { get; }

    /// <summary>지금까지 받은 /mcp 요청 수(진단용). 세션이 시작할 때 찍어 두고 나중에 비교한다.</summary>
    public McpCounters Counters => new(Interlocked.Read(ref _requests), Interlocked.Read(ref _unauthorized), Interlocked.Read(ref _toolLists));

    /// <summary>CLI가 <paramref name="since"/> 뒤로 도구 목록을 받아 갔는지(= gpixel MCP에 연결됐는지).</summary>
    public bool ToolsListedSince(McpCounters since) => Interlocked.Read(ref _toolLists) > since.ToolLists;

    /// <summary>연결 실패 안내: <paramref name="since"/> 뒤로 서버가 본 요청으로 어디서 막혔는지 짐작한다.</summary>
    public string DescribeProblem(McpCounters since, string? cliStatus)
    {
        var now = Counters;
        var head = cliStatus is null ? UiStrings.S_McpServer_DescribeProblem_23C0F2EB81 : string.Format(global::System.Globalization.CultureInfo.CurrentCulture,UiStrings.S_McpServer_DescribeProblem_BEA0D8C5FE,cliStatus);
        if (now.Requests == since.Requests)
            return string.Format(global::System.Globalization.CultureInfo.CurrentCulture,UiStrings.S_McpServer_DescribeProblem_EDC6AA4CA7,head,Url);
        if (now.Unauthorized > since.Unauthorized)
            return string.Format(global::System.Globalization.CultureInfo.CurrentCulture,UiStrings.S_McpServer_DescribeProblem_AB4BF0B540,head);
        return string.Format(global::System.Globalization.CultureInfo.CurrentCulture,UiStrings.S_McpServer_DescribeProblem_7D8C7E9591,head);
    }

    /// <summary>빈 포트를 찾아 듣기 시작한다. 이미 시작했으면 아무것도 안 한다.</summary>
    public void Start()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_listener is not null) return;
            Exception? last = null;
            for (int attempt = 0; attempt < 8; attempt++)
            {
                int port = FreePort();
                foreach (var host in Hosts())
                {
                    var listener = new HttpListener();
                    listener.Prefixes.Add($"http://{host}:{port}/");
                    try
                    {
                        listener.Start();
                    }
                    catch (Exception e) when (e is HttpListenerException or SocketException)
                    {
                        last = e;
                        try { listener.Close(); } catch { /* 이미 닫힘 */ }
                        continue;
                    }
                    _listener = listener;
                    _host = host;
                    _port = port;
                    _ = Task.Run(() => AcceptLoopAsync(listener));
                    return;
                }
            }
            throw new InvalidOperationException(string.Format(global::System.Globalization.CultureInfo.CurrentCulture,UiStrings.S_McpServer_Start_FF0B0F6631,last?.Message), last);
        }
    }

    /// <summary>
    /// 들을 주소 후보. Windows(http.sys)는 관리자 권한 없이 IP 주소 접두사를 등록할 수 없는 경우가 있어
    /// 실패하면 localhost로 다시 시도한다.
    /// </summary>
    private static string[] Hosts() => OperatingSystem.IsWindows() ? ["127.0.0.1", "localhost"] : ["127.0.0.1"];

    private static int FreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        try
        {
            return ((IPEndPoint)probe.LocalEndpoint).Port;
        }
        finally
        {
            probe.Stop();
        }
    }

    /// <summary>
    /// Claude Code의 <c>--mcp-config</c> 파일을 임시 폴더에 쓴다(서버가 꺼져 있으면 시작).
    /// 동시에 상태 점검과 채팅이 시작되어도 서로 파일을 지우지 않도록 호출마다 고유 파일을 만들며,
    /// 토큰이 들어 있으므로 macOS·Linux에서는 본인만 읽을 수 있게 만든다.
    /// </summary>
    public string WriteMcpConfigFile()
    {
        Start();
        var config = new JsonObject
        {
            ["mcpServers"] = new JsonObject
            {
                [ServerName] = new JsonObject
                {
                    ["type"] = "http",
                    ["url"] = Url,
                    ["headers"] = new JsonObject { ["Authorization"] = "Bearer " + Token },
                },
            },
        };
        var path = Path.Combine(Path.GetTempPath(), $"gpixel-mcp-{Environment.ProcessId}-{Guid.NewGuid():N}.json");
        var bytes = Encoding.UTF8.GetBytes(config.ToJsonString(PixelTools.JsonOptions));
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        using (var fs = new FileStream(path, options)) fs.Write(bytes);
        lock (_gate)
        {
            if (_disposed)
            {
                try { File.Delete(path); } catch { /* 종료와 겹친 임시 파일 */ }
                throw new ObjectDisposedException(nameof(McpServer));
            }
            _configPaths.Add(path);
        }
        return path;
    }

    /// <summary>짧게 띄운 상태 점검 프로세스가 다 읽은 임시 설정을 바로 지운다.</summary>
    internal void ReleaseMcpConfigFile(string path)
    {
        lock (_gate)
            if (!_configPaths.Remove(path)) return;
        try { File.Delete(path); } catch { /* 앱 종료 때 남은 임시 파일과 함께 정리됨 */ }
    }

    public void Dispose()
    {
        HttpListener? listener;
        string[] configPaths;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            listener = _listener;
            _listener = null;
            configPaths = [.. _configPaths];
            _configPaths.Clear();
        }
        try { listener?.Close(); } catch { /* 종료 중 오류는 무시 */ }
        foreach (var path in configPaths)
            try { File.Delete(path); } catch { /* 지우지 못해도 임시 폴더라 괜찮다 */ }
    }

    // ───────────── HTTP ─────────────

    private async Task AcceptLoopAsync(HttpListener listener)
    {
        while (!_disposed)
        {
            HttpListenerContext context;
            try
            {
                if (!listener.IsListening) return;
                context = await listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (Exception)
            {
                if (_disposed || !SafeIsListening(listener)) return;
                try { await Task.Delay(50).ConfigureAwait(false); } catch { /* 무시 */ }
                continue;
            }
            _ = Task.Run(() => HandleAsync(context));
        }
    }

    private static bool SafeIsListening(HttpListener listener)
    {
        try { return listener.IsListening; }
        catch { return false; }
    }

    private async Task HandleAsync(HttpListenerContext context)
    {
        var response = context.Response;
        try
        {
            var request = context.Request;
            var path = request.Url?.AbsolutePath.TrimEnd('/') ?? "";
            if (path != "/mcp")
            {
                response.StatusCode = 404;
                return;
            }
            Interlocked.Increment(ref _requests);
            if (!Authorized(request))
            {
                Interlocked.Increment(ref _unauthorized);
                response.StatusCode = 401;
                response.AddHeader("WWW-Authenticate", "Bearer");
                return;
            }
            switch (request.HttpMethod)
            {
                case "POST":
                    await HandlePostAsync(request, response).ConfigureAwait(false);
                    break;
                case "DELETE":
                    response.StatusCode = 200;
                    break;
                default:
                    response.StatusCode = 405;
                    response.AddHeader("Allow", "POST, DELETE");
                    break;
            }
        }
        catch (Exception)
        {
            try { response.StatusCode = 500; } catch { /* 이미 헤더를 보냄 */ }
        }
        finally
        {
            try { response.Close(); } catch { /* 연결이 끊김 */ }
        }
    }

    private bool Authorized(HttpListenerRequest request)
    {
        var header = request.Headers["Authorization"]?.Trim();
        const string scheme = "Bearer ";
        if (header is null || !header.StartsWith(scheme, StringComparison.OrdinalIgnoreCase)) return false;
        var given = Encoding.UTF8.GetBytes(header[scheme.Length..].Trim());
        var expected = Encoding.UTF8.GetBytes(Token);
        return CryptographicOperations.FixedTimeEquals(given, expected);
    }

    private async Task HandlePostAsync(HttpListenerRequest request, HttpListenerResponse response)
    {
        if (request.ContentLength64 > MaxBodyBytes)
        {
            response.StatusCode = 413;
            return;
        }
        byte[] body;
        using (var ms = new MemoryStream())
        {
            var buffer = new byte[16384];
            int n;
            while ((n = await request.InputStream.ReadAsync(buffer).ConfigureAwait(false)) > 0)
            {
                if (ms.Length + n > MaxBodyBytes)
                {
                    response.StatusCode = 413;
                    return;
                }
                ms.Write(buffer, 0, n);
            }
            body = ms.ToArray();
        }

        JsonNode? root;
        try
        {
            root = ParseBody(body);
        }
        catch (Exception e) when (e is JsonException or ArgumentException or InvalidOperationException)
        {
            await WriteJsonAsync(response, 400, ErrorResponse(null, -32700, "Parse error")).ConfigureAwait(false);
            return;
        }

        if (root is JsonArray batch)
        {
            if (batch.Count == 0)
            {
                await WriteJsonAsync(response, 400, ErrorResponse(null, -32600, "Invalid Request: empty batch")).ConfigureAwait(false);
                return;
            }
            var replies = new JsonArray();
            bool initialize = false;
            foreach (var item in batch.ToList())
            {
                initialize |= IsInitialize(item);
                if (await ProcessAsync(item).ConfigureAwait(false) is { } reply) replies.Add(reply);
            }
            if (initialize) response.AddHeader("Mcp-Session-Id", _sessionId);
            if (replies.Count == 0) response.StatusCode = 202;
            else await WriteJsonAsync(response, 200, replies).ConfigureAwait(false);
        }
        else if (root is JsonObject single && IsLongToolCall(single) && AcceptsEventStream(request))
        {
            await StreamToolCallAsync(single, response).ConfigureAwait(false);
        }
        else
        {
            if (IsInitialize(root)) response.AddHeader("Mcp-Session-Id", _sessionId);
            if (await ProcessAsync(root).ConfigureAwait(false) is { } reply) await WriteJsonAsync(response, 200, reply).ConfigureAwait(false);
            else response.StatusCode = 202;
        }
    }

    /// <summary>오래 걸리는 도구(<see cref="PixelTools.IsLongRunning"/>)를 부르는 <c>tools/call</c> 요청인지.</summary>
    private static bool IsLongToolCall(JsonObject message)
        => AsString(message["method"]) == "tools/call" && message.ContainsKey("id")
           && AsString((message["params"] as JsonObject)?["name"]) is { } name && PixelTools.IsLongRunning(name);

    private static bool AcceptsEventStream(HttpListenerRequest request)
        => request.Headers["Accept"]?.Contains("text/event-stream", StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>
    /// 오래 걸리는 도구 호출을 SSE(<c>text/event-stream</c>)로 답한다: 머리를 바로 보내고, 도는 동안 15초마다 빈 줄 신호(클라이언트의 HTTP 시간 제한 방지)와
    /// 요청에 progressToken이 있으면 <c>notifications/progress</c>(진행 글)를 보내고, 끝나면 결과 메시지 하나로 닫는다.
    /// 클라이언트가 끊으면(쓰기 실패) 도구를 취소한다.
    /// </summary>
    private async Task StreamToolCallAsync(JsonObject message, HttpListenerResponse response)
    {
        var id = message["id"];
        var parameters = message["params"] as JsonObject;
        var name = AsString(parameters?["name"])!;
        var arguments = parameters?["arguments"] as JsonObject;
        var progressToken = (parameters?["_meta"] as JsonObject)?["progressToken"]?.DeepClone();
        response.StatusCode = 200;
        response.ContentType = "text/event-stream";
        response.SendChunked = true;
        response.AddHeader("Cache-Control", "no-cache");
        var output = response.OutputStream;
        using var gate = new SemaphoreSlim(1, 1);
        using var cts = new CancellationTokenSource();
        async Task Send(string text)
        {
            await gate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (cts.IsCancellationRequested) return;
                var bytes = Encoding.UTF8.GetBytes(text);
                await output.WriteAsync(bytes).ConfigureAwait(false);
                await output.FlushAsync().ConfigureAwait(false);
            }
            catch (Exception e) when (e is HttpListenerException or IOException or ObjectDisposedException or InvalidOperationException)
            {
                cts.Cancel();
            }
            finally
            {
                gate.Release();
            }
        }
        await Send(": started\n\n").ConfigureAwait(false);
        int step = 0;
        Action<string> progress = text =>
        {
            if (progressToken is null) return;
            var note = new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["method"] = "notifications/progress",
                ["params"] = new JsonObject { ["progressToken"] = progressToken.DeepClone(), ["progress"] = Interlocked.Increment(ref step), ["message"] = text },
            };
            _ = Send("event: message\ndata: " + note.ToJsonString(PixelTools.JsonOptions) + "\n\n");
        };
        var call = CallToolAsync(name, arguments, progress, cts.Token);
        while (await Task.WhenAny(call, Task.Delay(TimeSpan.FromSeconds(15))).ConfigureAwait(false) != call)
            await Send(": keepalive\n\n").ConfigureAwait(false);
        var reply = Result(id, await call.ConfigureAwait(false));
        await Send("event: message\ndata: " + reply.ToJsonString(PixelTools.JsonOptions) + "\n\n").ConfigureAwait(false);
    }

    private static bool IsInitialize(JsonNode? node)
        => node is JsonObject o && AsString(o["method"]) == "initialize" && o.ContainsKey("id");

    /// <summary>본문 JSON 읽기(UTF-8 BOM 허용). 잘못된 JSON이면 <see cref="JsonException"/>.</summary>
    private static JsonNode? ParseBody(byte[] body)
    {
        ReadOnlySpan<byte> span = body;
        if (span.StartsWith("\uFEFF"u8)) span = span[3..];
        return JsonNode.Parse(span);
    }

    private static async Task WriteJsonAsync(HttpListenerResponse response, int status, JsonNode body)
    {
        var bytes = Encoding.UTF8.GetBytes(body.ToJsonString(PixelTools.JsonOptions));
        response.StatusCode = status;
        response.ContentType = "application/json; charset=utf-8";
        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
    }

    // ───────────── JSON-RPC ─────────────

    /// <summary>메시지 하나를 처리한다. 응답이 필요 없으면(알림·클라이언트 응답) null.</summary>
    private async Task<JsonObject?> ProcessAsync(JsonNode? node)
    {
        if (node is not JsonObject message) return ErrorResponse(null, -32600, "Invalid Request");
        bool hasId = message.TryGetPropertyValue("id", out var id);
        var method = AsString(message["method"]);
        if (method is null)
        {
            // 클라이언트가 보낸 응답(result/error)은 받기만 한다.
            if (message.ContainsKey("result") || message.ContainsKey("error")) return null;
            return ErrorResponse(hasId ? id : null, -32600, "Invalid Request");
        }
        if (!hasId) return null; // 알림

        var parameters = message["params"] as JsonObject;
        try
        {
            switch (method)
            {
                case "initialize":
                    return Result(id, new JsonObject
                    {
                        ["protocolVersion"] = AsString(parameters?["protocolVersion"]) ?? DefaultProtocolVersion,
                        ["capabilities"] = new JsonObject { ["tools"] = new JsonObject { ["listChanged"] = false } },
                        ["serverInfo"] = new JsonObject { ["name"] = ServerName, ["version"] = "1.0" },
                    });
                case "ping":
                    return Result(id, new JsonObject());
                case "tools/list":
                    Interlocked.Increment(ref _toolLists);
                    return Result(id, new JsonObject { ["tools"] = _tools.ListTools() });
                case "tools/call":
                    var name = AsString(parameters?["name"]);
                    if (string.IsNullOrEmpty(name)) return ErrorResponse(id, -32602, "Invalid params: tool name is required");
                    if (parameters!["arguments"] is { } argNode && argNode is not JsonObject)
                        return ErrorResponse(id, -32602, "Invalid params: arguments must be an object");
                    var arguments = parameters["arguments"] as JsonObject;
                    return Result(id, await CallToolAsync(name, arguments, null, CancellationToken.None).ConfigureAwait(false));
                default:
                    return ErrorResponse(id, -32601, $"Method not found: {method}");
            }
        }
        catch (Exception e)
        {
            return ErrorResponse(id, -32603, "Internal error: " + e.Message);
        }
    }

    /// <summary>
    /// 도구를 UI 스레드에서 실행한다(오래 걸리는 도구는 UI 스레드에서 기다리며 진행을 알림). 예외는 isError 결과로 바꾼다.
    /// 저장·열기 덮개가 떠 있으면(작업 스레드가 프로젝트를 읽는 중) 끝난 뒤에 실행한다.
    /// </summary>
    private async Task<JsonObject> CallToolAsync(string name, JsonObject? arguments, Action<string>? progress, CancellationToken ct)
    {
        try
        {
            return await Dispatcher.UIThread.InvokeAsync(async () =>
            {
                while (WorkOverlay.IsActive) await WorkOverlay.WhenIdle();
                return await _tools.CallAsync(name, arguments, progress, ct);
            });
        }
        catch (Exception e)
        {
            return new JsonObject
            {
                ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = string.Format(global::System.Globalization.CultureInfo.CurrentCulture,UiStrings.S_McpServer_CallToolAsync_526F48F779,name,e.Message) }),
                ["isError"] = true,
            };
        }
    }

    private static JsonObject Result(JsonNode? id, JsonNode result)
        => new() { ["jsonrpc"] = "2.0", ["id"] = id?.DeepClone(), ["result"] = result };

    private static JsonObject ErrorResponse(JsonNode? id, int code, string message) => new()
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id?.DeepClone(),
        ["error"] = new JsonObject { ["code"] = code, ["message"] = message },
    };

    private static string? AsString(JsonNode? n) => n is JsonValue v && v.TryGetValue(out string? s) ? s : null;
}

/// <summary>MCP 서버가 받은 요청 수(진단용): 전체 /mcp 요청, 토큰이 틀린 요청, tools/list.</summary>
public readonly record struct McpCounters(long Requests, long Unauthorized, long ToolLists);
