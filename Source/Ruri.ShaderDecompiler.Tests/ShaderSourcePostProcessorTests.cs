using NUnit.Framework;
using Ruri.ShaderTools;

namespace Ruri.ShaderDecompiler.Tests;

[TestFixture]
public sealed class ShaderSourcePostProcessorTests
{
    [Test]
    public void Apply_RewritesNestedMadPatternToReadableForm()
    {
        const string source = """
float2 tangentNormal = mad(
    mad(_BendNormalUpward, float2(0.5f, 1.0f) - normalMapVector, normalMapVector),
    2.0f, -1.0f);
""";

        SourcePostProcessResult result = ShaderSourcePostProcessor.Apply(
            source,
            new SourcePostProcessContext("hlsl", "Fragment", ShaderSourceTarget.Generic),
            ShaderSourceRewriteFlags.HlslReadability);

        const string expected = """
float2 tangentNormal = (lerp(normalMapVector, float2(0.5f, 1.0f), _BendNormalUpward) * 2.0f - 1.0f);
""";
        Assert.That(result.Text, Is.EqualTo(expected));
        Assert.That(result.AppliedRules, Is.EqualTo(new[]
        {
            "HlslReadability.MadToLerp",
            "HlslReadability.MadScaleBias",
        }));
    }

    [Test]
    public void Apply_LeavesNonMatchingMadAlone()
    {
        const string source = "float value = mad(t, a - c, b);";

        SourcePostProcessResult result = ShaderSourcePostProcessor.Apply(
            source,
            new SourcePostProcessContext("hlsl", "Fragment", ShaderSourceTarget.Generic),
            ShaderSourceRewriteFlags.HlslReadability);

        Assert.That(result.Text, Is.EqualTo(source));
        Assert.That(result.AppliedRules, Is.Empty);
    }

    [Test]
    public void Apply_RewritesLoweredMadDifference_WhenBaseIsFirstArgument()
    {
        const string source = "float value = mad(t, mad(b, s, (-0.0f) - b), b);";

        SourcePostProcessResult result = ShaderSourcePostProcessor.Apply(
            source,
            new SourcePostProcessContext("hlsl", "Fragment", ShaderSourceTarget.Generic),
            ShaderSourceRewriteFlags.HlslReadability);

        Assert.That(result.Text, Is.EqualTo("float value = lerp(b, b * s, t);"));
        Assert.That(result.AppliedRules, Is.EqualTo(new[] { "HlslReadability.MadToLerp" }));
    }

    [Test]
    public void Apply_RewritesLoweredMadDifference_WhenBaseIsSecondArgument()
    {
        const string source = "float value = mad(t, mad(s, b, (-0.0f) - b), b);";

        SourcePostProcessResult result = ShaderSourcePostProcessor.Apply(
            source,
            new SourcePostProcessContext("hlsl", "Fragment", ShaderSourceTarget.Generic),
            ShaderSourceRewriteFlags.HlslReadability);

        Assert.That(result.Text, Is.EqualTo("float value = lerp(b, s * b, t);"));
        Assert.That(result.AppliedRules, Is.EqualTo(new[] { "HlslReadability.MadToLerp" }));
    }

    [Test]
    public void Apply_RewritesLoweredMadDifference_WithUnaryNegativeBase()
    {
        const string source = "float value = mad(t, mad(b, s, -b), b);";

        SourcePostProcessResult result = ShaderSourcePostProcessor.Apply(
            source,
            new SourcePostProcessContext("hlsl", "Fragment", ShaderSourceTarget.Generic),
            ShaderSourceRewriteFlags.HlslReadability);

        Assert.That(result.Text, Is.EqualTo("float value = lerp(b, b * s, t);"));
        Assert.That(result.AppliedRules, Is.EqualTo(new[] { "HlslReadability.MadToLerp" }));
    }

    [Test]
    public void Apply_DoesNotRewriteLoweredMadDifference_WhenNegativeBaseDoesNotMatch()
    {
        const string source = "float value = mad(t, mad(x, y, (-0.0f) - z), x);";

        SourcePostProcessResult result = ShaderSourcePostProcessor.Apply(
            source,
            new SourcePostProcessContext("hlsl", "Fragment", ShaderSourceTarget.Generic),
            ShaderSourceRewriteFlags.HlslReadability);

        Assert.That(result.Text, Is.EqualTo(source));
        Assert.That(result.AppliedRules, Is.Empty);
    }

    [Test]
    public void Apply_LeavesPositiveBiasMadAlone()
    {
        const string source = "float value = mad(v, 2, +1);";

        SourcePostProcessResult result = ShaderSourcePostProcessor.Apply(
            source,
            new SourcePostProcessContext("hlsl", "Fragment", ShaderSourceTarget.Generic),
            ShaderSourceRewriteFlags.HlslReadability);

        Assert.That(result.Text, Is.EqualTo(source));
        Assert.That(result.AppliedRules, Is.Empty);
    }

    [Test]
    public void Apply_LeavesGlslInputAlone()
    {
        const string source = "vec2 tangentNormal = mad(mad(bend, vec2(0.5, 1.0) - normalMapVector, normalMapVector), 2.0, -1.0);";

        SourcePostProcessResult result = ShaderSourcePostProcessor.Apply(
            source,
            new SourcePostProcessContext("glsl", "Fragment", ShaderSourceTarget.Generic),
            ShaderSourceRewriteFlags.HlslReadability);

        Assert.That(result.Text, Is.EqualTo(source));
        Assert.That(result.AppliedRules, Is.Empty);
    }

    [Test]
    public void Apply_RewritesRenderDocDeferredLightingRegressionShape()
    {
        const string source = "_702 = _695 * mad(_682, mad(_668, _676.z, (-0.0f) - _668), _668);";

        SourcePostProcessResult result = ShaderSourcePostProcessor.Apply(
            source,
            new SourcePostProcessContext("hlsl", "Fragment", ShaderSourceTarget.Generic),
            ShaderSourceRewriteFlags.HlslReadability);

        Assert.That(result.Text, Is.EqualTo("_702 = _695 * lerp(_668, _668 * _676.z, _682);"));
        Assert.That(result.AppliedRules, Is.EqualTo(new[] { "HlslReadability.MadToLerp" }));
    }

    [Test]
    public void Apply_HandlesWhitespaceInScaleBiasRewrite()
    {
        const string source = "float value = mad( x , 2.0f , -1.0f );";

        SourcePostProcessResult result = ShaderSourcePostProcessor.Apply(
            source,
            new SourcePostProcessContext("hlsl", "Vertex", ShaderSourceTarget.Generic),
            ShaderSourceRewriteFlags.HlslReadability);

        Assert.That(result.Text, Is.EqualTo("float value = (x * 2.0f - 1.0f);"));
        Assert.That(result.AppliedRules, Is.EqualTo(new[] { "HlslReadability.MadScaleBias" }));
    }

    [Test]
    public void Apply_WithNoFlagsIsNoOp()
    {
        const string source = "float value = mad(v, 2, -1);";

        SourcePostProcessResult result = ShaderSourcePostProcessor.Apply(
            source,
            new SourcePostProcessContext("hlsl", "Vertex", ShaderSourceTarget.Generic),
            ShaderSourceRewriteFlags.None);

        Assert.That(result.Text, Is.EqualTo(source));
        Assert.That(result.AppliedRules, Is.Empty);
    }
}
