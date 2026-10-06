# ikd 语言规范（v0.1）

文件扩展名 `.ikd`。类 C 大括号风格；类型写在名字后面：`name: Type`。

## 注释与空白

```ikd
// 行注释
/* 块注释 */
```

语句末尾分号可选。

## 类型

| 类型 | 含义 |
|---|---|
| `Int` | 64 位整数 |
| `Float` | 双精度浮点 |
| `Str` | 字符串 |
| `Bool` | `true` / `false` |
| `Null` | `null` |
| `Any` | 任意值 |
| `Void` | 无返回值 |
| `List<T>` | 列表 |
| `Map<K, V>` | 映射 |
| `Fn` | 函数值 |

字符串支持插值：`"你好 ${name}"`。`+` 可拼接字符串。

## 变量与函数

```ikd
let x = 1          // 绑定后不可再赋给这个名字（字段语义见类）
var y: Int = 2     // 可变
const K = 3

fn add(a: Int, b: Int): Int {
    return a + b
}

fn greet(name) {          // 省略返回类型视为 Void
    println("hi " + name)
}
```

入口为 `fn main(args)`。`args` 是 `List<Str>`。`main` 若返回 `Int`，作为进程退出码。

## 闭包

```ikd
let f = |x| x * 2
let g = fn (a) { return a + 1 }
```

闭包按引用捕获外层变量。

## 类、继承、接口

单继承、多接口。默认成员私有，`pub` 公开。支持 `static`、`abstract`、`override`。

```ikd
interface Shape {
    fn area(): Float
}

abstract class Animal {
    let name: Str
    init(name) { this.name = name }
    abstract fn speak(): Str
    fn greet(): Str { return this.name + ": " + this.speak() }
}

class Dog : Animal, Shape {
    init(name) { super.init(name) }
    override fn speak(): Str { return "汪" }
    fn area(): Float { return 1.0 }
}
```

- 构造：`Dog("旺财")` 或 `new Dog("旺财")`
- 当前实例：`this`；基类：`super.init(...)`、`super.method()`
- 静态：`ClassName.member`

## 枚举与 match

`match` 是表达式，支持字面量、`is Type`、枚举解构、`when` 守卫、`_`。

分支体既可以是表达式，也可以是**值块** `{ ... }`（块的值是最后一条表达式语句；若最后一条不是表达式则为 `null`）：

```ikd
enum Op { Add(Int), Div(Int) }

fn ev(op, a, b) {
    return match op {
        Op.Add(_) => a + b,
        Op.Div(_) => {
            if (b == 0) { throw "div by zero" }
            a / b
        },
    }
}
```

> **注意**：`=>` 后的 `{` 一律表示值块。要在分支里放映射字面量请加括号：`_ => ({"v": 1})`。

## 控制流

```ikd
if (x > 0) { ... } else { ... }
while (ok) { ... }
for x in xs { ... }
loop { break }
return expr
```

三元：`cond ? a : b`。

### if 表达式

写在**表达式位置**（赋值、返回、实参、match 分支等）时，`if` 是表达式，且必须有 `else`：

```ikd
let s = if (n > 0) "正" else "非正"

fn classify(n) {
    return if (n > 0) "正" else if (n == 0) "零" else "负"
}

println(toStr(if (ok) 1 else 0))

let v = if (c) { let t = 41; t + 1 } else { 0 }   // 分支可用值块
```

作为**语句开头**的 `if` 走语句形式，分支必须是块：`if (c) { ... } else { ... }`。

## 异常

```ikd
try { throw "boom" }
catch e { println(toStr(e)) }
finally { }
```

## 模块

```ikd
import "std.io"
import "./util.ikd" as util
```

用户模块路径相对当前文件；可省略 `.ikd`。被导入模块里需 `pub` 才能被外面用。默认别名是文件名（去掉后缀）。

## 泛型

编译期检查、运行期类型擦除。

```ikd
class Box<T> {
    let value: T
    init(value) { this.value = value }
    fn get(): T { return this.value }
}

fn id<T>(x: T): T { return x }
```

## 运算符

算术 `+ - * / %`，比较 `== != < <= > >=`，逻辑 `&& || !` 以及 `and or not`，空合并 `??`，可选成员 `?.`，复合赋值 `+= -= *= /= %=`。

## 集合字面量

```ikd
let xs = [1, 2, 3]
let m = {"a": 1, "b": 2}
xs[0]
m["a"]
```

也可以用构造函数创建：`List()`、`List(1, 2, 3)`、`Map()`、`Map("a", 1, "b", 2)`（键值成对，奇数个参数报运行时错误）。

常用方法：列表 `push pop insert remove get set contains`，映射 `get set has remove keys values`，字符串 `len contains`。

