# ikd — 一门 OOP 编程语言 + 编译器（C# 实现，产物 exe）

## 0. 已确认的决策
- **架构**：自研字节码 + 内置 VM（纯 C#，零第三方 NuGet 依赖）
- **语法**：类 C 大括号风格，关键字 `class / fn / let / var / import / ...`，类型标注 `name: Type`
- **特性**：核心 OOP（类/继承/接口/封装/静态/抽象/重写）+ 泛型 + 闭包/lambda + `match` 模式匹配 + `enum`
- **工作目录**：`D:\irsin\ikd\`（当前 D:\irsin 为空，非 git 仓库）
- **工具链**：.NET SDK 10.0.401（已确认），NuGet 可联网（回退方案用）
- **语言文件扩展名**：`.ikd`；编译器命令：`ikd`
- **文档/诊断**：README 与语言规范用中文；编译错误信息中文 + 错误码（`E0xxx`）

---

## 1. 语言设计（IKD 语言 v0.1）

### 1.1 一瞥
```ikd
import "std.io"

interface Shape {
    fn area(): Float
}

class Animal {
    pub var name: Str
    var age: Int = 0

    init(name: Str) { this.name = name }

    fn speak(): Str { return "${this.name} 发出声音" }
}

class Dog : Animal, Shape {
    init(name: Str) { super.init(name) }
    override fn speak(): Str { return "${this.name}: 汪!" }
    fn area(): Float { return 1.5 }
}

let animals: List<Animal> = [Dog("旺财"), Animal("未知")]
for a in animals {
    println(a.speak())
}

let kind = match animals[0] {
    is Dog  => "狗"
    is Cat  => "猫"
    _       => "其它"
}
```

### 1.2 语法要点（规范摘要）
| 领域 | 形式 |
|---|---|
| 注释 | `// 行注释`、`/* 块注释 */` |
| 变量 | `let x = 1`（不可变）、`var y: Int = 2`（可变）、`const K = 3` |
| 函数 | `fn add(a: Int, b: Int): Int { return a + b }`；无返回类型 = `Void` |
| 闭包 | 匿名 `fn (a: Int) { ... }`；箭头简写 `\|x\| x * 2`（表达式体）；闭包是一等值 |
| 类 | `class C : Base, I1, I2 { ... }`（单继承 + 多接口） |
| 成员 | `var/let` 字段（默认有初始值或 init 赋值）、`fn` 方法、`init` 构造器、`static var/fn`、`pub` 公开（默认私有）、`override`/`abstract` |
| 访问 | `this`、`super.method()`、`ClassName.staticMember` |
| 控制流 | `if/else`、`while`、`for x in iter`、`loop`、`break`、`continue`、`return` |
| `match` | **是表达式**：`match v { 1 => "一", is Str => "字符串", Color.Red => .., [a, b] => .., _ => "其它" }`，支持 `when` 守卫 |
| 枚举 | `enum Color { Red, Green }`；带载荷 `enum Tree { Leaf(Int), Node(Tree, Tree) }`；match 解构 `Tree.Leaf(v) => v` |
| 异常 | `try { } catch e { } finally { }`、`throw expr`；`std.errors` 提供 `Error` |
| 模块 | `import "std.io"`（标准库）、`import "./util.ikd" as util`（用户模块） |
| 字符串 | `Str`、插值 `"hi ${name}"`、`+` 拼接 |
| 泛型 | `fn id<T>(x: T): T`、`class Box<T> { var v: T }`、`List<T>` / `Map<K, V>`；编译期检查、运行期类型擦除 |
| 内建类型 | `Int`(int64)、`Float`(double)、`Str`、`Bool`、`Null`、`Any`、`Void`、`List<T>`、`Map<K,V>`、函数值、类/对象 |
| 运算符 | `+ - * / %`、`== != < <= > >=`、`&& || !`、`??`(空合并，可选)、`and/or/not` 亦支持 |

