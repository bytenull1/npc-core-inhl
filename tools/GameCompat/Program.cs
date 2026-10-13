using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

// Mod assemblies against a game install, read as metadata: compiled references, GameInternals lookups
// and Harmony patch targets. Nothing is executed. README.md

string? gameRoot = null;
List<string> mods = [];
for (int i = 0; i < args.Length; i++)
{
    if (args[i] == "--game" && i + 1 < args.Length) gameRoot = args[++i];
    else mods.Add(args[i]);
}
gameRoot ??= FindGameRoot();
string managed = Path.Combine(gameRoot ?? "", "Isolated Inhale_Data", "Managed");
if (gameRoot == null || !Directory.Exists(managed))
{
    Console.Error.WriteLine("No game install found; pass --game <folder holding Isolated Inhale_Data>.");
    return 2;
}
string plugins = Path.Combine(gameRoot, "BepInEx", "plugins");
if (mods.Count == 0) mods.AddRange(Directory.GetFiles(plugins, "*.dll"));

ModResolver resolver = new();
foreach (string dir in resolver.GetSearchDirectories()) resolver.RemoveSearchDirectory(dir);
resolver.AddSearchDirectory(managed);
resolver.AddSearchDirectory(Path.Combine(gameRoot, "BepInEx", "core"));
resolver.AddSearchDirectory(plugins);
ReaderParameters reading = new() { AssemblyResolver = resolver };
List<AssemblyDefinition> checkedMods = mods.Select(path => AssemblyDefinition.ReadAssembly(path, reading)).ToList();
// The assemblies under check win over installed copies, so a fresh YourBuddy is checked against a fresh NPC.Core.
foreach (AssemblyDefinition mod in checkedMods) resolver.Add(mod);

Console.WriteLine($"Game: {managed}");
int missing = 0;
foreach (AssemblyDefinition mod in checkedMods)
{
    SortedSet<string> misses = new(StringComparer.Ordinal);
    ModuleDefinition module = mod.MainModule;
    foreach (AssemblyNameReference reference in module.AssemblyReferences.Where(r => !IsBcl(r.Name)))
    {
        if (Try(() => resolver.Resolve(reference)) == null) misses.Add($"assembly {reference.Name}");
    }
    int references = CheckReferences(module, misses);
    int lookups = CheckLookups(module, misses);
    int patches = CheckPatches(module, misses);
    Console.WriteLine($"{Path.GetFileName(module.FileName)}: {references} references into game and plugin assemblies, " +
                      $"{lookups} GameInternals lookups, {patches} Harmony patches");
    foreach (string miss in misses) Console.WriteLine("  missing: " + miss);
    missing += misses.Count;
}
Console.WriteLine(missing == 0 ? "Everything the mods use is present." : $"{missing} missing.");
return missing == 0 ? 0 : 1;

static string? FindGameRoot()
{
    foreach (string start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
    {
        for (DirectoryInfo? dir = new(start); dir != null; dir = dir.Parent)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "Isolated Inhale_Data", "Managed"))) return dir.FullName;
        }
    }
    return null;
}

static bool IsBcl(string name) => name is "netstandard" or "mscorlib" ||
    name.StartsWith("System", StringComparison.Ordinal) || name.StartsWith("Microsoft.", StringComparison.Ordinal);

static bool External(IMetadataScope? scope) => scope is AssemblyNameReference assembly && !IsBcl(assembly.Name);

static T? Try<T>(Func<T?> resolve) where T : class
{
    try
    {
        return resolve();
    }
    catch (AssemblyResolutionException)
    {
        return null;
    }
}

