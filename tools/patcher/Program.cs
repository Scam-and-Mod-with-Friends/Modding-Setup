// Patches a Scam With Your Friends install so BepInEx can load it.
//
// Two problems caused by Unity's managed stripper:
//
//   1. Awaiter types lost OnCompleted(Action) because INotifyCompletion was deleted.
//      We restore it by cloning UnsafeOnCompleted (which is identical in UniTask / BCL awaiters).
//
//   2. Other game types implemented interfaces that the stripper narrowed. Restoring the
//      unstripped corlib widens those interfaces again, so the types now fail vtable setup.
//      We add throwing stubs for the missing methods.
//
// Usage: patcher <GameDir>

using Mono.Cecil;
using Mono.Cecil.Cil;

if (args.Length < 1) { Console.Error.WriteLine("Usage: patcher <GameDir>"); return 1; }

var gameDir   = args[0];
var managed   = Path.Combine(gameDir, "Scam With Your Friends_Data", "Managed");
var overDir   = Path.Combine(gameDir, "unstripped_corlib");

if (!Directory.Exists(managed)) { Console.Error.WriteLine($"Not found: {managed}"); return 1; }
if (!Directory.Exists(overDir)) { Console.Error.WriteLine($"Not found: {overDir}"); return 1; }

var resolver = new DefaultAssemblyResolver();
resolver.AddSearchDirectory(overDir);
resolver.AddSearchDirectory(managed);
var rp = new ReaderParameters { AssemblyResolver = resolver };

UnstripAwaiters(managed, overDir, rp);
FixVtableBreaks(managed, overDir, rp);
return 0;

// ── 1. awaiter patch ────────────────────────────────────────────────────────

static void UnstripAwaiters(string managed, string overDir, ReaderParameters rp)
{
    Console.WriteLine("\n=== restoring stripped awaiter methods ===");

    const string CRITICAL = "System.Runtime.CompilerServices.ICriticalNotifyCompletion";
    const string NOTIFY   = "System.Runtime.CompilerServices.INotifyCompletion";

    var src = Path.Combine(managed, "UniTask.dll");
    if (!File.Exists(src)) { Console.WriteLine("UniTask.dll not in Managed, skipping"); return; }

    using var asm = AssemblyDefinition.ReadAssembly(src, rp);
    int patched = 0;

    foreach (var t in asm.MainModule.GetTypes())
    {
        bool impl = t.Interfaces.Any(i =>
            i.InterfaceType.FullName == CRITICAL || i.InterfaceType.FullName == NOTIFY);
        if (!impl) continue;

        bool hasOn = t.Methods.Any(m =>
            (m.Name == "OnCompleted" || m.Name.EndsWith(".OnCompleted")) && m.Parameters.Count == 1);
        if (hasOn) continue;

        var unsafe_ = t.Methods.FirstOrDefault(m =>
            (m.Name == "UnsafeOnCompleted" || m.Name.EndsWith(".UnsafeOnCompleted")) && m.Parameters.Count == 1);
        if (unsafe_ == null) { Console.WriteLine($"  warning: {t.FullName} has no UnsafeOnCompleted to clone, skipped"); continue; }

        var m2 = new MethodDefinition("OnCompleted", unsafe_.Attributes, unsafe_.ReturnType);
        foreach (var p in unsafe_.Parameters)
            m2.Parameters.Add(new ParameterDefinition(p.Name, p.Attributes, p.ParameterType));
        m2.ImplAttributes = unsafe_.ImplAttributes;
        m2.SemanticsAttributes = unsafe_.SemanticsAttributes;
        t.Methods.Add(m2);

        CloneBody(unsafe_, m2);
        Console.WriteLine($"  + {t.FullName}::OnCompleted(Action)");
        patched++;
    }

    if (patched == 0) { Console.WriteLine("  nothing to patch"); return; }

    var dest = Path.Combine(overDir, "UniTask.dll");
    asm.Write(dest);
    Console.WriteLine($"  wrote {dest} ({patched} method(s) added)");
}

