using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using GorillaTagScripts;
using HarmonyLib;

internal static class Program
{
    private static int checks;
    private static void Main(string[] args)
    {
        string[] directories = { Path.Combine(args[0], "BepInEx", "core"), Path.Combine(args[0], "Gorilla Tag_Data", "Managed"), Path.GetDirectoryName(args[1]) };
        var loaded = new Dictionary<string, Assembly>();
        AppDomain.CurrentDomain.AssemblyResolve += (sender, eventArgs) =>
        {
            string name = new AssemblyName(eventArgs.Name).Name + ".dll";
            if (loaded.TryGetValue(name, out Assembly existing)) return existing;
            foreach (string directory in directories)
            {
                string candidate = Path.Combine(directory, name);
                if (File.Exists(candidate)) return loaded[name] = Assembly.Load(File.ReadAllBytes(candidate));
            }
            return null;
        };
        try { Run(args[1]); }
        catch (Exception error) { Console.WriteLine(error); Environment.ExitCode = 1; }
    }

    private static void Check(bool ok, string label)
    {
        if (!ok) throw new Exception("FAIL: " + label);
        checks++;
        Console.WriteLine("PASS: " + label);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Run(string pluginPath)
    {
        Assembly plugin = Assembly.LoadFrom(pluginPath);
        Type root = plugin.GetType("IHateGhosts.Plugin", true);
        Type patch = root.GetNestedType("PossessionPatch", BindingFlags.NonPublic);
        MethodInfo transpiler = patch.GetMethod("Transpiler", BindingFlags.NonPublic | BindingFlags.Static);
        MethodInfo original = AccessTools.Method(typeof(LurkerGhost), "ChangeState");
        var before = PatchProcessor.GetOriginalInstructions(original);
        var snapshot = before.Select(x => new { x.opcode, x.operand, labels = x.labels.ToArray(), blocks = x.blocks.ToArray() }).ToArray();
        var after = ((IEnumerable<CodeInstruction>)transpiler.Invoke(null, new object[] { before })).ToList();
        Check(after.Count == snapshot.Length, "real game method retains its instruction count");
        int changed = 0;
        for (int i = 0; i < after.Count; i++)
        {
            if (after[i].opcode != snapshot[i].opcode || !Equals(after[i].operand, snapshot[i].operand))
            {
                changed++;
                MethodInfo replacement = after[i].operand as MethodInfo;
                if (replacement == null || replacement.DeclaringType != patch || replacement.ReturnType != typeof(void))
                    throw new Exception("Unexpected replacement.");
                Type[] expected;
                if (snapshot[i].operand is MethodInfo method)
                    expected = (method.IsStatic ? Array.Empty<Type>() : new[] { method.DeclaringType }).Concat(method.GetParameters().Select(p => p.ParameterType)).ToArray();
                else
                {
                    FieldInfo field = (FieldInfo)snapshot[i].operand;
                    expected = new[] { field.DeclaringType, field.FieldType };
                }
                if (!expected.SequenceEqual(replacement.GetParameters().Select(p => p.ParameterType)))
                    throw new Exception("Replacement stack signature mismatch.");
            }
            if (!snapshot[i].labels.SequenceEqual(after[i].labels) || !snapshot[i].blocks.SequenceEqual(after[i].blocks))
                throw new Exception("Control-flow metadata changed.");
        }
        Check(changed == 5, "only five presentation instructions change; stack signatures and control flow are preserved");

        foreach (string missing in new[] { "StartVibration", "SetGlobalFloat", "IsHaunted" })
        {
            var incompatible = PatchProcessor.GetOriginalInstructions(original);
            int at = incompatible.FindIndex(x => x.operand is MemberInfo member && member.Name == missing);
            incompatible.RemoveAt(at);
            bool rejected = false;
            try { transpiler.Invoke(null, new object[] { incompatible }); }
            catch (TargetInvocationException error) { rejected = error.InnerException is NotSupportedException; }
            Check(rejected, "changed game layout is rejected when " + missing + " is missing");
        }

        foreach (string name in new[] { "SoundPatch", "VisibilityPatch" })
        {
            MethodInfo prefix = root.GetNestedType(name, BindingFlags.NonPublic).GetMethod("Prefix", BindingFlags.NonPublic | BindingFlags.Static);
            Check(Equals(prefix.Invoke(null, null), false), name + " skips the original presentation method");
        }

        // Unity native internal calls cannot be executed in this external .NET process.
        // Validate binding against real metadata without installing runtime detours here.
        var targets = root.GetNestedTypes(BindingFlags.NonPublic)
            .SelectMany(t => t.GetCustomAttributes(typeof(HarmonyPatch), false).Cast<HarmonyPatch>()).ToArray();
        Check(targets.Length == 4 && targets.All(t => t.info.declaringType == typeof(LurkerGhost)
            && AccessTools.Method(t.info.declaringType, t.info.methodName) != null),
            "all four Harmony patch targets resolve on the installed LurkerGhost class");
        Check(targets.Select(t => t.info.methodName).OrderBy(n => n).SequenceEqual(
            new[] { "ChangeState", "PlaySound", "Start", "UpdateGhostVisibility" }),
            "patch targets are limited to initialization and presentation");
        Check(AccessTools.Field(typeof(LurkerGhost), "audioSource") != null
            && AccessTools.Field(typeof(LurkerGhost), "lastHauntedVRRig")?.FieldType == typeof(VRRig),
            "private fields used for startup suppression resolve correctly");
        Console.WriteLine("Completed " + checks + " checks using the installed game's real assemblies. No game session was launched.");
    }
}