// Every type, method and field the compiled code names in a game or plugin assembly. A miss here is a
// TypeLoadException or MissingMethodException the first time that code runs in the game.
static int CheckReferences(ModuleDefinition module, ISet<string> misses)
{
    int count = 0;
    foreach (TypeReference type in module.GetTypeReferences())
    {
        if (!External(type.Scope)) continue;
        count++;
        if (Try(type.Resolve) == null) misses.Add($"type {type.FullName} ({type.Scope.Name})");
    }
    foreach (MemberReference member in module.GetMemberReferences())
    {
        if (member.DeclaringType is ArrayType) continue;
        TypeReference owner = member.DeclaringType.GetElementType();
        if (!External(owner.Scope)) continue;
        count++;
        object? found = member switch
        {
            MethodReference method => Try(method.Resolve),
            FieldReference field => Try(field.Resolve),
            _ => member,
        };
        if (found == null) misses.Add($"{(member is FieldReference ? "field" : "method")} {member.FullName} ({owner.Scope.Name})");
    }
    return count;
}

// GameInternals' Field<T>(typeof(X), "name", ...) and Method(typeof(X), "name", [types], ...), read from the IL.
// The count must equal the game log's "[internals] All N game members resolved."
static int CheckLookups(ModuleDefinition module, ISet<string> misses)
{
    int count = 0;
    foreach (TypeDefinition type in module.GetTypes().Where(t => Outermost(t).Name == "GameInternals"))
    {
        foreach (MethodDefinition method in type.Methods.Where(m => m.HasBody))
        {
            (TypeReference Type, string Name)? pending = null;
            List<TypeReference> parameters = [];
            foreach (Instruction ins in method.Body.Instructions)
            {
                if (ins.OpCode == OpCodes.Ldtoken && ins.Operand is TypeReference token && IsTypeOf(ins.Next))
                {
                    // typeof(X) then a string names the member; typeof(X) stored into an array is a parameter type.
                    if (ins.Next.Next?.OpCode == OpCodes.Ldstr)
                    {
                        pending = (token, (string)ins.Next.Next.Operand);
                        parameters.Clear();
                    }
                    else if (pending != null)
                    {
                        parameters.Add(token);
                    }
                }
                else if (ins.OpCode == OpCodes.Call && ins.Operand is MethodReference call && pending is { } target &&
                         call.DeclaringType.Name == "GameInternals" && call.Name is "Field" or "Method")
                {
                    count++;
                    string? miss = call is GenericInstanceMethod generic
                        ? FieldMiss(target.Type, target.Name, generic.GenericArguments[0])
                        : MethodMiss(target.Type, target.Name, parameters);
                    if (miss != null) misses.Add($"{miss} (GameInternals.{type.Name})");
                    pending = null;
                }
            }
        }
    }
    return count;
}

static TypeDefinition Outermost(TypeDefinition type)
{
    while (type.DeclaringType != null) type = type.DeclaringType;
    return type;
}

static bool IsTypeOf(Instruction? ins) => ins?.OpCode == OpCodes.Call && ins.Operand is MethodReference { Name: "GetTypeFromHandle" };

// The type and its bases, as Type.GetField/GetMethod with Instance|Public|NonPublic search them:
// a base class's private members are not found.
static IEnumerable<(TypeDefinition Type, bool Declared)> Chain(TypeDefinition type)
{
    for (TypeDefinition? t = type; t != null; t = t.BaseType == null ? null : Try(t.BaseType.Resolve))
    {
        yield return (t, t == type);
    }
}

static string? FieldMiss(TypeReference owner, string name, TypeReference expected)
{
    TypeDefinition? type = Try(owner.Resolve);
    if (type == null) return $"type {owner.FullName}";
    FieldDefinition? field = Chain(type)
        .SelectMany(c => c.Type.Fields.Where(f => f.Name == name && !f.IsStatic && (c.Declared || !f.IsPrivate)))
        .FirstOrDefault();
    if (field == null) return $"field {owner.FullName}.{name}";
    if (field.FieldType is GenericParameter || Assignable(expected, field.FieldType)) return null;

    return $"field {owner.FullName}.{name} is {field.FieldType.FullName}, not {expected.FullName}";
}

static bool Assignable(TypeReference to, TypeReference from)
{
    if (to.FullName == from.FullName || to.FullName == "System.Object") return true;
    if (to is ArrayType toArray && from is ArrayType fromArray) return Assignable(toArray.ElementType, fromArray.ElementType);
    TypeDefinition? type = Try(from.Resolve);
    if (type == null) return false;
    if (type.Interfaces.Any(i => i.InterfaceType.FullName == to.FullName)) return true;

    return type.BaseType != null && Assignable(to, type.BaseType);
}

