using System.Net;
using System.Net.Sockets;
using System.Text;
using Ikd.Compiler;
using Ikd.Runtime;

namespace Ikd.Tests;

/// <summary>std.http：用本机极简 HTTP/1.1 服务器验证，不依赖外网。</summary>
public static class HttpTests
{
    // ---------------- 测试基础设施 ----------------

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } =
            System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                "ikd-http-" + Guid.NewGuid().ToString("N"));
        public TempDir() => Directory.CreateDirectory(Path);
        public void Dispose()
        {
            try { Directory.Delete(Path, true); } catch { /* 忽略 */ }
        }
    }

    /// <summary>编译并运行源码；返回 (stdout+stderr 归一化, 退出码)。</summary>
    private static (string Stdout, int Code) Run(string source)
    {
        using var dir = new TempDir();
        string file = System.IO.Path.Combine(dir.Path, "main.ikd");
        File.WriteAllText(file, source, new UTF8Encoding(false));

        var result = Compilation.CompileFile(file);
        if (!result.Success)
        {
            var sb = new StringBuilder();
            foreach (var d in result.Diagnostics.Items)
                sb.AppendLine(d.ToString());
            return (Normalize(sb.ToString()), -1);
        }

        var oldOut = Console.Out;
        var oldErr = Console.Error;
        var buf = new StringWriter();
        var err = new StringWriter();
        Console.SetOut(buf);
        Console.SetError(err);
        try
        {
            var vm = new Vm(result.Image!);
            int code = vm.Run(Array.Empty<string>());
            return (Normalize(buf.ToString() + err.ToString()), code);
        }
        finally
        {
            Console.SetOut(oldOut);
            Console.SetError(oldErr);
        }
    }

    private static string Normalize(string s) => s.Replace("\r\n", "\n").Replace("\r", "\n");

    private static void AssertOut(string expected, string source)
    {
        var (stdout, code) = Run(source);
        Assert.Equal(0, code, "退出码非 0，输出:\n" + stdout);
        Assert.Equal(expected, stdout);
    }

    /// <summary>把常量片段嵌进源码（引号转义）。</summary>
    private static string Lit(string s)
        => "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    // ---------------- 用例 ----------------

    [Test]
    public static void GetReturnsStatusBodyAndHeaders()
    {
        using var srv = new MiniHttpServer { Body = "hello ikd" };
        AssertOut(
            "200\n" +
            "true\n" +
            "hello ikd\n" +
            "\n" +
            "\n" +
            "text/plain; charset=utf-8\n" +
            "ikd\n" +
            "GET\n",
            "import \"std.http\";\n" +
            "fn main(args) {\n" +
            "    let r = http.request(\"GET\", " + Lit(srv.Url) + ");\n" +
            "    println(toStr(r.get(\"status\")));\n" +
            "    println(toStr(r.get(\"ok\")));\n" +
            "    println(r.get(\"body\"));\n" +
            "    println(r.get(\"error\"));\n" +
            "    println(r.get(\"errorType\"));\n" +
            "    println(r.get(\"headers\").get(\"content-type\"));\n" +
            "    println(r.get(\"headers\").get(\"x-mini\"));\n" +
            "    println(r.get(\"method\"));\n" +
            "    return 0;\n}");
    }

    [Test]
    public static void PostSendsBodyAndHeaders()
    {
        using var srv = new MiniHttpServer
        {
            Status = 201,
            Body = "{\"ok\":1}",
            ContentType = "application/json",
        };
        var (stdout, code) = Run(
            "import \"std.http\";\n" +
            "fn main(args) {\n" +
            "    let r = http.request(\"POST\", " + Lit(srv.Url) + ", " +
            "    \"{\\\"a\\\":1}\", {\"Content-Type\": \"application/json\", \"X-Token\": \"t1\"});\n" +
            "    println(toStr(r.get(\"status\")));\n" +
            "    println(r.get(\"body\"));\n" +
            "    println(r.get(\"headers\").get(\"content-type\"));\n" +
            "    println(r.get(\"error\"));\n" +
            "    return 0;\n}");
        Assert.Equal(0, code, stdout);
        Assert.Equal("201\n{\"ok\":1}\napplication/json\n\n", stdout);

        string raw = srv.LastRequest;
        Assert.True(raw.Contains("POST ", StringComparison.OrdinalIgnoreCase), "应是 POST: " + raw);
        Assert.True(raw.Contains("{\"a\":1}", StringComparison.Ordinal), "应带上请求体: " + raw);
        Assert.True(raw.IndexOf("Content-Type: application/json", StringComparison.OrdinalIgnoreCase) >= 0,
            "应带上 Content-Type: " + raw);
        Assert.True(raw.IndexOf("X-Token: t1", StringComparison.OrdinalIgnoreCase) >= 0,
            "应带上自定义头: " + raw);
        Assert.True(!raw.Contains("text/plain", StringComparison.OrdinalIgnoreCase),
            "Content-Type 应被覆盖而非追加: " + raw);
    }

    [Test]
    public static void HttpStatus404IsNotAnException()
    {
        using var srv = new MiniHttpServer { Status = 404, Body = "missing" };
        AssertOut("404\nfalse\nmissing\n\n\n",
            "import \"std.http\";\n" +
            "fn main(args) {\n" +
            "    let r = http.request(\"GET\", " + Lit(srv.Url) + ");\n" +
            "    println(toStr(r.get(\"status\")));\n" +
            "    println(toStr(r.get(\"ok\")));\n" +
            "    println(r.get(\"body\"));\n" +
            "    println(r.get(\"error\"));\n" +
            "    println(r.get(\"errorType\"));\n" +
            "    return 0;\n}");
    }

    [Test]
    public static void ConnectionRefusedReturnsErrorInfo()
    {
        int port = FreePort();
        var (stdout, code) = Run(
            "import \"std.http\";\n" +
            "fn main(args) {\n" +
            "    let r = http.request(\"GET\", \"http://127.0.0.1:" + port + "/\");\n" +
            "    println(toStr(r.get(\"status\")));\n" +
            "    println(toStr(r.get(\"ok\")));\n" +
            "    println(toStr(r.get(\"error\")) == \"\");\n" +
            "    println(toStr(r.get(\"errorType\")) == \"\");\n" +
            "    println(toStr(r.get(\"body\")) == \"\");\n" +
            "    return 0;\n}");
        Assert.Equal(0, code, "连接失败应作为返回值而非异常:\n" + stdout);
        Assert.Equal("0\nfalse\nfalse\nfalse\ntrue\n", stdout);
    }

    [Test]
    public static void InvalidUrlSchemeReportedInReturn()
    {
        var (stdout, code) = Run(
            "import \"std.http\";\n" +
            "fn main(args) {\n" +
            "    let r = http.request(\"GET\", \"ftp://example.com/\");\n" +
            "    println(toStr(r.get(\"status\")));\n" +
            "    println(r.get(\"errorType\"));\n" +
            "    println(toStr(r.get(\"error\")));\n" +
            "    return 0;\n}");
        Assert.Equal(0, code, stdout);
        Assert.True(stdout.StartsWith("0\nValueError\n", StringComparison.Ordinal),
            "协议错误应进 error 字段: " + stdout);
        Assert.True(stdout.Contains("http://", StringComparison.Ordinal), stdout);
    }

    [Test]
    public static void InvalidMethodReportedInReturn()
    {
        var (stdout, code) = Run(
            "import \"std.http\";\n" +
            "fn main(args) {\n" +
            "    let r = http.request(\"BAD METHOD\", \"http://127.0.0.1:1/\");\n" +
            "    println(toStr(r.get(\"status\")));\n" +
            "    println(r.get(\"errorType\"));\n" +
            "    return 0;\n}");
        Assert.Equal(0, code, stdout);
        Assert.Equal("0\nValueError\n", stdout);
    }

    [Test]
    public static void TimeoutReportedAsErrorInfo()
    {
        using var srv = new MiniHttpServer { DelayMs = 3000, Body = "slow" };
        var (stdout, code) = Run(
            "import \"std.http\";\n" +
            "fn main(args) {\n" +
            "    let r = http.request(\"GET\", " + Lit(srv.Url) + ", null, null, 1);\n" +
            "    println(toStr(r.get(\"status\")));\n" +
            "    println(r.get(\"errorType\"));\n" +
            "    println(toStr(r.get(\"body\")) == \"\");\n" +
            "    return 0;\n}");
        Assert.Equal(0, code, "超时应作为返回值而非异常:\n" + stdout);
        Assert.Equal("0\nTimeoutError\ntrue\n", stdout);
    }

    [Test]
    public static void WrongArgumentCountThrows()
    {
        var (stdout, code) = Run(
            "import \"std.http\";\n" +
            "fn main(args) { return http.request(\"GET\"); }");
        Assert.True(code != 0, "参数个数错误应报运行时错误: " + stdout);
        Assert.True(stdout.Contains("http.request", StringComparison.Ordinal), stdout);

        var (out2, code2) = Run(
            "import \"std.http\";\n" +
            "fn main(args) {\n" +
            "    return http.request(\"GET\", \"http://127.0.0.1:1/\", null, null, 1, \"x\");\n" +
            "}");
        Assert.True(code2 != 0, "6 个参数应报运行时错误: " + out2);
        Assert.True(out2.Contains("http.request", StringComparison.Ordinal), out2);
    }

    [Test]
    public static void ImportAliasIsHttp()
        => AssertOut("Fn\n",
            "import \"std.http\";\n" +
            "fn main(args) { println(typeOf(http.request)); return 0; }");

    [Test]
    public static void RequestMethodIsUppercasedInReturn()
    {
        using var srv = new MiniHttpServer();
        var (stdout, code) = Run(
            "import \"std.http\";\n" +
            "fn main(args) {\n" +
            "    let r = http.request(\"head\", " + Lit(srv.Url) + ");\n" +
            "    println(r.get(\"method\"));\n" +
            "    return 0;\n}");
        Assert.Equal(0, code, stdout);
        Assert.Equal("HEAD\n", stdout);
    }

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }
}

