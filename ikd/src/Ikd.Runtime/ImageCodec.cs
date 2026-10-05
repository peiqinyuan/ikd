using System.IO.Compression;

namespace Ikd.Runtime;

/// <summary>
/// ProgramImage 的二进制编解码：用于 <c>ikd build</c> 把字节码追加到 exe 尾部。
/// </summary>
public static class ImageCodec
{
    /// <summary>附加数据末尾的魔数（trailer）。</summary>
    public static readonly byte[] TrailerMagic = { (byte)'I', (byte)'K', (byte)'D', (byte)'X' };

    // ----------------------------------------------------------------
    //  序列化
    // ----------------------------------------------------------------

    public static byte[] Serialize(ProgramImage image)
    {
        using var ms = new MemoryStream();
        using var w = new BinWriter(ms);

        foreach (var b in ProgramImage.Magic) w.Byte(b);
        w.Int(image.Version);
        w.Str(image.EntryPath);
        w.Str(image.EntryName);

        w.Int(image.Interfaces.Length);
        foreach (var i in image.Interfaces)
        {
            w.Int(i.Id);
            w.Str(i.Name);
            w.Strs(i.MethodNames);
        }

        w.Strs(image.NativeNames);

        w.Int(image.Modules.Length);
        foreach (var m in image.Modules)
        {
            w.Str(m.Name);
            w.Str(m.Path);
            w.Strs(m.Imports);
            w.Int(m.Constants.Length);
            foreach (var c in m.Constants) w.WriteValue(c);
            w.Int(m.Functions.Length);
            foreach (var f in m.Functions) WriteFunction(w, f);
            w.Int(m.Classes.Length);
            foreach (var c in m.Classes)
            {
                w.Str(c.Name);
                w.Int(c.BaseIndex);
                w.Ints(c.InterfaceIds);
                w.Strs(c.FieldNames);
                w.Int(c.FieldCount);
                w.Bool(c.IsAbstract);
                w.Int(c.Methods.Length);
                foreach (var (n, fi) in c.Methods) { w.Str(n); w.Int(fi); }
                w.Int(c.StaticMethods.Length);
                foreach (var (n, fi) in c.StaticMethods) { w.Str(n); w.Int(fi); }
                w.Strs(c.StaticFieldNames);
                w.Strs(c.TypeParams);
            }
            w.Int(m.Enums.Length);
            foreach (var e in m.Enums)
            {
                w.Str(e.Name);
                w.Strs(e.CaseNames);
                w.Ints(e.CaseArities);
                w.Strs(e.TypeParams);
            }
            w.Int(m.TypeDescriptors.Length);
            foreach (var t in m.TypeDescriptors) WriteTypeDesc(w, t);
            w.Int(m.GlobalCount);
            w.Strs(m.GlobalNames);
            w.Strs(m.ExportNames);
            w.Ints(m.CellGlobalIndices);
            w.Int(m.InitFunction);
        }

        w.Flush();
        return ms.ToArray();
    }

    private static void WriteFunction(BinWriter w, IkdFunction f)
    {
        w.Str(f.Name);
        w.Int(f.Arity);
        w.Bytes(f.Code);
        w.Int(f.Constants.Length);
        foreach (var c in f.Constants) w.WriteValue(c);
        w.Int(f.LocalCount);
        w.Int(f.MaxStack);
        w.Ints(f.LineIps);
        w.Ints(f.LineNums);
        w.Int(f.Upvalues.Length);
        foreach (var u in f.Upvalues)
        {
            w.Bool(u.IsLocal);
            w.Bool(u.IsGlobal);
            w.Int(u.Index);
        }
        w.Str(f.ModulePath);
        w.Bool(f.IsMethod);
        w.Int(f.ParamNames.Length);
        foreach (var p in f.ParamNames) w.Str(p);
    }

    private static void WriteTypeDesc(BinWriter w, TypeDesc t)
    {
        w.Byte((byte)t.Kind);
        w.Int(t.ClassIndex);
        w.Int(t.InterfaceId);
        w.Int(t.EnumIndex);
        w.Int(t.Args.Length);
        foreach (var a in t.Args) WriteTypeDesc(w, a);
    }

    // ----------------------------------------------------------------
    //  反序列化
    // ----------------------------------------------------------------

