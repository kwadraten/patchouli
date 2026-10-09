using FluentAssertions;
using Patchouli.Llm;

namespace Patchouli.Tests;

/// <summary>
/// The S6 OCR adapter feeds these codes into <c>OcrRetryPolicy</c>, so classification must be complete and
/// stable: every code this library can emit is covered, and an unknown code is never retried blindly.
/// </summary>
public sealed class LlmFailureClassifierTests
{
    [Fact]
    public void Every_declared_failure_code_is_classified()
    {
        string[] codes = typeof(LlmFailureCodes).GetFields()
            .Where(field => field.IsLiteral && field.FieldType == typeof(string))
            .Select(field => (string)field.GetRawConstantValue()!)
            .OrderBy(code => code, StringComparer.Ordinal)
            .ToArray();

        codes.Should().NotBeEmpty();
        codes.Should().OnlyHaveUniqueItems();
        LlmFailureClassifier.AllCodes.Should().BeEquivalentTo(codes);
    }

    [Fact]
    public void Retryable_codes_classify_as_transient()
    {
        foreach (string code in (string[])
                 [
                     LlmFailureCodes.NetworkTimeout, LlmFailureCodes.TemporaryProviderError,
                     LlmFailureCodes.RateLimited, LlmFailureCodes.WorkerCrashed, LlmFailureCodes.Cancelled,
                     LlmFailureCodes.Interrupted
                 ])
        {
            LlmFailureClassifier.Classify(code).Should().Be(LlmFailureClassification.TransientRetryable);
            LlmFailureClassifier.IsRetryable(code).Should().BeTrue();
            LlmFailureClassifier.RequiresManualRepair(code).Should().BeFalse();
        }
    }

    [Fact]
    public void Configuration_and_content_codes_require_manual_repair()
    {
        foreach (string code in (string[])
                 [
                     LlmFailureCodes.AuthFailed, LlmFailureCodes.ModelNotFound,
                     LlmFailureCodes.BadEndpointConfig, LlmFailureCodes.ContextLengthExceeded,
                     LlmFailureCodes.ContentFiltered, LlmFailureCodes.EmptyResponse, LlmFailureCodes.InvalidPageBox
                 ])
        {
            LlmFailureClassifier.Classify(code).Should().Be(LlmFailureClassification.ManualRepairRequired);
            LlmFailureClassifier.IsRetryable(code).Should().BeFalse();
            LlmFailureClassifier.RequiresManualRepair(code).Should().BeTrue();
        }
    }

    [Fact]
    public void Ocr_failure_codes_keep_their_existing_meaning()
    {
        LlmFailureClassifier.Classify("image_too_large_for_ocr").Should()
            .Be(LlmFailureClassification.ManualRepairRequired);
        LlmFailureClassifier.Classify("renderer_timeout").Should()
            .Be(LlmFailureClassification.ManualRepairRequired);
        LlmFailureClassifier.Classify("source_file_missing").Should()
            .Be(LlmFailureClassification.ManualRepairRequired);
        LlmFailureClassifier.Classify("missing_executable").Should()
            .Be(LlmFailureClassification.ManualRepairRequired);
    }

    [Fact]
    public void Programming_and_unknown_codes_are_not_retried()
    {
        foreach (string? code in (string?[])
                 [
                     LlmFailureCodes.UnsupportedInput,
                     LlmFailureCodes.HistoryInvariantViolated, LlmFailureCodes.UsageContractViolated,
                     LlmFailureCodes.UnknownProviderError, "something-new", null, "", "   "
                 ])
        {
            LlmFailureClassifier.Classify(code).Should().Be(LlmFailureClassification.NonRetryable);
            LlmFailureClassifier.IsRetryable(code).Should().BeFalse();
        }
    }

    [Fact]
    public void Classification_ignores_surrounding_whitespace_and_case_of_unknown_codes()
    {
        LlmFailureClassifier.Classify("  rate_limited  ").Should()
            .Be(LlmFailureClassification.TransientRetryable);
        LlmFailureClassifier.Classify("RATE_LIMITED").Should().Be(LlmFailureClassification.NonRetryable);
    }

    [Fact]
    public void The_exception_carries_its_own_classification()
    {
        LlmChatException retryable = new(LlmFailureCodes.NetworkTimeout, "timeout");
        LlmChatException repairable = new(LlmFailureCodes.AuthFailed, "bad key");
        LlmChatException wrapped = LlmChatException.From(LlmFailureCodes.RateLimited, new IOException("socket"));

        retryable.IsRetryable.Should().BeTrue();
        retryable.RequiresManualRepair.Should().BeFalse();
        repairable.RequiresManualRepair.Should().BeTrue();
        repairable.IsRetryable.Should().BeFalse();
        wrapped.ErrorCode.Should().Be(LlmFailureCodes.RateLimited);
        wrapped.InnerException.Should().BeOfType<IOException>();
        wrapped.Message.Should().Be("socket");
    }
}