static string? MethodMiss(TypeReference owner, string name, List<TypeReference> parameters)
{
    TypeDefinition? type = Try(owner.Resolve);
    if (type == null) return $"type {owner.FullName}";
    bool found = Chain(type).Any(c => c.Type.Methods.Any(m =>
        m.Name == name && !m.IsStatic && (c.Declared || !m.IsPrivate) && SameParameters(m, parameters)));

    return found ? null : $"method {owner.FullName}.{name}({string.Join(", ", parameters.Select(p => p.Name))})";
}

static bool SameParameters(MethodDefinition method, IReadOnlyList<TypeReference> types) =>
    method.Parameters.Select(p => p.ParameterType.FullName).SequenceEqual(types.Select(t => t.FullName));

// Harmony resolves [HarmonyPatch(typeof(X), "Name")] to X's one declared method of that name, and binds each
// patch parameter not starting with "__" to the original's parameter of the same name.
static int CheckPatches(ModuleDefinition module, ISet<string> misses)
{
    int count = 0;
    foreach (TypeDefinition type in module.GetTypes())
    {
        PatchTarget outer = Target(type.CustomAttributes, default);
        foreach (MethodDefinition patch in type.Methods.Where(m => m.CustomAttributes.Any(IsHarmonyPatch)))
        {
            count++;
            string label = $"patch {type.Name}.{patch.Name}";
            PatchTarget target = Target(patch.CustomAttributes, outer);
            if (target.Type == null || target.Name == null)
            {
                misses.Add($"{label}: its attributes do not name a type and method, so it cannot be checked");
                continue;
            }
            TypeDefinition? owner = Try(target.Type.Resolve);
            if (owner == null)
            {
                misses.Add($"type {target.Type.FullName} ({label})");
                continue;
            }
            List<MethodDefinition> originals = owner.Methods
                .Where(m => m.Name == target.Name && (target.Parameters == null || SameParameters(m, target.Parameters)))
                .ToList();
            if (originals.Count != 1)
            {
                misses.Add(originals.Count == 0
                    ? $"method {owner.FullName}.{target.Name} ({label})"
                    : $"method {owner.FullName}.{target.Name} is overloaded; {label} must name its parameter types");
                continue;
            }
            foreach (ParameterDefinition parameter in patch.Parameters)
            {
                string name = parameter.Name;
                if (name.StartsWith("___", StringComparison.Ordinal))
                {
                    if (Chain(owner).All(c => c.Type.Fields.All(f => f.Name != name[3..])))
                        misses.Add($"field {owner.FullName}.{name[3..]} ({label})");
                }
                else if (!name.StartsWith("__", StringComparison.Ordinal) && originals[0].Parameters.All(p => p.Name != name))
                {
                    misses.Add($"parameter '{name}' of {owner.FullName}.{target.Name} ({label})");
                }
            }
        }
    }
    return count;
}

static bool IsHarmonyPatch(CustomAttribute attribute) => attribute.AttributeType.FullName == "HarmonyLib.HarmonyPatch";

static PatchTarget Target(IEnumerable<CustomAttribute> attributes, PatchTarget inherited)
{
    PatchTarget target = inherited;
    foreach (CustomAttribute attribute in attributes.Where(IsHarmonyPatch))
    {
        foreach (CustomAttributeArgument argument in attribute.ConstructorArguments)
        {
            if (argument.Value is TypeReference type) target.Type = type;
            else if (argument.Value is string name) target.Name = name;
            else if (argument.Value is CustomAttributeArgument[] types) target.Parameters = types.Select(t => (TypeReference)t.Value).ToArray();
        }
    }
    return target;
}

internal sealed class ModResolver : DefaultAssemblyResolver
{
    internal void Add(AssemblyDefinition assembly) => RegisterAssembly(assembly);
}

internal record struct PatchTarget(TypeReference? Type, string? Name, TypeReference[]? Parameters);
