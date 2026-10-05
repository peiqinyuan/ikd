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

```ikd
enum Tree { Leaf(Int), Node(Tree, Tree) }

fn sum(t) {
    return match t {
        Tree.Leaf(v) => v,
        Tree.Node(l, r) => sum(l) + sum(r),
        _ => 0,
    }
}
```

## 控制流

```ikd
if (x > 0) { ... } else { ... }
while (ok) { ... }
for x in xs { ... }
loop { break }
return expr
```

三元：`cond ? a : b`。

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

## 预置函数

全局可用（不必 import）：`print`、`println`、`readLine`、`toStr`、`toInt`、`toFloat`、`len`、`typeOf`、`range`、`assert`。

标准库模块（`import "std.io"` 等）提供同类能力的命名空间形式，以及数学 / 字符串 / 列表 / 映射方法。详见运行时 `StdLib`。

## 程序结构建议

一个可执行程序至少包含 `main`。用 `ikd build` 把字节码封进 `ikd.exe` 副本的尾部，得到可双击运行的用户程序。
