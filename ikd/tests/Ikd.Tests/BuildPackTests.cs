using Ikd.Compiler;
using Ikd.Runtime;

namespace Ikd.Tests;

public static class BuildPackTests
{
    [Test]
    public static void ImageRoundTrip()
    {
        var src = "fn main(args) { println(\"hi\"); return 0; }";
        string dir = Path.Combine(Path.GetTempPath(), "ikd-pack-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string file = Path.Combine(dir, "m.ikd");
            File.WriteAllText(file, src);
            var res = Compilation.CompileFile(file);
            Assert.True(res.Success, "编译失败");

            var a = ImageCodec.Serialize(res.Image!);
            var b = ImageCodec.Deserialize(a);
            Assert.Equal(res.Image!.EntryName, b.EntryName);
            Assert.Equal(res.Image.Modules.Length, b.Modules.Length);
            Assert.Equal(res.Image.Modules[0].Functions.Length, b.Modules[0].Functions.Length);
            Assert.Equal(res.Image.Modules[0].Constants.Length, b.Modules[0].Constants.Length);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Test]
    public static void ExeTrailerRoundTrip()
    {
        var src = "fn main(args) { println(\"pack\"); return 0; }";
        string dir = Path.Combine(Path.GetTempPath(), "ikd-pack-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string file = Path.Combine(dir, "m.ikd");
            File.WriteAllText(file, src);
            var res = Compilation.CompileFile(file);
            Assert.True(res.Success, "编译失败");

            string host = Path.Combine(dir, "host.bin");
            File.WriteAllBytes(host, new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 });
            byte[] hostBytes = File.ReadAllBytes(host);

            string exe = Path.Combine(dir, "out.bin");
            ImageCodec.AppendToExe(exe, res.Image!, hostBytes);

            Assert.True(ImageCodec.TryReadFromExe(exe, out var back), "应能读出内嵌镜像");
            Assert.Equal(res.Image!.EntryName, back.EntryName);

            // 无尾部的普通文件应返回 false
            Assert.False(ImageCodec.TryReadFromExe(host, out _), "普通文件不应被识别");
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }
}