using System.Text;

namespace Ikd.Cli;

internal static class NewProject
{
    public static int Run(string[] args)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine("ikd: new 需要一个目录名，例如: ikd new hello");
            return 2;
        }

        string dir = Path.GetFullPath(args[0]);
        if (Directory.Exists(dir) && Directory.EnumerateFileSystemEntries(dir).Any())
        {
            Console.Error.WriteLine($"ikd: 目录已存在且非空: {dir}");
            return 1;
        }

        Directory.CreateDirectory(dir);
        string main = Path.Combine(dir, "main.ikd");
        File.WriteAllText(main,
"""
// ikd 示例项目。运行: ikd run main.ikd
// 打包: ikd build main.ikd -o app.exe

class Greeter {
    let name: Str;
    init(name) { this.name = name; }
    fn hello(): Str { return "你好, " + this.name + "!"; }
}

fn main(args) {
    let who = len(args) > 0 ? args[0] : "ikd";
    let g = Greeter(who);
    println(g.hello());
    return 0;
}
""", new UTF8Encoding(false));

        Console.WriteLine($"已创建 {main}");
        Console.WriteLine("下一步: ikd run " + Path.Combine(dir, "main.ikd"));
        return 0;
    }
}