    public static ProgramImage Deserialize(byte[] data)
    {
        using var ms = new MemoryStream(data, writable: false);
        using var r = new BinReader(ms);

        var magic = r.Bytes(4);
        if (magic.Length != 4 || magic[0] != ProgramImage.Magic[0] ||
            magic[1] != ProgramImage.Magic[1] || magic[2] != ProgramImage.Magic[2] ||
            magic[3] != ProgramImage.Magic[3])
            throw new IkdException("镜像魔数不匹配");

        var image = new ProgramImage { Version = r.Int() };
        image.EntryPath = r.Str();
        image.EntryName = r.Str();

        int ifaceCount = r.Int();
        var ifaces = new InterfaceImage[ifaceCount];
        for (int i = 0; i < ifaceCount; i++)
            ifaces[i] = new InterfaceImage { Id = r.Int(), Name = r.Str(), MethodNames = r.Strs() };
        image.Interfaces = ifaces;

        image.NativeNames = r.Strs();

        int modCount = r.Int();
        var mods = new ModuleImage[modCount];
        for (int mi = 0; mi < modCount; mi++)
        {
            var m = new ModuleImage();
            m.Name = r.Str();
            m.Path = r.Str();
            m.Imports = r.Strs();

            int constCount = r.Int();
            var consts = new Value[constCount];
            for (int i = 0; i < constCount; i++) consts[i] = r.ReadValue();
            m.Constants = consts;

            int fnCount = r.Int();
            var fns = new IkdFunction[fnCount];
            for (int i = 0; i < fnCount; i++) fns[i] = ReadFunction(r);
            m.Functions = fns;

            int clsCount = r.Int();
            var cls = new ClassImage[clsCount];
            for (int i = 0; i < clsCount; i++)
            {
                var c = new ClassImage
                {
                    Name = r.Str(),
                    BaseIndex = r.Int(),
                    InterfaceIds = r.Ints(),
                    FieldNames = r.Strs(),
                    FieldCount = r.Int(),
                    IsAbstract = r.Bool(),
                };
                int nm = r.Int();
                var meth = new (string, int)[nm];
                for (int k = 0; k < nm; k++) meth[k] = (r.Str(), r.Int());
                c.Methods = meth;
                int ns = r.Int();
                var smeth = new (string, int)[ns];
                for (int k = 0; k < ns; k++) smeth[k] = (r.Str(), r.Int());
                c.StaticMethods = smeth;
                c.StaticFieldNames = r.Strs();
                c.TypeParams = r.Strs();
                cls[i] = c;
            }
            m.Classes = cls;

            int enumCount = r.Int();
            var enums = new EnumImage[enumCount];
            for (int i = 0; i < enumCount; i++)
                enums[i] = new EnumImage
                {
                    Name = r.Str(),
                    CaseNames = r.Strs(),
                    CaseArities = r.Ints(),
                    TypeParams = r.Strs(),
                };
            m.Enums = enums;

            int tdCount = r.Int();
            var tds = new TypeDesc[tdCount];
            for (int i = 0; i < tdCount; i++) tds[i] = ReadTypeDesc(r);
            m.TypeDescriptors = tds;

            m.GlobalCount = r.Int();
            m.GlobalNames = r.Strs();
            m.ExportNames = r.Strs();
            m.CellGlobalIndices = r.Ints();
            m.InitFunction = r.Int();
            mods[mi] = m;
        }
        image.Modules = mods;
        return image;
    }

    private static IkdFunction ReadFunction(BinReader r)
    {
        string name = r.Str();
        int arity = r.Int();
        byte[] code = r.Bytes(r.Int());
        int cc = r.Int();
        var constants = new Value[cc];
        for (int i = 0; i < cc; i++) constants[i] = r.ReadValue();
        int localCount = r.Int();
        int maxStack = r.Int();
        int[] lineIps = r.Ints();
        int[] lineNums = r.Ints();
        int upCount = r.Int();
        var ups = new UpvalueDesc[upCount];
        for (int i = 0; i < upCount; i++)
            ups[i] = new UpvalueDesc { IsLocal = r.Bool(), IsGlobal = r.Bool(), Index = (ushort)r.Int() };
        string modulePath = r.Str();
        bool isMethod = r.Bool();
        int pc = r.Int();
        var paramNames = new string?[pc];
        for (int i = 0; i < pc; i++) paramNames[i] = r.Str();
        return new IkdFunction(name, arity, code, constants, localCount, maxStack,
            lineIps, lineNums, ups, modulePath, isMethod, paramNames);
    }