## 宏（macro）

宏在编译期把名字替换成它的体，不产生函数对象，也没有调用帧。只有两种形式：

```ikd
macro PI = 3.14                      // 常量宏
macro MAX(a, b) { if (a > b) { a } else { b } }   // 函数式宏
```

规则：

- 只能定义在**模块顶层**（放进函数或块里报 `E2011`），模块内可见；所有宏在编译最前面统一登记，因此顶层函数体可以引用文本位置更靠后的宏。
- 函数宏的实参**只求值一次**（先存进临时变量，宏体里按形参名读取），与函数调用一致；参数个数不符报 `E3004`。
- 宏体在调用处原地展开，其中的 `return` / `break` / `continue` 作用于**调用者**的函数与循环。
- 宏体内的同名局部变量会遮蔽同名形参。
- 宏名优先于全局名与类型名；重复定义、或与已有变量 / 类型重名报 `E3008`。
- 常量宏被当成函数调用、函数宏被当成值使用，报 `E3003`；宏在展开中引用 / 调用自身报 `E3037`。

```ikd
macro SQUARE(x) { x * x }
println(toStr(SQUARE(4)))               // 16

macro DONE(n) { if (n > 3) { break } }  // 宏体里的 break 作用于调用者的 for
for i in range(0, 10) {
    DONE(i)
    println(toStr(SQUARE(i)))
}
```

## 预置函数

全局可用（不必 import）：`print`、`println`、`readLine`、`toStr`、`toInt`、`toFloat`、`len`、`sizeof`、`typeOf`、`range`、`assert`、`List`、`Map`。

`sizeof(值)` 返回近似内存占用（字节）：`Null` 0、`Bool` 1、`Int` / `Float` 8、`Str` 16 + 2 × 字符数、`List` 16 + 16 × 元素数、`Map` 16 + 48 × 条目数、实例 16 + 16 × 字段数、闭包 48。写成 `sizeof(Int)`、`sizeof(Str)` 这类内置类型名时在编译期折叠成该类型的基础大小。

## 标准库模块

`import "std.xxx"` 引入，别名默认是最后一段：`import "std.http"` → `http`（可用 `as` 改名）。

| 模块 | 成员 |
|---|---|
| `std.io` | `print` `println` `readLine` |
| `std.math` | `abs` `absf` `min` `max` `minf` `maxf` `sqrt` `pow` `floor` `ceil` `round` `sin` `cos` `tan` `log` `exp` `pi` `e` `random` |
| `std.str` | `len` `upper` `lower` `trim` `split` `contains` `join` |
| `std.list` | `new` `of` |
| `std.map` | `new` |
| `std.errors` | `runtimeError` `valueError` `typeError` |
| `std.http` | `request` |

### std.http

```ikd
http.request(method, url)
http.request(method, url, body)
http.request(method, url, body, headers)
http.request(method, url, body, headers, timeoutSeconds)
```

| 参数 | 类型 | 说明 |
|---|---|---|
| `method` | `Str` | `GET` / `POST` / `HEAD` …，大小写不敏感 |
| `url` | `Str` | 必须以 `http://` 或 `https://` 开头 |
| `body` | `Str` 或 `null` | 请求体，省略或 `null` 表示无 |
| `headers` | `Map` | 自定义请求头；`Content-Type` 覆盖默认值 |
| `timeoutSeconds` | `Int` | 1~600，缺省 30 |

返回 `Map`：

| 键 | 类型 | 说明 |
|---|---|---|
| `status` | `Int` | HTTP 状态码；没拿到响应时为 `0` |
| `ok` | `Bool` | `200 <= status < 300` |
| `body` | `Str` | 响应体，失败时 `""` |
| `headers` | `Map` | 响应头，**键一律小写** |
| `error` | `Str` | **catch 到的错误信息**，成功时 `""` |
| `errorType` | `Str` | 错误类别：`HttpRequestException`、`TimeoutError`、`ValueError` …，成功时 `""` |
| `method` `url` | `Str` | 回显请求 |

网络连不上、DNS 失败、超时、协议错误**都不抛异常**，而是写进 `error` / `errorType`、`status` 置 `0`；只有参数个数或类型不对才抛运行时错误。

```ikd
import "std.http";

let r = http.request("GET", "https://www.example.com/", null, null, 10);
if (!r.get("ok")) {
    println(r.get("errorType") + ": " + r.get("error"));
} else {
    println(toStr(r.get("status")) + "，" + toStr(len(r.get("body"))) + " 字节");
}
```

见 `examples/http.ikd`。

## 程序结构建议

一个可执行程序至少包含 `main`。用 `ikd build` 把字节码封进 `ikd.exe` 副本的尾部，得到可双击运行的用户程序。
