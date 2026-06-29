using FluentAssertions;
using LiteratureApp.Core.Results;

namespace LiteratureApp.Tests;

public sealed class ResultTests
{
    [Fact]
    public void Success_result_has_no_error()
    {
        var result = Result.Success();

        result.IsSuccess.Should().BeTrue();
        result.IsFailure.Should().BeFalse();
        result.ErrorCode.Should().BeNull();
        result.ErrorMessage.Should().BeNull();
    }

    [Fact]
    public void Failure_result_contains_error_code_and_message()
    {
        var result = Result.Failure(AppErrorCodes.NotFound, "The requested record was not found.");

        result.IsSuccess.Should().BeFalse();
        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(AppErrorCodes.NotFound);
        result.ErrorMessage.Should().Be("The requested record was not found.");
    }
}
