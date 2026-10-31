using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.Loader;
using HarmonyLib;
using RimWorld;
using Verse;

string game = Environment.GetEnvironmentVariable("RIMWORLD_DIR") ?? @"D:\SteamLibrary\steamapps\common\RimWorld";
AssemblyLoadContext.Default.Resolving += (_, name) => File.Exists(Path.Combine(game, name.Name + ".dll"))
    ? AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(game, name.Name + ".dll")) : null;
AssemblyLoadContext.Default.Resolving += (_, name) => name.Name == "0Harmony"
    ? AssemblyLoadContext.Default.LoadFromAssemblyPath(Environment.GetEnvironmentVariable("HARMONY_PATH")
        ?? @"D:\SteamLibrary\steamapps\workshop\content\294100\2009463077\1.7\Assemblies\0Harmony.dll") : null;
// The game host is already loaded during real startup; the offline console must load it explicitly.
AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(game, "RimWorld.dll"));
Audit.Run(args.Contains("preview-drift"), args.Contains("ref-backend"));

public static class Audit
{
    public static void BestAttackTarget(int value) { }
    public static void BestAttackTarget(string value) { }
    public static bool Global, OnThread, ThrowGetter;
    public static bool ReadGlobal() => ThrowGetter ? throw new InvalidOperationException("Injected getter failure") : Global;
    public static bool ReadThread() => OnThread;
    public static void Run(bool drift, bool shared)
    {
        int checks = 0;
        void Check(bool ok, string label) { checks++; if (!ok) throw new Exception(label); }
        var assembly = typeof(RimExodus.RimExodusMod).Assembly;
        const BindingFlags statics = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        var preview = assembly.GetType("RimExodus.SeamlessMapPreviewCompat", true)!;
        if (!drift) CheckCastGateComposition(shared, Check);
        bool Read(string name) => (bool)preview.GetProperty(name, statics)!.GetValue(null)!;
        Check(!Read("IsPreviewInFlight") && !Read("IsGeneratingPreviewOnCurrentThread"), "absent MapPreview is inert");
        var harmony = new Harmony("RE.Regression.Compat");
        try
        {
            foreach (string name in new[] { "SeamlessPerspectiveShiftCompat", "SeamlessVehiclesCompat", "SeamlessVMFTargetingCompat", "SeamlessVMFDisembarkDiagnostics", "SeamlessVEFCompat", "SeamlessDMSCompat", "SeamlessGLRiverCompat", "SeamlessSurveillanceCompat" })
            {
                assembly.GetType("RimExodus." + name, true)!.GetMethod("Register", statics)!.Invoke(null, new object[] { harmony });
                Check(!harmony.GetPatchedMethods().Any(), "absent adapter is inert: " + name);
            }
            var refPreview = typeof(RimExodus.Maps.MapModule).Assembly.GetType("RimExodus.Maps.MapPreviewGenerationPatch", true)!;
            void RegisterPreview() => refPreview.GetMethod("Register", statics)!.Invoke(null, new object[] { harmony });
            RegisterPreview();
            Check(!harmony.GetPatchedMethods().Any(), "absent preview installs no hook");
            var target = LoadPreviewFixture(drift);
            Global = OnThread = true;
            if (drift)
            {
                Check(!Read("IsPreviewInFlight"), "signature drift is contained");
                Check(!Read("IsGeneratingPreviewOnCurrentThread"), "drift remains disabled");
                Console.WriteLine($"{checks} MapPreview signature-drift checks passed (synthetic API fixture).");
                return;
            }
            Check(Read("IsPreviewInFlight") && Read("IsGeneratingPreviewOnCurrentThread"), "late load resolves previous absence");
            OnThread = false;
            Check(Read("IsPreviewInFlight") && !Read("IsGeneratingPreviewOnCurrentThread"), "global and thread status stay distinct");
            Global = false; OnThread = true;
            Check(!Read("IsGeneratingPreviewOnCurrentThread"), "thread-only flag is insufficient");
            RegisterPreview(); RegisterPreview();
            var info = Harmony.GetPatchInfo(target);
            Check(info.Prefixes.Count(p => p.owner == harmony.Id) == 1 && info.Finalizers.Count(p => p.owner == harmony.Id) == 1,
                "manual preview registration retries absence and is idempotent");
            ThrowGetter = true;
            Check(!Read("IsPreviewInFlight"), "getter failure contained");
            ThrowGetter = false; Global = true;
            Check(!Read("IsPreviewInFlight"), "failed getter does not flood retries");

            // Real PatchAll, not a mirror of Harmony's discovery rules. Manual helpers must be ignored.
            if (shared)
            {
                assembly.GetType("RimExodus.SeamlessFramework", true)!.GetField("<Enabled>k__BackingField", statics)!.SetValue(null, true);
                typeof(RimExodus.Maps.MapModule).GetMethod("InstallCorePatches", statics)!.Invoke(null, new object[] { harmony });
                typeof(RimExodus.Maps.MapCombatModule).GetMethod("InstallCombatPatches", statics)!.Invoke(null, new object[] { harmony });
            }
            harmony.PatchAll(assembly);
            if (shared)
            {
                var callbacks = harmony.GetPatchedMethods().Select(Harmony.GetPatchInfo).SelectMany(p =>
                    p.Prefixes.Concat(p.Postfixes).Concat(p.Transpilers).Concat(p.Finalizers))
                    .Where(p => p.owner == harmony.Id).Select(p => p.PatchMethod.DeclaringType).ToHashSet();
                Check(!assembly.GetTypes().Where(t => t.GetMethod("UseLegacyBackend", statics) != null).Any(callbacks.Contains),
                    "REF mode excludes legacy combat callbacks");
                Check(callbacks.Any(t => t!.GetCustomAttributes(false).Any(a => a.GetType().Name == "CombatPatchAttribute")),
                    "REF mode installs shared combat callbacks");
            }
            Check(!harmony.GetPatchedMethods().Select(Harmony.GetPatchInfo).SelectMany(p =>
                p.Prefixes.Concat(p.Postfixes).Concat(p.Transpilers).Concat(p.Finalizers))
                .Any(p => p.owner == harmony.Id && p.PatchMethod.DeclaringType != refPreview
                    && !Attribute.IsDefined(p.PatchMethod.DeclaringType!, typeof(HarmonyPatch))), "automatic scan excludes unannotated helpers");
            typeof(RimExodus.RimExodusMod).GetMethod("RegisterManualPatches", statics)!.Invoke(null, new object[] { harmony });
            foreach (string name in new[] { "FindRoadExitCell", "ApplyDistanceField", "RefinePath", "GetMapEdgeNodes" })
                Check(harmony.GetPatchedMethods().Any(m => m.Name == name), "manual hook bound: " + name);
            Check(harmony.GetPatchedMethods().Count(m => m.Name == "TryFindRandomEdgeCellWith") == 2, "both manual edge overloads bound");
            var binder = assembly.GetType("RimExodus.SeamlessPatchBinding", true)!.GetMethod("TryBind", statics)!;
            bool Attempt(Func<MethodBase> lookup) => (bool)binder.Invoke(null, new object[] {
                harmony, "injected missing target", lookup, typeof(Audit), nameof(ReadThread), false })!;
            Check(!Attempt(() => null!), "missing manual target contained");
            Check(!Attempt(() => throw new AmbiguousMatchException("Injected lookup drift")), "lookup failure contained before Patch");
            CheckVmfTargetingHook(assembly, harmony, Check);
            foreach (var adapter in new[] { assembly.GetType("RimExodus.SeamlessCombatExtendedCompat", true)!,
                typeof(RimExodus.Maps.MapModule).Assembly.GetType("RimExodus.Maps.CombatExtendedBridge", true)! })
            {
                var ready = adapter.GetField("initialized", statics)!;
                object previous = ready.GetValue(null)!;
                int before = harmony.GetPatchedMethods().Count();
                try
                {
                    ready.SetValue(null, true);
                    // Ambiguous finder lookup throws before TryPatch. Execute the real deferred guard.
                    adapter.GetMethod("RegisterDeferredHooks", statics)!.Invoke(null, new object[] { harmony, null!, null!, typeof(Audit) });
                    Check(harmony.GetPatchedMethods().Count() == before, "deferred CE signature failure contained: " + adapter.Name);
                }
                finally { ready.SetValue(null, previous); }
            }
            int gameMethods = harmony.GetPatchedMethods().Count(m => m.Module.Assembly != target.Module.Assembly);
            Console.WriteLine($"{checks} compatibility checks passed ({(shared ? "REF" : "legacy")}); automatic/manual hooks bound {gameMethods} game methods plus one preview fixture. No game launched.");
        }
        finally { harmony.UnpatchAll(harmony.Id); }
    }