### 1.3 标准库（v1）
- `std.io`：`print`、`println`、`readLine`
- `std.math`：`abs/min/max/sqrt/pow/floor/ceil/round/random`
- `std.str`：`len/substr/split/trim/upper/lower/contains/replace/startsWith/endsWith`（也可作 `Str` 方法）
- `std.list`：`push/pop/insert/remove/indexOf/slice/sort/forEach/map/filter/fold`（方法形式优先）
- `std.map`：`get/set/has/remove/keys/values/len`
- `std.errors`：`Error`、`throw` 约定

---

## 2. 技术架构

### 2.1 目录结构
```
D:\irsin\ikd\
├─ ikd.sln
├─ build.ps1                    # 一键：还原→测试→发布单文件→示例端到端
├─ README.md                    # 中文：安装、快速上手、命令
├─ docs\
│   ├─ language.md              # 语言规范（中文）
│   ├─ errors.md                # 错误码表
│   └─ bytecode.md              # 字节码与 .ikdb 文件格式
├─ src\
│   ├─ Ikd.Compiler\            # 词法/语法/语义/代码生成（无外部依赖）
│   │   ├─ Diagnostics\  (Diagnostic, DiagnosticBag, ErrorCode)
│   │   ├─ Syntax\       (Token, Lexer, Ast, Parser)
│   │   ├─ Semantics\    (TypeSystem, Symbols, Scope, Binder)
│   │   └─ CodeGen\      (OpCode, Chunk, ConstantPool, Emitter)
│   ├─ Ikd.Runtime\            # VM + 对象模型 + 标准库 + 序列化
│   │   ├─ (Value, IkdObject*, Vm, CallFrame, UpvalueCell, StdLib, ChunkSerializer)
│   └─ Ikd.Cli\                # 命令行入口 → 发布为 ikd.exe
│       └─ (Program, Commands, Repl, Packager, SourceHighlighter)
├─ tests\Ikd.Tests\             # 自写迷你测试框架（无第三方包）
└─ examples\                    # hello / shapes(继承接口) / generics / closures / match / errors / modules
```
> 三个可执行/库项目：`Ikd.Compiler`、`Ikd.Runtime`（ClassLib）、`Ikd.Cli`（Exe），全部 `net10.0`，**不引任何 NuGet 包**。

### 2.2 编译流水线
`源码 → Lexer(Token+行列) → Parser(AST, 错误恢复收集多诊断) → Binder(符号表/类层次/类型检查/访问控制/泛型推断) → Emitter(栈式字节码) → Chunk(常量池+代码+行号表) → [序列化 .ikdb | 交给 VM]`

### 2.3 VM 关键设计
- **值表示**：`struct Value { ValueKind kind; long i; double d; object? ref; }`（tagged union，避免装箱）
- **对象模型**：`.NET` 对象承载（`IkdClass`/`IkdInstance`/`IkdString`/`IkdList`/`IkdMap`/`IkdClosure`/`IkdEnumCase`…），**GC 直接复用 .NET GC，零成本**
- **虚分派**：类内 `Dictionary<string,int>` 方法槽位 + 继承链查找；字段按继承布局扁平化偏移
- **泛型**：类型擦除（slot 存 `Value`，运行期不校验，编译期已检查）；泛型函数做“按实参实例化克隆 + 擦除”统一处理
- **闭包捕获**：Binder 做逃逸分析——被 lambda 捕获的局部变量提升为堆 `Cell`（C# 语义式按引用捕获），无需 Lua 式 upvalue 链
- **异常**：VM 栈上 try 范围表 + C# `try/catch` 包裹执行循环，非局部跳出用内部信号
- **接口调用 v1**：按方法名沿类层次表查找（文档注明同名接口方法的限制）

### 2.4 CLI 命令
```
ikd run <file.ikd> [-- args...]        运行
ikd build <file.ikd> -o out.exe        编译打包成独立可执行文件
ikd check <file.ikd>                   只做语义检查（CI 用）
ikd disasm <file.ikd>                  反汇编（调试）
ikd new <dir>                          生成示例脚手架
ikd repl                               交互式
ikd version | -h                       版本 / 帮助
```
诊断输出格式：`examples\hello.ikd(3,12): 错误[E0201]: ...` + 源码行 + `^~~~` 指示 + 修复提示。