/// <summary>只说 HTTP/1.1 的一次性服务器，绑定回环随机端口，不需要管理员权限。</summary>
internal sealed class MiniHttpServer : IDisposable
{
    private readonly TcpListener _listener;
    private readonly Thread _thread;
    private volatile bool _running = true;

    public int Port { get; }
    public string Url => "http://127.0.0.1:" + Port + "/test";
    public int Status { get; set; } = 200;
    public string Body { get; set; } = "";
    public string? ContentType { get; set; } = "text/plain; charset=utf-8";
    public string ExtraHeaders { get; set; } = "";
    public int DelayMs { get; set; }
    /// <summary>最近一次收到的原始请求（头 + 体）。</summary>
    public string LastRequest { get; private set; } = "";

    public MiniHttpServer()
    {
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _thread = new Thread(Loop) { IsBackground = true };
        _thread.Start();
    }

    private void Loop()
    {
        while (_running)
        {
            TcpClient client;
            try { client = _listener.AcceptTcpClient(); }
            catch { break; }
            try { Handle(client); }
            catch { /* 客户端提前断开等情况忽略 */ }
            finally { client.Dispose(); }
        }
    }

    private void Handle(TcpClient client)
    {
        using var stream = client.GetStream();
        string head = ReadHead(stream);
        if (head.Length == 0) return;

        int headerLen = head.IndexOf("\r\n\r\n", StringComparison.Ordinal) + 4;
        if (headerLen < 4) return;

        int contentLength = 0;
        foreach (var line in head.Split('\n'))
        {
            var t = line.TrimEnd('\r');
            if (t.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                int.TryParse(t[(t.IndexOf(':') + 1)..].Trim(), out contentLength);
        }

        var payload = new List<byte>(Encoding.UTF8.GetBytes(head[headerLen..]));
        var buf = new byte[8192];
        while (payload.Count < contentLength)
        {
            int n = stream.Read(buf, 0, buf.Length);
            if (n <= 0) break;
            for (int i = 0; i < n; i++) payload.Add(buf[i]);
        }
        LastRequest = head[..headerLen] + Encoding.UTF8.GetString(payload.ToArray());

        if (DelayMs > 0) Thread.Sleep(DelayMs);

        byte[] response = Encoding.UTF8.GetBytes(Body);
        var sb = new StringBuilder();
        sb.Append("HTTP/1.1 ").Append(Status).Append(' ').Append(Reason(Status)).Append("\r\n");
        if (ContentType is not null)
            sb.Append("Content-Type: ").Append(ContentType).Append("\r\n");
        sb.Append("Content-Length: ").Append(response.Length).Append("\r\n");
        sb.Append("X-Mini: ikd\r\n");
        if (ExtraHeaders.Length > 0) sb.Append(ExtraHeaders);
        sb.Append("Connection: close\r\n\r\n");

        byte[] headBytes = Encoding.UTF8.GetBytes(sb.ToString());
        stream.Write(headBytes, 0, headBytes.Length);
        stream.Write(response, 0, response.Length);
        stream.Flush();
    }

    private static string ReadHead(NetworkStream stream)
    {
        var sb = new StringBuilder();
        var buf = new byte[2048];
        while (sb.ToString().IndexOf("\r\n\r\n", StringComparison.Ordinal) < 0)
        {
            int n;
            try { n = stream.Read(buf, 0, buf.Length); }
            catch { return sb.ToString(); }
            if (n <= 0) return sb.ToString();
            sb.Append(Encoding.UTF8.GetString(buf, 0, n));
        }
        return sb.ToString();
    }

    private static string Reason(int status) => status switch
    {
        200 => "OK",
        201 => "Created",
        204 => "No Content",
        301 => "Moved Permanently",
        400 => "Bad Request",
        404 => "Not Found",
        418 => "I'm a teapot",
        500 => "Internal Server Error",
        503 => "Service Unavailable",
        _ => "Status",
    };

    public void Dispose()
    {
        _running = false;
        try { _listener.Stop(); } catch { /* 忽略 */ }
        try { _thread.Join(2000); } catch { /* 忽略 */ }
    }
}
