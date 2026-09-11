using FluentAssertions;
using Patchouli.Infrastructure.Ocr.NdlLite;

namespace Patchouli.Tests;

public sealed class NdlLiteRecognitionCascadeTests
{
    [Fact]
    public void Cascade_thresholds_and_predicted_character_counts_match_the_official_ocr_py()
    {
        NdlLiteRecognitionCascade.Tier30OverflowThreshold.Should().Be(25);
        NdlLiteRecognitionCascade.Tier50OverflowThreshold.Should().Be(45);
        NdlLiteRecognitionCascade.Tier100SplitThreshold.Should().Be(98);
        NdlLiteRecognitionCascade.Tier30PredictedCharCount.Should().Be(3.0f);
        NdlLiteRecognitionCascade.Tier50PredictedCharCount.Should().Be(2.0f);
    }

    [Theory]
    [InlineData(3.0f, true, 30)]
    [InlineData(2.0f, true, 50)]
    [InlineData(1.0f, true, 100)]
    [InlineData(100.0f, true, 100)]
    [InlineData(3.0f, false, 100)]
    [InlineData(2.0f, false, 100)]
    public void SelectInitialTier_matches_pred_char_cnt_routing(float predictedCharCount, bool isCascade,
        int expectedTier)
    {
        NdlLiteRecognitionCascade.SelectInitialTier(predictedCharCount, isCascade).Should().Be(expectedTier);
    }

    [Fact]
    public void Default_worker_count_matches_the_python_thread_pool_executor_default_bounds()
    {
        int workers = NdlLiteRecognitionCascade.DefaultWorkerCount;

        workers.Should().BeGreaterThanOrEqualTo(1);
        workers.Should().BeLessThanOrEqualTo(32);
        workers.Should().Be(Math.Max(1, Math.Min(32, Environment.ProcessorCount + 4)));
    }

    [Fact]
    public void RunParallel_surfaces_the_worker_failure_instead_of_an_aggregate_exception()
    {
        // Parallel.For wraps worker failures in AggregateException; the cascade must
        // rethrow the original so the adapter can map it to a typed OCR failure
        // rather than letting OcrQueueScheduler report a generic worker_crashed.
        Action run = () => NdlLiteRecognitionCascade.RunParallel(8, 4,
            _ => throw new InvalidOperationException("decode failed"));

        run.Should().Throw<InvalidOperationException>().WithMessage("decode failed");
    }

    [Fact]
    public void RunParallel_does_nothing_for_an_empty_stage()
    {
        Action run = () => NdlLiteRecognitionCascade.RunParallel(0, 4, _ => throw new InvalidOperationException());

        run.Should().NotThrow();
    }
}
