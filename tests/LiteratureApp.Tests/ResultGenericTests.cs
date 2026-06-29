using FluentAssertions;
using LiteratureApp.Core.Results;

namespace LiteratureApp.Tests;

public sealed class ResultGenericTests
{
    [Fact]
    public void Success_result_can_return_value()
    {
        var result = Result<string>.Success("source text");

        result.IsSuccess.Should().BeTrue();
        result.IsFailure.Should().BeFalse();
        result.Value.Should().Be("source text");
        result.ErrorCode.Should().BeNull();
        result.ErrorMessage.Should().BeNull();
    }

    [Fact]
    public void Failure_result_contains_error_code_and_message()
    {
        var result = Result<int>.Failure(AppErrorCodes.ValidationFailed, "The page number is invalid.");

        result.IsSuccess.Should().BeFalse();
        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(AppErrorCodes.ValidationFailed);
        result.ErrorMessage.Should().Be("The page number is invalid.");
    }

    [Fact]
    public void Failure_result_throws_when_value_is_accessed()
    {
        var result = Result<int>.Failure(AppErrorCodes.NotFound, "The requested page was not found.");

        Action action = () => _ = result.Value;

        action.Should().Throw<InvalidOperationException>()
            .WithMessage("Cannot access Value for a failed result.");
    }
}
