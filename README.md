# ikd

一门用 C# 实现的面向对象编程语言：自研字节码 + 内置虚拟机，编译器命令为 **ikd**。源文件扩展名 `.ikd`，可把程序打包成独立 `.exe`。

## 要求

- Windows x64
- 从源码构建需要 [.NET SDK 10](https://dotnet.microsoft.com/)
- 发布后的 `dist\ikd.exe` 为自包含单文件，**不需要**安装 .NET 即可运行

## 快速上手

```text
ikd run examples\hello.ikd
ikd build examples\hello.ikd -o hello.exe
ikd repl
ikd new myapp
```

Hello：

```ikd
fn main(args) {
    println("Hello, ikd!");
    return 0;
}
```

面向对象：

```ikd
interface Speak {
    fn speak(): Str;
}

class Animal : Speak {
    let name: Str;
    init(name) { this.name = name; }
    fn speak(): Str { return "..."; }
}

class Dog : Animal {
    init(name) { super.init(name); }
    override fn speak(): Str { return "汪"; }
}

fn main(args) {
    let a: Speak = Dog("旺财");
    println(a.speak());
    return 0;
}
```

## 命令

| 命令 | 作用 |
|---|---|
| `ikd run <file.ikd> [-- args...]` | 编译并运行 |
| `ikd build <file.ikd> [-o out.exe]` | 生成独立可执行文件 |
| `ikd check <file.ikd>` | 只检查，不运行 |
| `ikd disasm <file.ikd>` | 反汇编字节码 |
| `ikd new <dir>` | 生成示例项目 |
| `ikd repl` | 交互式求值 |
| `ikd version` | 版本号 |
| `ikd help` | 帮助 |

诊断格式：`路径(行,列): 错误[E0xxx]: 说明`，并附源码行与 `^` 指示。

## 从源码构建

在 `ikd` 目录执行：

```powershell
.\build.ps1
```

会还原、测试、发布 `dist\ikd.exe`，并跑一遍 `examples`。

手动发布：

```powershell
dotnet publish src\Ikd.Cli\Ikd.Cli.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o dist
```

## 文档

- [语言规范](docs/language.md)
- [错误码](docs/errors.md)
- [字节码](docs/bytecode.md)

## 项目结构

```text
ikd/
  src/Ikd.Compiler   词法 / 语法 / 代码生成
  src/Ikd.Runtime    虚拟机 / 对象模型 / 标准库
  src/Ikd.Cli        命令行，发布为 ikd.exe
  tests/Ikd.Tests    自写测试框架
  examples/          示例程序
```

无第三方 NuGet 依赖。