    private static void CheckVmfTargetingHook(Assembly assembly, Harmony harmony, Action<bool, string> check)
    {
        var module = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("VMF.TargetingFixture"),
            AssemblyBuilderAccess.Run).DefineDynamicModule("fixture");
        var type = module.DefineType("VehicleMapFramework.GenUIOnVehicle", TypeAttributes.Public);
        var method = type.DefineMethod("TargetsAtMouse", MethodAttributes.Public | MethodAttributes.Static,
            typeof(IEnumerable<LocalTargetInfo>), new[] { typeof(TargetingParameters), typeof(bool), typeof(ITargetingSource) });
        var il = method.GetILGenerator();
        il.Emit(OpCodes.Ldnull); il.Emit(OpCodes.Ret);
        var target = type.CreateType()!.GetMethod("TargetsAtMouse")!;
        module.DefineType("VehicleMapFramework.JobDriverAcrossMaps", TypeAttributes.Public).CreateType();
        check(AccessTools.TypeByName("VehicleMapFramework.GenUIOnVehicle") != null,
            "late VMF targeting fixture resolves by type name");
        check(AccessTools.Method(AccessTools.TypeByName("VehicleMapFramework.GenUIOnVehicle"),
            "TargetsAtMouse", new[] { typeof(TargetingParameters), typeof(bool), typeof(ITargetingSource) }) == target,
            "VMF alternate Targeter signature resolves");
        var adapter = assembly.GetType("RimExodus.SeamlessVMFTargetingCompat", true)!;
        adapter.GetMethod("Register", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, new object[] { harmony });
        var callback = adapter.GetMethod("Prefix", BindingFlags.Static | BindingFlags.Public)!;
        check(Harmony.GetPatchInfo(target)?.Prefixes.Any(p => p.owner == harmony.Id && p.PatchMethod == callback) == true,
            "VMF alternate Targeter entry binds when its API is present");
        harmony.Unpatch(target, callback);

