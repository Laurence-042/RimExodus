using System;
using HarmonyLib;
using UnityEngine;
using Verse;

namespace RimExodus
{
    /// <summary>
    /// Draw-only translation for effects owned and simulated by a neighbor map. Source-map ownership is
    /// preserved; this context changes only the final matrices submitted to the current composite view.
    /// </summary>
    internal static class SeamlessCompositeDrawContext
    {
        // Intentionally process-wide rather than thread-local: vanilla FleckSystemBase may build DrawBatch
        // entries on ThreadPool workers, and FleckManagerDraw waits for those workers before this scope exits.
        // Map/component drawing itself is sequential, so a tightly scoped additive stack is sufficient.
        private static Vector3 offset;
        private static int depth;

        internal static bool Active => depth > 0;
        internal static Vector3 Offset => offset;

        internal static Scope Push(Vector3 drawOffset)
        {
            depth++;
            offset += drawOffset;
            return new Scope(drawOffset);
        }

        internal readonly struct Scope : IDisposable
        {
            private readonly Vector3 drawOffset;

            internal Scope(Vector3 drawOffset)
            {
                this.drawOffset = drawOffset;
            }

            public void Dispose()
            {
                offset -= drawOffset;
                depth--;
            }
        }
    }

    [HarmonyPatch(typeof(DrawBatch), nameof(DrawBatch.DrawMesh),
        new[] { typeof(Mesh), typeof(Matrix4x4), typeof(Material), typeof(int), typeof(Color?), typeof(bool), typeof(DrawBatchPropertyBlock) })]
    public static class Patch_DrawBatch_DrawMeshColored_SeamlessView
    {
        public static void Prefix(ref Matrix4x4 matrix)
        {
            if (SeamlessCompositeDrawContext.Active)
                matrix = Matrix4x4.Translate(SeamlessCompositeDrawContext.Offset) * matrix;
        }
    }

    [HarmonyPatch(typeof(DrawBatch), nameof(DrawBatch.DrawMesh),
        new[] { typeof(Mesh), typeof(Matrix4x4), typeof(Material), typeof(int), typeof(bool) })]
    public static class Patch_DrawBatch_DrawMesh_SeamlessView
    {
        public static void Prefix(ref Matrix4x4 matrix)
        {
            if (SeamlessCompositeDrawContext.Active)
                matrix = Matrix4x4.Translate(SeamlessCompositeDrawContext.Offset) * matrix;
        }
    }

    [HarmonyPatch(typeof(Mote), "DrawMote")]
    public static class Patch_Mote_DrawMote_SeamlessView
    {
        public struct State
        {
            public bool active;
            public Vector3 position;
        }

        public static void Prefix(Mote __instance, out State __state)
        {
            __state = default;
            if (!SeamlessCompositeDrawContext.Active) return;
            __state.active = true;
            __state.position = __instance.exactPosition;
            __instance.exactPosition += SeamlessCompositeDrawContext.Offset;
        }

        public static Exception Finalizer(Mote __instance, State __state, Exception __exception)
        {
            if (__state.active) __instance.exactPosition = __state.position;
            return __exception;
        }
    }
}