    private static TypeDesc ReadTypeDesc(BinReader r)
    {
        var t = new TypeDesc
        {
            Kind = (TypeDesc.TypeDescKind)r.Byte(),
            ClassIndex = r.Int(),
            InterfaceId = r.Int(),
            EnumIndex = r.Int(),
        };
        int n = r.Int();
        var args = new TypeDesc[n];
        for (int i = 0; i < n; i++) args[i] = ReadTypeDesc(r);
        t.Args = args;
        return t;
    }

    // ----------------------------------------------------------------
    //  压缩
    // ----------------------------------------------------------------

    public static byte[] Compress(byte[] raw)
    {
        using var ms = new MemoryStream();
        using (var gz = new DeflateStream(ms, CompressionLevel.Optimal, leaveOpen: true))
            gz.Write(raw, 0, raw.Length);
        return ms.ToArray();
    }

    public static byte[] Decompress(byte[] packed, int rawLength)
    {
        var raw = new byte[rawLength];
        using (var gz = new DeflateStream(new MemoryStream(packed), CompressionMode.Decompress))
        {
            int read = 0;
            while (read < rawLength)
            {
                int n = gz.Read(raw, read, rawLength - read);
                if (n <= 0) break;
                read += n;
            }
            if (read != rawLength)
                throw new IkdException($"镜像解压长度不符：期望 {rawLength}，实际 {read}");
        }
        return raw;
    }

    // ----------------------------------------------------------------
    //  exe 尾部读写
    // ----------------------------------------------------------------

    /// <summary>
    /// 把压缩镜像写到 <paramref name="exePath"/> 尾部。
    /// 布局：[exe][packed][u32 packedLen][u32 rawLen][magic 4B]。
    /// </summary>
    public static void AppendToExe(string exePath, ProgramImage image, byte[] exeBytes)
    {
        byte[] raw = Serialize(image);
        byte[] packed = Compress(raw);

        using var ms = new MemoryStream();
        ms.Write(exeBytes, 0, exeBytes.Length);
        ms.Write(packed, 0, packed.Length);
        WriteU32(ms, (uint)packed.Length);
        WriteU32(ms, (uint)raw.Length);
        ms.Write(TrailerMagic, 0, TrailerMagic.Length);

        var tmp = exePath + ".tmp";
        File.WriteAllBytes(tmp, ms.ToArray());
        if (File.Exists(exePath)) File.Delete(exePath);
        File.Move(tmp, exePath);
    }

    /// <summary>尝试从自身 exe 尾部读出镜像；没有则返回 false。</summary>
    public static bool TryReadFromExe(string exePath, out ProgramImage image)
    {
        image = null!;
        try
        {
            var fi = new FileInfo(exePath);
            if (!fi.Exists || fi.Length < 12) return false;

            using var fs = File.OpenRead(exePath);
            fs.Seek(-4, SeekOrigin.End);
            var magic = new byte[4];
            if (fs.Read(magic, 0, 4) != 4) return false;
            for (int i = 0; i < 4; i++)
                if (magic[i] != TrailerMagic[i]) return false;

            fs.Seek(-12, SeekOrigin.End);
            uint packedLen = ReadU32From(fs);
            uint rawLen = ReadU32From(fs);
            if (packedLen == 0 || rawLen == 0 ||
                packedLen > fi.Length || rawLen > 1L << 30) return false;
            if (packedLen + 12 > (ulong)fi.Length) return false;

            fs.Seek(-(long)packedLen - 12, SeekOrigin.End);
            var packed = new byte[packedLen];
            int got = 0;
            while (got < packed.Length)
            {
                int n = fs.Read(packed, got, packed.Length - got);
                if (n <= 0) return false;
                got += n;
            }

            image = Deserialize(Decompress(packed, (int)rawLen));
            return true;
        }
        catch
        {
            image = null!;
            return false;
        }
    }

    private static void WriteU32(Stream s, uint v)
    {
        s.WriteByte((byte)v);
        s.WriteByte((byte)(v >> 8));
        s.WriteByte((byte)(v >> 16));
        s.WriteByte((byte)(v >> 24));
    }

    private static uint ReadU32From(Stream s)
    {
        var b = new byte[4];
        if (s.Read(b, 0, 4) != 4) return 0;
        return (uint)(b[0] | (b[1] << 8) | (b[2] << 16) | (b[3] << 24));
    }