static void CloneBody(MethodDefinition src, MethodDefinition dst)
{
    var sb = src.Body;
    var db = dst.Body;
    db.InitLocals = sb.InitLocals;

    foreach (var v in sb.Variables)
        db.Variables.Add(new VariableDefinition(v.VariableType));

    var il  = db.GetILProcessor();
    var map = new Dictionary<Instruction, Instruction>();

    foreach (var ins in sb.Instructions)
    {
        Instruction n = ins.Operand switch
        {
            null                    => il.Create(ins.OpCode),
            Instruction             => il.Create(ins.OpCode, sb.Instructions[0]), // fixed in pass 2
            Instruction[]           => il.Create(ins.OpCode, Array.Empty<Instruction>()),
            VariableDefinition v    => il.Create(ins.OpCode, db.Variables[v.Index]),
            ParameterDefinition p   => p.Index < 0
                                        ? il.Create(ins.OpCode, dst.Body.ThisParameter)
                                        : il.Create(ins.OpCode, dst.Parameters[p.Index]),
            MethodReference mr      => il.Create(ins.OpCode, mr),
            FieldReference fr       => il.Create(ins.OpCode, fr),
            TypeReference tr        => il.Create(ins.OpCode, tr),
            string s                => il.Create(ins.OpCode, s),
            sbyte sb2               => il.Create(ins.OpCode, sb2),
            byte b                  => il.Create(ins.OpCode, b),
            int i                   => il.Create(ins.OpCode, i),
            long l                  => il.Create(ins.OpCode, l),
            float f                 => il.Create(ins.OpCode, f),
            double d                => il.Create(ins.OpCode, d),
            _                       => throw new Exception($"Unhandled operand {ins.Operand.GetType()} in {src.FullName}")
        };
        il.Append(n);
        map[ins] = n;
    }

    // fix branch targets
    for (int i = 0; i < sb.Instructions.Count; i++)
    {
        var old = sb.Instructions[i];
        var @new = db.Instructions[i];
        if (old.Operand is Instruction br)
            @new.Operand = map[br];
        else if (old.Operand is Instruction[] brs)
            @new.Operand = brs.Select(b => map[b]).ToArray();
    }

    foreach (var h in sb.ExceptionHandlers)
    {
        var nh = new ExceptionHandler(h.HandlerType);
        if (h.TryStart     != null) nh.TryStart     = map[h.TryStart];
        if (h.TryEnd       != null) nh.TryEnd       = map[h.TryEnd];
        if (h.HandlerStart != null) nh.HandlerStart = map[h.HandlerStart];
        if (h.HandlerEnd   != null) nh.HandlerEnd   = map[h.HandlerEnd];
        if (h.FilterStart  != null) nh.FilterStart  = map[h.FilterStart];
        nh.CatchType = h.CatchType;
        db.ExceptionHandlers.Add(nh);
    }

    db.MaxStackSize = sb.MaxStackSize;
}

// ── 2. vtable patch ─────────────────────────────────────────────────────────

static void FixVtableBreaks(string managed, string overDir, ReaderParameters rp)
{
    Console.WriteLine("\n=== patching vtable breakages ===");

    // Find interfaces whose required method set grew under the unstripped corlib.
    var widened = new Dictionary<string, List<(string name, int arity)>>();
    string[] corlibs = new[] { "mscorlib", "System", "System.Core", "System.Xml", "System.Configuration" };

    foreach (var n in corlibs)
    {
        var sp = Path.Combine(managed, n + ".dll");
        var up = Path.Combine(overDir,  n + ".dll");
        if (!File.Exists(sp) || !File.Exists(up)) continue;

        using var sAsm = AssemblyDefinition.ReadAssembly(sp, rp);
        using var uAsm = AssemblyDefinition.ReadAssembly(up, rp);

        var sMap = IndexInterfaces(sAsm);
        var uMap = IndexInterfaces(uAsm);

        foreach (var (iface, _) in uMap)
        {
            if (!sMap.ContainsKey(iface)) continue;
            var sReq = RequiredMethods(iface, sMap, new HashSet<string>());
            var uReq = RequiredMethods(iface, uMap, new HashSet<string>());
            var added = uReq.Except(sReq).ToList();
            if (added.Count > 0) widened[iface] = added.Select(x => { var p = x.Split('/'); return (p[0], int.Parse(p[1])); }).ToList();
        }
    }

    if (widened.Count == 0) { Console.WriteLine("  No vtable breakages remain."); return; }

    // Scan every assembly for types that implement a widened interface but lack the new methods.
    var scan = new Dictionary<string, string>();
    foreach (var f in Directory.GetFiles(managed,  "*.dll")) scan[Path.GetFileName(f)] = f;
    foreach (var f in Directory.GetFiles(overDir,  "*.dll")) scan[Path.GetFileName(f)] = f;

    int fixedCount = 0;

    foreach (var (name, path) in scan.OrderBy(x => x.Key))
    {
        if (name == "mscorlib.dll") continue;

        AssemblyDefinition asm;
        try { asm = AssemblyDefinition.ReadAssembly(path, rp); } catch { continue; }

        var todo = new List<(TypeDefinition type, string mName, int mArity, TypeReference iface)>();

        foreach (var t in asm.MainModule.GetTypes())
        {
            if (t.IsInterface) continue;
            foreach (var i in t.Interfaces)
            {
                var ifaceName = i.InterfaceType.FullName;
                var lt = ifaceName.IndexOf('<');
                if (lt >= 0) ifaceName = ifaceName[..lt];
                if (!widened.TryGetValue(ifaceName, out var needed)) continue;

                foreach (var (mName, mArity) in needed)
                {
                    if (ProvidesMethod(t, mName, mArity)) continue;
                    Console.WriteLine($"  {t.FullName} missing {mName}/{mArity} (from {ifaceName})");
                    todo.Add((t, mName, mArity, i.InterfaceType));
                }
            }
        }

        if (todo.Count == 0) { asm.Dispose(); continue; }

        MethodReference? notImplCtor = null;

        foreach (var (type, mName, mArity, ifaceRef) in todo)
        {
            TypeDefinition? ifaceDef;
            try { ifaceDef = ifaceRef.Resolve(); } catch { ifaceDef = null; }
            if (ifaceDef == null) { Console.WriteLine($"    cannot resolve {ifaceRef.FullName}, skipped"); continue; }

            var sig = ifaceDef.Methods.FirstOrDefault(m => m.Name == mName && m.Parameters.Count == mArity);
            if (sig == null) { Console.WriteLine($"    no signature for {mName}, skipped"); continue; }

            var typeArgMap = new Dictionary<string, TypeReference>();
            if (ifaceRef is GenericInstanceType git)
            {
                var def = git.ElementType.Resolve();
                if (def != null)
                    for (int gi = 0; gi < def.GenericParameters.Count && gi < git.GenericArguments.Count; gi++)
                        typeArgMap[def.GenericParameters[gi].Name] = git.GenericArguments[gi];
            }

            var retType = ImportSubstituted(sig.ReturnType, typeArgMap, asm.MainModule);
            if (retType == null) { Console.WriteLine($"    cannot build return type for {mName}, skipped"); continue; }

            const MethodAttributes attrs =
                MethodAttributes.Public | MethodAttributes.Final |
                MethodAttributes.Virtual | MethodAttributes.HideBySig | MethodAttributes.NewSlot;

            var nm = new MethodDefinition(mName, attrs, retType);
            bool failed = false;
            foreach (var p in sig.Parameters)
            {
                var pt = ImportSubstituted(p.ParameterType, typeArgMap, asm.MainModule);
                if (pt == null) { failed = true; break; }
                nm.Parameters.Add(new ParameterDefinition(p.Name, p.Attributes, pt));
            }
            if (failed) { Console.WriteLine($"    cannot build parameters for {mName}, skipped"); continue; }

            notImplCtor ??= asm.MainModule.ImportReference(
                typeof(NotImplementedException).GetConstructor(Type.EmptyTypes));

            var ilp = nm.Body.GetILProcessor();
            ilp.Append(ilp.Create(OpCodes.Newobj, notImplCtor));
            ilp.Append(ilp.Create(OpCodes.Throw));
            nm.Body.MaxStackSize = 1;

            type.Methods.Add(nm);
            Console.WriteLine($"    + {type.FullName}::{mName} (stub)");
        }

        var dest = Path.Combine(overDir, name);
        asm.Write(dest);
        asm.Dispose();
        Console.WriteLine($"    wrote {dest}");
        fixedCount++;
    }

    Console.WriteLine(fixedCount > 0
        ? $"\n  Patched {fixedCount} assembly/assemblies."
        : "\n  No vtable breakages remain.");
}

