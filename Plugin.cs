using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using BepInEx;
using GorillaTagScripts;
using HarmonyLib;
using UnityEngine;

namespace IHateGhosts
{
    [BepInPlugin(Id, "I Hate Ghosts", "1.0.0")]
    public sealed class Plugin : BaseUnityPlugin
    {
        public const string Id = "com.ihateghosts.gorillatag";
        private static Plugin instance;
        private readonly List<HiddenGhost> ghosts = new List<HiddenGhost>();
        private Harmony harmony;

        private void OnEnable()
        {
            instance = this;
            harmony = new Harmony(Id);
            try
            {
                // Patch before discovering existing instances, so later spawns are covered too.
                harmony.PatchAll(typeof(Plugin).Assembly);
                foreach (LurkerGhost ghost in Resources.FindObjectsOfTypeAll<LurkerGhost>())
                {
                    // Do not modify prefab assets. Start handles future instantiated ghosts.
                    if (ghost != null && ghost.gameObject.scene.IsValid())
                        Hide(ghost, (AudioSource)AccessTools.Field(typeof(LurkerGhost), "audioSource").GetValue(ghost));
                }
                Logger.LogInfo("I Hate Ghosts enabled. Halloween Lurker visuals, sound, and possession effects are suppressed locally.");
            }
            catch (Exception error)
            {
                // A changed game method must not leave a partially applied mod running.
                Restore();
                Logger.LogError("I Hate Ghosts could not activate (" + error.GetType().Name + "). The game may need a compatible mod update.");
                enabled = false;
            }
        }

        private void OnDisable()
        {
            Restore();
        }

        private void Restore()
        {
            if (harmony != null)
            {
                harmony.UnpatchSelf();
                harmony = null;
            }
            foreach (HiddenGhost ghost in ghosts)
                ghost.Restore();
            ghosts.Clear();
            if (instance == this)
                instance = null;
        }

        private void Hide(LurkerGhost ghost, AudioSource audio)
        {
            for (int i = ghosts.Count - 1; i >= 0; i--)
            {
                if (ghosts[i].Ghost == null)
                    ghosts.RemoveAt(i);
                else if (ghosts[i].Ghost == ghost)
                    return;
            }
            ghosts.Add(new HiddenGhost(ghost, audio));

            // Also clear an encounter already in progress if the plugin is loaded late.
            var hauntedRig = (VRRig)AccessTools.Field(typeof(LurkerGhost), "lastHauntedVRRig").GetValue(ghost);
            if (hauntedRig != null)
                hauntedRig.IsHaunted = false;
            Shader.SetGlobalFloat("_BlackAndWhite", 0f);
        }

        private sealed class HiddenGhost
        {
            internal readonly LurkerGhost Ghost;
            private readonly MeshRenderer body;
            private readonly MeshRenderer bones;
            private readonly AudioSource audio;
            private readonly bool bodyWasHidden;
            private readonly bool bonesWereHidden;
            private readonly bool audioWasMuted;

            internal HiddenGhost(LurkerGhost ghost, AudioSource source)
            {
                Ghost = ghost;
                body = ghost.meshRenderer;
                bones = ghost.bonesMeshRenderer;
                audio = source;
                if (body != null)
                {
                    bodyWasHidden = body.forceRenderingOff;
                    body.forceRenderingOff = true;
                }
                if (bones != null)
                {
                    bonesWereHidden = bones.forceRenderingOff;
                    bones.forceRenderingOff = true;
                }
                if (audio != null)
                {
                    audioWasMuted = audio.mute;
                    audio.Stop();
                    audio.mute = true;
                }
            }

            internal void Restore()
            {
                if (body != null) body.forceRenderingOff = bodyWasHidden;
                if (bones != null) bones.forceRenderingOff = bonesWereHidden;
                if (audio != null) audio.mute = audioWasMuted;
            }
        }

        [HarmonyPatch(typeof(LurkerGhost), "Start")]
        private static class SpawnPatch
        {
            private static void Prefix(LurkerGhost __instance, AudioSource ___audioSource)
            {
                if (instance != null)
                    instance.Hide(__instance, ___audioSource);
            }
        }

        [HarmonyPatch(typeof(LurkerGhost), "UpdateGhostVisibility")]
        private static class VisibilityPatch
        {
            // The two renderers are hidden once. Skip redundant material updates each frame.
            private static bool Prefix() => false;
        }

        [HarmonyPatch(typeof(LurkerGhost), "PlaySound")]
        private static class SoundPatch
        {
            private static bool Prefix() => false;
        }

        [HarmonyPatch(typeof(LurkerGhost), "ChangeState")]
        internal static class PossessionPatch
        {
            internal static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
            {
                var result = new List<CodeInstruction>(instructions);
                var vibration = AccessTools.Method(typeof(GorillaTagger), "StartVibration", new[] { typeof(bool), typeof(float), typeof(float) });
                var shader = AccessTools.Method(typeof(Shader), "SetGlobalFloat", new[] { typeof(int), typeof(float) });
                var haunted = AccessTools.Field(typeof(VRRig), "IsHaunted");
                int vibrations = 0, tints = 0, hauntings = 0;

                foreach (CodeInstruction instruction in result)
                {
                    if (instruction.Calls(vibration))
                    {
                        ReplaceWithCall(instruction, nameof(IgnoreVibration));
                        vibrations++;
                    }
                    else if (instruction.Calls(shader))
                    {
                        ReplaceWithCall(instruction, nameof(IgnoreTint));
                        tints++;
                    }
                    else if (instruction.opcode == OpCodes.Stfld && Equals(instruction.operand, haunted))
                    {
                        ReplaceWithCall(instruction, nameof(RemoveHaunting));
                        hauntings++;
                    }
                }

                // These are the presentation operations in the supported game build.
                // Preserve all state transitions, targeting, ownership, and serialization.
                if (vibrations != 2 || tints != 1 || hauntings != 2)
                    throw new NotSupportedException("The Lurker possession method has changed.");
                return result;
            }

            private static void ReplaceWithCall(CodeInstruction instruction, string method)
            {
                // Mutate in place to retain branch labels and exception-block metadata.
                instruction.opcode = OpCodes.Call;
                instruction.operand = AccessTools.Method(typeof(PossessionPatch), method);
            }

            private static void IgnoreVibration(GorillaTagger tagger, bool left, float strength, float duration) { }
            private static void IgnoreTint(int property, float value) { }
            private static void RemoveHaunting(VRRig rig, bool value)
            {
                if (rig != null)
                    rig.IsHaunted = false;
            }
        }
    }
}
