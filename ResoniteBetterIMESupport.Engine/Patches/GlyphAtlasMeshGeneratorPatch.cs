using Elements.Assets;
using Elements.Core;
using HarmonyLib;

namespace ResoniteBetterIMESupport.Engine.Patches;

[HarmonyPatch(typeof(GlyphAtlasMeshGenerator), nameof(GlyphAtlasMeshGenerator.Generate))]
static class GlyphAtlasMeshGeneratorPatch
{
    readonly struct LineSegment
    {
        public readonly StringLine Line;
        public readonly int EndGlyphIndex;
        public readonly RenderGlyph StartGlyph;
        public readonly RenderGlyph EndGlyph;

        public LineSegment(StringLine line, int endGlyphIndex, RenderGlyph startGlyph, RenderGlyph endGlyph)
        {
            Line = line;
            EndGlyphIndex = endGlyphIndex;
            StartGlyph = startGlyph;
            EndGlyph = endGlyph;
        }
    }

    static void Prefix(StringRenderTree renderTree, ref TextEditingVisuals textEditVisuals)
    {
        if (EngineIMEPatch.TryGetCompositionVisualCaret(renderTree.String, out var visualCaret))
            textEditVisuals.caretPosition = visualCaret;
    }

    static void Postfix(
        StringRenderTree renderTree,
        float2 offset,
        MeshX meshx,
        AtlasSubmeshMapper submeshMapper,
        TextEditingVisuals textEditVisuals,
        ref bool usesAuxiliarySubmesh)
    {
        if (!EngineIMEPatch.TryGetCompositionVisualRange(
                renderTree.String,
                out var compositionStart,
                out var compositionLength))
        {
            return;
        }

        var compositionEnd = compositionStart + compositionLength;
        var startGlyph = MatchStringPositionToGlyph(renderTree, compositionStart);
        var endGlyph = MatchStringPositionToGlyph(renderTree, compositionEnd);
        if (startGlyph < 0 || endGlyph <= startGlyph || renderTree.GlyphLayoutLength <= 0)
            return;

        var triangleSubmesh = submeshMapper(default);
        var lineSegments = Pool.BorrowList<LineSegment>();
        try
        {
            ExtractLineSegments(renderTree, startGlyph, endGlyph - startGlyph, lineSegments);
            var color = textEditVisuals.selectionColor.ToProfile(meshx.Profile);
            foreach (var segment in lineSegments)
                Underline(meshx, triangleSubmesh, renderTree, segment, color, offset);
        }
        finally
        {
            Pool.Return(ref lineSegments);
        }

        usesAuxiliarySubmesh = true;
    }

    static int MatchStringPositionToGlyph(StringRenderTree renderTree, int stringPosition)
    {
        if (stringPosition < 0)
            return stringPosition;

        for (var i = 0; i < renderTree.GlyphLayoutLength; i++)
        {
            var glyph = renderTree.GetRenderGlyph(i);
            if (glyph.stringIndex == stringPosition)
                return i;

            if (glyph.stringIndex > stringPosition)
                return i - 1;
        }

        return renderTree.GlyphLayoutLength;
    }

    static void ExtractLineSegments(StringRenderTree renderTree, int startIndex, int length, List<LineSegment> lineSegments)
    {
        var lineIndex = -1;
        var startGlyph = default(RenderGlyph);
        var endGlyph = default(RenderGlyph);

        for (var i = 0; i < length + 1; i++)
        {
            var isEnd = i == length;
            var glyphIndex = startIndex + i;
            var glyph = default(RenderGlyph);
            if (!isEnd)
                glyph = renderTree.GetRenderGlyph(MathX.Min(glyphIndex, renderTree.GlyphLayoutLength - 1));

            if (glyph.line != lineIndex || isEnd)
            {
                if (lineIndex >= 0)
                    lineSegments.Add(new LineSegment(renderTree.GetLine(lineIndex), glyphIndex, startGlyph, endGlyph));

                if (!isEnd)
                {
                    startGlyph = glyph;
                    lineIndex = glyph.line;
                }
            }

            endGlyph = glyph;
        }
    }

    static void Underline(
        MeshX mesh,
        TriangleSubmesh submesh,
        StringRenderTree renderTree,
        LineSegment segment,
        color color,
        float2 offset)
    {
        var height = MathX.Max(segment.Line.ActualHeight, segment.Line.LineHeight);
        var y = 0f - segment.Line.Position.y - 0.1f * height;
        var startPoint = new float2(segment.StartGlyph.rect.xmin + segment.Line.Position.x, y);
        var endPoint = new float2(segment.EndGlyph.pen.x + segment.Line.Position.x, y);
        var nextGlyphIndex = segment.EndGlyphIndex + 1;
        if (nextGlyphIndex < renderTree.GlyphLayoutLength)
        {
            var nextGlyph = renderTree.GetRenderGlyph(nextGlyphIndex);
            if (nextGlyph.line == segment.EndGlyph.line)
                endPoint = new float2(nextGlyph.rect.xmin + segment.Line.Position.x, endPoint.y);
        }

        InsertLine(mesh, submesh, startPoint, endPoint, color, 0.075f * height, offset);
    }

    static void InsertLine(
        MeshX mesh,
        TriangleSubmesh submesh,
        float2 startPoint,
        float2 endPoint,
        color color,
        float thickness,
        float2 offset)
    {
        mesh.IncreaseVertexCount(4);
        var index = mesh.VertexCount - 4;
        var y = thickness * 0.5f;

        mesh.RawPositions[index] = startPoint - new float2(0f, y) + offset;
        mesh.RawPositions[index + 1] = startPoint + new float2(0f, y) + offset;
        mesh.RawPositions[index + 2] = endPoint + new float2(0f, y) + offset;
        mesh.RawPositions[index + 3] = endPoint - new float2(0f, y) + offset;

        mesh.RawUV0s[index] = new float2(0f, 0f);
        mesh.RawUV0s[index + 1] = new float2(0f, 1f);
        mesh.RawUV0s[index + 2] = new float2(1f, 1f);
        mesh.RawUV0s[index + 3] = new float2(1f);

        for (var i = 0; i < 4; i++)
        {
            var vertex = index + i;
            mesh.RawColors[vertex] = color;
            mesh.RawNormals[vertex] = new float3(0f, 0f, 0f);
        }

        submesh.AddQuadAsTriangles(index, index + 1, index + 2, index + 3);
    }
}
