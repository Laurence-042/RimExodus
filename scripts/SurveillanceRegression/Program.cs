using System.Reflection;
using System.Runtime.Loader;

string game = Environment.GetEnvironmentVariable("RIMWORLD_DIR") ?? @"D:\SteamLibrary\steamapps\common\RimWorld";
AssemblyLoadContext.Default.Resolving += (_, name) => File.Exists(Path.Combine(game, name.Name + ".dll"))
    ? AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(game, name.Name + ".dll")) : null;
AssemblyLoadContext.Default.Resolving += (_, name) => name.Name == "0Harmony"
    ? AssemblyLoadContext.Default.LoadFromAssemblyPath(Environment.GetEnvironmentVariable("HARMONY_PATH")
        ?? @"D:\SteamLibrary\steamapps\workshop\content\294100\2009463077\1.7\Assemblies\0Harmony.dll") : null;
AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(game, "RimWorld.dll"));
SurveillanceChecks.Run();