    // ----------------------------------------------------------------
    //  底层读写器
    // ----------------------------------------------------------------

    private sealed class BinWriter : IDisposable
    {
        private readonly Stream _s;
        public BinWriter(Stream s) => _s = s;
        public void Dispose() => _s.Dispose();
        public void Flush() => _s.Flush();
        public void Byte(byte v) => _s.WriteByte(v);
        public void Bool(bool v) => _s.WriteByte(v ? (byte)1 : (byte)0);
        public void Int(int v)
        {
            _s.WriteByte((byte)v);
            _s.WriteByte((byte)(v >> 8));
            _s.WriteByte((byte)(v >> 16));
            _s.WriteByte((byte)(v >> 24));
        }
        public void Bytes(byte[] b)
        {
            Int(b.Length);
            _s.Write(b, 0, b.Length);
        }
        public void Str(string? s)
        {
            s ??= "";
            var b = System.Text.Encoding.UTF8.GetBytes(s);
            Int(b.Length);
            _s.Write(b, 0, b.Length);
        }
        public void Strs(string?[] arr)
        {
            Int(arr.Length);
            foreach (var s in arr) Str(s);
        }
        public void Ints(int[] arr)
        {
            Int(arr.Length);
            foreach (var v in arr) Int(v);
        }
        public void WriteValue(Value v)
        {
            Byte((byte)v.Kind);
            switch (v.Kind)
            {
                case ValueKind.Bool:
                case ValueKind.Int:
                    Long(v.AsLong);
                    break;
                case ValueKind.Float:
                    Long(BitConverter.DoubleToInt64Bits(v.AsDouble));
                    break;
                case ValueKind.Ref:
                    if (v.AsRef is IkdString s) Str(s.Value);
                    else throw new IkdException("镜像常量只支持标量与字符串");
                    break;
            }
        }
        private void Long(long v)
        {
            unchecked
            {
                _s.WriteByte((byte)v);
                _s.WriteByte((byte)(v >> 8));
                _s.WriteByte((byte)(v >> 16));
                _s.WriteByte((byte)(v >> 24));
                _s.WriteByte((byte)(v >> 32));
                _s.WriteByte((byte)(v >> 40));
                _s.WriteByte((byte)(v >> 48));
                _s.WriteByte((byte)(v >> 56));
            }
        }
    }

    private sealed class BinReader : IDisposable
    {
        private readonly Stream _s;
        public BinReader(Stream s) => _s = s;
        public void Dispose() => _s.Dispose();
        public byte Byte()
        {
            int v = _s.ReadByte();
            if (v < 0) throw new IkdException("镜像读取意外结束");
            return (byte)v;
        }
        public bool Bool() => Byte() != 0;
        public int Int()
        {
            int b0 = Byte(), b1 = Byte(), b2 = Byte(), b3 = Byte();
            return b0 | (b1 << 8) | (b2 << 16) | (b3 << 24);
        }
        private long Long()
        {
            long v = 0;
            for (int i = 0; i < 8; i++) v |= (long)Byte() << (i * 8);
            return v;
        }
        public byte[] Bytes(int len)
        {
            if (len < 0) throw new IkdException("镜像长度为负");
            var b = new byte[len];
            int got = 0;
            while (got < len)
            {
                int n = _s.Read(b, got, len - got);
                if (n <= 0) throw new IkdException("镜像读取意外结束");
                got += n;
            }
            return b;
        }
        public string Str()
        {
            int len = Int();
            if (len == 0) return "";
            return System.Text.Encoding.UTF8.GetString(Bytes(len));
        }
        public string[] Strs()
        {
            int n = Int();
            var a = new string[n];
            for (int i = 0; i < n; i++) a[i] = Str();
            return a;
        }
        public int[] Ints()
        {
            int n = Int();
            var a = new int[n];
            for (int i = 0; i < n; i++) a[i] = Int();
            return a;
        }
        public Value ReadValue()
        {
            var kind = (ValueKind)Byte();
            return kind switch
            {
                ValueKind.Null => Value.Null,
                ValueKind.Bool => Value.Of(Long() != 0),
                ValueKind.Int => Value.Of(Long()),
                ValueKind.Float => Value.Of(BitConverter.Int64BitsToDouble(Long())),
                ValueKind.Ref => Value.OfStr(Str()),
                _ => throw new IkdException($"未知常量类型 {(int)kind}"),
            };
        }
    }
}