### 2.5 打包成 exe（两个层面）
1. **编译器自身**：`dotnet publish -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -o dist` → `dist\ikd.exe`（双击/任意目录可运行，无需安装 .NET）
2. **用户程序**：`ikd build app.ikd -o app.exe` = 复制宿主 `ikd.exe` + 追加 `[长度][magic][压缩字节码]` 载荷 + 启动时自读文件定位载荷直接进 VM。
   - **风险与回退**（第 13 阶段先做 10 分钟实验验证）：
     - 实验：单文件 apphost 是否要求 bundle 签名位于 EOF。若追加会破坏 → **回退 A**：追加后再把原文件尾部 24 字节（bundle offset + 签名）重新补到 EOF，宿主用自己的 trailer 定位载荷，两侧互不干扰。
     - 若仍不可行 → **回退 B**：改用 NativeAOT 发布宿主（原生 exe 追加数据绝对安全）；或 **回退 C**：`--il` 模式用 Roslyn 生成 .NET 程序集 + 复制/打补丁 apphost（需 NuGet）。

---

## 3. 实施阶段（里程碑）

| # | 阶段 | 交付物 / 验收 |
|---|---|---|
| M0 | 脚手架 | solution + 3 csproj + 空 CLI，`dotnet build` 通过，`ikd version` 输出 |
| M1 | 词法 + 诊断框架 | Lexer 覆盖全部 token/字符串插值/注释，错误码体系，单测绿 |
| M2 | AST + Parser | 全语法可解析 + 错误恢复，`ikd check` 能报语法错（带行列高亮） |
| M3 | 类型系统 + Binder | 类/继承/接口/静态/访问控制/override/abstract 检查；语义单测绿 |
| M4 | 字节码 + Emitter + VM 核心 | 能跑算术/控制流/函数调用/字符串插值；`ikd run hello.ikd` |
| M5 | 对象模型 | 类实例化、`init`、字段、虚方法重写、继承链、`static`、接口调用 |
| M6 | 闭包/lambda | 一等函数、捕获变量、作为参数传递（map/filter 示例通过） |
| M7 | 泛型 | `fn f<T>`、`class Box<T>`、`List<T>/Map<K,V>`，擦除 + 推断，单测绿 |
| M8 | enum + match | 常量/`is`/绑定/解构/`when` 守卫/`_`；match 作表达式；穷尽性警告 |
| M9 | 异常 + 标准库 | try/catch/finally/throw；`std.*` 全部落地，端到端示例通过 |
| M10 | 模块系统 | `import "std.io"` 与相对路径用户模块 + `as` 别名 |
| M11 | CLI 完善 | run/build/check/disasm/new/repl 全部可用，诊断美化 |
| M12 | **打包 exe** | apphost 追加实验 → `ikd build` 出的 exe 实跑通过（回退方案见 2.5） |
| M13 | 发布 + 文档 + 测试 | `build.ps1` 一键全绿；`dist\ikd.exe` 单文件自包含；README/docs 完整；examples 端到端 |

> 每个里程碑结束跑一次 `dotnet build` + 测试，再进下一阶段；M12 后额外做“新目录零依赖冒烟测试”（把 `ikd.exe` 拷到临时目录运行示例）。

## 4. 验收标准
1. `dist\ikd.exe`：单文件、自包含、任意路径可运行（`ikd version` / `ikd run` / `ikd repl`）
2. `examples\` 中 6+ 示例（覆盖继承接口、泛型、闭包、match/enum、异常、模块）全部 `ikd build` 出独立 exe 并运行输出正确
3. `tests\Ikd.Tests` 自写测试框架全绿（词法/语法/语义诊断/VM 行为 ≈ 数百断言）
4. 错误示例能给出中文错误码 + 行列 + 源码高亮
5. README、语言规范 `docs/language.md`、错误码表 `docs/errors.md` 齐全