        var diagnostics = assembly.GetType("RimExodus.SeamlessVMFDisembarkDiagnostics", true)!;
        diagnostics.GetMethod("Register", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, new object[] { harmony });
        var genSpawn = AccessTools.Method(typeof(GenSpawn), nameof(GenSpawn.Spawn),
            new[] { typeof(Thing), typeof(IntVec3), typeof(Map), typeof(Rot4),
                typeof(WipeMode), typeof(bool), typeof(bool) })!;
        var recovery = AccessTools.Method(typeof(Verse.AI.Pawn_PathFollower),
            nameof(Verse.AI.Pawn_PathFollower.TryRecoverFromUnwalkablePosition), new[] { typeof(bool) })!;
        var flags = BindingFlags.Static | BindingFlags.NonPublic;
        var before = diagnostics.GetMethod("SpawnPrefix", flags)!;
        var after = diagnostics.GetMethod("SpawnPostfix", flags)!;
        var recover = diagnostics.GetMethod("RecoveryPrefix", flags)!;
        check(Harmony.GetPatchInfo(genSpawn)?.Prefixes.Any(p => p.owner == harmony.Id && p.PatchMethod == before) == true
            && Harmony.GetPatchInfo(genSpawn)?.Postfixes.Any(p => p.owner == harmony.Id && p.PatchMethod == after) == true
            && Harmony.GetPatchInfo(recovery)?.Prefixes.Any(p => p.owner == harmony.Id && p.PatchMethod == recover) == true,
            "VMF disembark diagnostic hooks bind when its API is present");
        harmony.Unpatch(genSpawn, before);
        harmony.Unpatch(genSpawn, after);
        harmony.Unpatch(recovery, recover);
    }

    private static void CheckCastGateComposition(bool shared, Action<bool, string> check)
    {
        var owner = shared ? typeof(RimExodus.Maps.MapModule).Assembly : typeof(RimExodus.RimExodusMod).Assembly;
        var name = shared ? "RimExodus.Maps.Patch_Verb_LaunchProjectile_TryCastShot"
            : "RimExodus.Patch_Verb_LaunchProjectile_TryCastShot";
        var transpiler = owner.GetType(name, true)!.GetMethod("Transpiler", BindingFlags.Public | BindingFlags.Static)!;
        var original = AccessTools.Method(typeof(Verb_LaunchProjectile), "TryCastShot", Type.EmptyTypes)!;
        var getThing = AccessTools.PropertyGetter(typeof(LocalTargetInfo), nameof(LocalTargetInfo.Thing));
        var getMap = AccessTools.PropertyGetter(typeof(Thing), nameof(Thing.Map));
        var module = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("VMF.CastGateFixture"), AssemblyBuilderAccess.Run)
            .DefineDynamicModule("fixture");
        var type = module.DefineType("VehicleMapFramework.VehicleMapUtility", TypeAttributes.Public);
        var baseMap = type.DefineMethod("BaseMap", MethodAttributes.Public | MethodAttributes.Static,
            typeof(Map), new[] { typeof(Thing) });
        var il = baseMap.GetILGenerator();
        il.Emit(OpCodes.Ldnull); il.Emit(OpCodes.Ret);
        var vmfMap = type.CreateType()!.GetMethod("BaseMap")!;
        List<CodeInstruction> Original() => PatchProcessor.GetOriginalInstructions(original);
        List<CodeInstruction> Apply(List<CodeInstruction> codes)
            => ((IEnumerable<CodeInstruction>)transpiler.Invoke(null, new object[] { codes })!).ToList();
        int TargetRead(List<CodeInstruction> codes)
        {
            for (int i = 0; i + 1 < codes.Count; i++)
                if (codes[i].Calls(getThing)
                    && (codes[i + 1].Calls(getMap) || codes[i + 1].Calls(vmfMap))) return i;
            return -1;
        }
        void FoldToVmf(List<CodeInstruction> codes)
        {
            int at = TargetRead(codes);
            check(at >= 0, "original target-map read found");
            codes[at + 1].opcode = OpCodes.Call;
            codes[at + 1].operand = vmfMap;
        }
        void CheckGate(List<CodeInstruction> codes, bool vmfFirst)
        {
            int at = TargetRead(codes);
            check(at >= 0, "VMF target-map read retained");
            check(codes[at + 1].Calls(vmfMap), "VMF BaseMap retained");
            check(codes[at + 2].opcode == OpCodes.Ldarg_0
                && codes[at + 3].operand is MethodInfo gate
                && gate.Name == "TargetMapForCastGate"
                && gate.GetParameters().Select(p => p.ParameterType).SequenceEqual(new[] { typeof(Map), typeof(Verb) }),
                "cast gate receives VMF-normalized map " + (vmfFirst ? "after VMF" : "before VMF"));
        }
        var afterVmf = Original(); FoldToVmf(afterVmf); CheckGate(Apply(afterVmf), true);
        var beforeVmf = Apply(Original()); FoldToVmf(beforeVmf); CheckGate(beforeVmf, false);
    }

    private static MethodInfo LoadPreviewFixture(bool drift)
    {
        // Fixtures test loader timing and failure handling, not third-party version compatibility.
        var module = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("MapPreview.RegressionFixture"), AssemblyBuilderAccess.Run).DefineDynamicModule("fixture");
        var request = module.DefineType("MapPreview.MapPreviewRequest", TypeAttributes.Public).CreateType()!;
        var result = module.DefineType("MapPreview.MapPreviewResult", TypeAttributes.Public).CreateType()!;
        void Property(TypeBuilder type, string name, MethodInfo getter)
        {
            var returnType = getter == null ? typeof(string) : typeof(bool);
            var property = type.DefineProperty(name, PropertyAttributes.None, returnType, Type.EmptyTypes);
            var method = type.DefineMethod("get_" + name, MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.SpecialName, returnType, Type.EmptyTypes);
            var il = method.GetILGenerator();
            if (getter == null) il.Emit(OpCodes.Ldstr, "changed signature"); else il.Emit(OpCodes.Call, getter);
            il.Emit(OpCodes.Ret); property.SetGetMethod(method);
        }
        var api = module.DefineType("MapPreview.MapPreviewAPI", TypeAttributes.Public);
        Property(api, "IsGeneratingPreview", drift ? null : typeof(Audit).GetMethod(nameof(ReadGlobal)));
        api.CreateType();
        var generator = module.DefineType("MapPreview.MapPreviewGenerator", TypeAttributes.Public);
        Property(generator, "IsGeneratingOnCurrentThread", typeof(Audit).GetMethod(nameof(ReadThread)));
        var generate = generator.DefineMethod("GeneratePreview", MethodAttributes.Public | MethodAttributes.Static, typeof(void), new[] { request, result });
        generate.GetILGenerator().Emit(OpCodes.Ret);
        return generator.CreateType()!.GetMethod("GeneratePreview")!;
    }
}
