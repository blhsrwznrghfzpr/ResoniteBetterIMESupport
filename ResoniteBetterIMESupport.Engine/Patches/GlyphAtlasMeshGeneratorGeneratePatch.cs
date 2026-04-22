using Elements.Assets;
using Elements.Core;
using FrooxEngine;
using HarmonyLib;

namespace ResoniteBetterIMESupport.Engine.Patches;

[HarmonyPatch(typeof(GlyphAtlasMeshGenerator), nameof(GlyphAtlasMeshGenerator.Generate))]
static class GlyphAtlasMeshGeneratorGeneratePatch
{
    public readonly struct CaretVisualState
    {
        public CaretVisualState(int caretPosition, colorX caretColor)
        {
            CaretPosition = caretPosition;
            CaretColor = caretColor;
        }

        public int CaretPosition { get; }

        public colorX CaretColor { get; }

        public bool IsActive => CaretPosition >= 0;
    }

    static void Prefix(ref TextEditingVisuals textEditVisuals, out CaretVisualState __state)
    {
        if (EngineIMEPatch.TryGetCompositionCaretVisual(textEditVisuals, out var caretPosition, out var caretColor))
        {
            __state = new CaretVisualState(caretPosition, caretColor);
            textEditVisuals.caretColor = colorX.Clear;
            return;
        }

        __state = default;
    }

    static void Postfix(StringRenderTree renderTree, float2 offset, MeshX meshx, AtlasSubmeshMapper submeshMapper, CaretVisualState __state)
    {
        if (!__state.IsActive || renderTree == null)
            return;

        var glyphIndex = MatchStringPositionToGlyph(renderTree, __state.CaretPosition, out var afterGlyph);

        if (glyphIndex < 0 && renderTree.GlyphLayoutLength > 0)
            return;

        var submesh = submeshMapper(default(AtlasData));
        StringLine line;
        float x;

        if (renderTree.GlyphLayoutLength > 0)
        {
            var glyph = renderTree.GetRenderGlyph(glyphIndex);
            line = renderTree.GetLine(glyph.line);
            x = afterGlyph ? glyph.rect.xmax : glyph.rect.xmin;
        }
        else
        {
            line = renderTree.GetLine(0);
            x = 0f;
        }

        var height = line.LineHeight * line.LineHeightMultiplier * 0.7f;
        var start = new float2(x + line.Position.x, 0f - line.Position.y + height * 0.5f - line.Descender);
        InsertLine(meshx, submesh, start, start + new float2(line.LineHeight * 0.04f), __state.CaretColor.ToProfile(meshx.Profile), height, offset);
    }

    static int MatchStringPositionToGlyph(StringRenderTree renderTree, int stringPosition, out bool afterGlyph)
    {
        afterGlyph = false;

        if (stringPosition < 0)
            return stringPosition;

        if (renderTree.GlyphLayoutLength == 0)
            return 0;

        for (var i = 0; i < renderTree.GlyphLayoutLength; i++)
        {
            var glyph = renderTree.GetRenderGlyph(i);

            if (glyph.stringIndex == stringPosition)
                return i;

            if (glyph.stringIndex > stringPosition)
                return Math.Max(0, i - 1);
        }

        afterGlyph = true;
        return renderTree.GlyphLayoutLength - 1;
    }

    static void InsertLine(MeshX mesh, TriangleSubmesh submesh, float2 startPoint, float2 endPoint, color color, float thickness, float2 offset)
    {
        mesh.IncreaseVertexCount(4);
        var start = mesh.VertexCount - 4;
        var halfThickness = thickness * 0.5f;

        mesh.RawPositions[start] = startPoint - new float2(0f, halfThickness) + offset;
        mesh.RawPositions[start + 1] = startPoint + new float2(0f, halfThickness) + offset;
        mesh.RawPositions[start + 2] = endPoint + new float2(0f, halfThickness) + offset;
        mesh.RawPositions[start + 3] = endPoint - new float2(0f, halfThickness) + offset;
        mesh.RawUV0s[start] = new float2(0f, 0f);
        mesh.RawUV0s[start + 1] = new float2(0f, 1f);
        mesh.RawUV0s[start + 2] = new float2(1f, 1f);
        mesh.RawUV0s[start + 3] = new float2(1f);

        for (var i = 0; i < 4; i++)
        {
            var vertex = start + i;
            mesh.RawColors[vertex] = color;
            mesh.RawNormals[vertex] = new float3(0f, 0f, 0f);
        }

        submesh.AddQuadAsTriangles(start, start + 1, start + 2, start + 3);
    }
}
