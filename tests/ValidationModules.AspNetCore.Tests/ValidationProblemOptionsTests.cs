using ValidationModules.AspNetCore;
using Xunit;

namespace ValidationModules.AspNetCore.Tests;

/// <summary>
/// The <c>type</c> member follows <c>StatusCode</c> unless the author pinned it. Before this link
/// existed, an options object set to 422 still pointed every body at the RFC 9110 definition of
/// 400 - a document that contradicts itself.
/// </summary>
public class ValidationProblemOptionsTests {

    [Fact]
    public void DefaultType_IsTheBadRequestSection() {
        Assert.Equal("https://tools.ietf.org/html/rfc9110#section-15.5.1", new ValidationProblemOptions().Type);
    }

    [Fact]
    public void Type_FollowsTheStatusCode() {
        var options = new ValidationProblemOptions { StatusCode = 422 };

        Assert.Equal("https://tools.ietf.org/html/rfc9110#section-15.5.21", options.Type);
    }

    [Fact]
    public void ExplicitType_OutlivesAStatusChange() {
        var options = new ValidationProblemOptions {
            Type = "https://example.test/problems/order",
            StatusCode = 422,
        };

        Assert.Equal("https://example.test/problems/order", options.Type);
    }

    /// <summary>
    /// RFC 9457 defines <c>about:blank</c> as "the problem is the status code". For a status
    /// outside the RFC 9110 table that is the only link that cannot be wrong.
    /// </summary>
    [Fact]
    public void UnknownStatus_FallsBackToAboutBlank() {
        Assert.Equal("about:blank", new ValidationProblemOptions { StatusCode = 499 }.Type);
    }

    [Fact]
    public void ToProblemDetails_CarriesTheDerivedType() {
        var collector = new ValidationErrorCollector();
        var context = new ValidationContext(collector);
        context.ReportRequired("name");

        var problem = ValidationProblem.ToProblemDetails(
            collector.ToResult(), new ValidationProblemOptions { StatusCode = 422 });

        Assert.Equal(422, problem.Status);
        Assert.Equal("https://tools.ietf.org/html/rfc9110#section-15.5.21", problem.Type);
    }
}