static Dictionary<string, TypeDefinition> IndexInterfaces(AssemblyDefinition asm)
{
    var map = new Dictionary<string, TypeDefinition>();
    foreach (var t in asm.MainModule.GetTypes())
        if (t.IsInterface) map[t.FullName] = t;
    return map;
}

static HashSet<string> RequiredMethods(string name, Dictionary<string, TypeDefinition> map, HashSet<string> seen)
{
    var out_ = new HashSet<string>();
    if (!map.TryGetValue(name, out var t)) return out_;
    if (!seen.Add(name)) return out_;
    foreach (var m in t.Methods)
        out_.Add($"{m.Name}/{m.Parameters.Count}");
    foreach (var i in t.Interfaces)
        out_.UnionWith(RequiredMethods(i.InterfaceType.FullName, map, seen));
    return out_;
}

static bool ProvidesMethod(TypeDefinition type, string name, int arity)
{
    var cur = type;
    for (int guard = 0; cur != null && guard < 32; guard++)
    {
        if (cur.Methods.Any(m => m.Parameters.Count == arity && (m.Name == name || m.Name.EndsWith("." + name))))
            return true;
        if (cur.BaseType == null) return false;
        try { cur = cur.BaseType.Resolve(); } catch { return false; }
    }
    return false;
}

static TypeReference? ImportSubstituted(TypeReference typeRef, Dictionary<string, TypeReference> map, ModuleDefinition module)
{
    if (typeRef is GenericParameter gp)
        return map.TryGetValue(gp.Name, out var sub) ? module.ImportReference(sub) : null;

    if (typeRef is GenericInstanceType git)
    {
        var elem = module.ImportReference(git.ElementType);
        var result = new GenericInstanceType(elem);
        foreach (var arg in git.GenericArguments)
        {
            var s = ImportSubstituted(arg, map, module);
            if (s == null) return null;
            result.GenericArguments.Add(s);
        }
        return result;
    }

    if (typeRef is ByReferenceType brt)
    {
        var inner = ImportSubstituted(brt.ElementType, map, module);
        return inner == null ? null : new ByReferenceType(inner);
    }

    if (typeRef is ArrayType at)
    {
        var inner = ImportSubstituted(at.ElementType, map, module);
        return inner == null ? null : new ArrayType(inner, at.Rank);
    }

    try { return module.ImportReference(typeRef); } catch { return null; }
}
