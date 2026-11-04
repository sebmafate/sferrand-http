using System.Net;
using System.Net.Http.Headers;

namespace SFerrand.Http.Tests;

[TestClass]
public class RetryOptionsTests
{
    private static RetryOptions Options(int baseMs = 100, int maxMs = 1000)
        => new() { BaseDelay = TimeSpan.FromMilliseconds(baseMs), MaxDelay = TimeSpan.FromMilliseconds(maxMs) };

    [TestMethod]
    [DataRow(1, 100)]
    [DataRow(2, 200)]
    [DataRow(3, 400)]
    [DataRow(4, 800)]
    [DataRow(5, 1000)]
    [DataRow(30, 1000)]
    public void Delay_DoublesEachAttempt_UpToMaxDelay(int attempt, int expectedMaxMs)
    {
        var options = Options();

        Assert.AreEqual(TimeSpan.FromMilliseconds(expectedMaxMs), options.GetDelay(attempt, random: () => 1.0));
        Assert.AreEqual(TimeSpan.FromMilliseconds(expectedMaxMs / 2.0), options.GetDelay(attempt, random: () => 0.0));
    }

    [TestMethod]
    public void Delay_JitterStaysBetweenHalfAndFull()
    {
        var options = Options();
        for (var i = 0; i < 200; i++)
        {
            var delay = options.GetDelay(3)!.Value;
            Assert.IsGreaterThanOrEqualTo(200.0, delay.TotalMilliseconds);
            Assert.IsLessThanOrEqualTo(400.0, delay.TotalMilliseconds);
        }
    }

    [TestMethod]
    public void RetryAfterDelta_IsHonoured()
    {
        var retryAfter = new RetryConditionHeaderValue(TimeSpan.FromMilliseconds(750));

        Assert.AreEqual(TimeSpan.FromMilliseconds(750), Options().GetDelay(1, retryAfter));
    }

    [TestMethod]
    public void RetryAfterDate_IsHonoured()
    {
        var now = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        var retryAfter = new RetryConditionHeaderValue(now.AddMilliseconds(600));

        Assert.AreEqual(TimeSpan.FromMilliseconds(600), Options().GetDelay(1, retryAfter, now: now));
    }

    [TestMethod]
    public void RetryAfterBeyondMaxDelay_GivesUp()
    {
        var retryAfter = new RetryConditionHeaderValue(TimeSpan.FromHours(1));

        Assert.IsNull(Options().GetDelay(1, retryAfter));
    }

    [TestMethod]
    public void RetryAfterInThePastOrZero_FallsBackToBackoff()
    {
        var now = DateTimeOffset.UtcNow;

        Assert.AreEqual(TimeSpan.FromMilliseconds(100), Options().GetDelay(1, new RetryConditionHeaderValue(now.AddMinutes(-5)), () => 1.0, now));
        Assert.AreEqual(TimeSpan.FromMilliseconds(100), Options().GetDelay(1, new RetryConditionHeaderValue(TimeSpan.Zero), () => 1.0));
    }

    [TestMethod]
    [DataRow("GET", true)]
    [DataRow("HEAD", true)]
    [DataRow("OPTIONS", true)]
    [DataRow("PUT", true)]
    [DataRow("DELETE", true)]
    [DataRow("POST", false)]
    [DataRow("PATCH", false)]
    public void OnlyIdempotentMethodsAreEligibleByDefault(string method, bool expected)
        => Assert.AreEqual(expected, new RetryOptions().IsEligible(new HttpMethod(method)));

    [TestMethod]
    public void NonIdempotentMethods_CanBeOptedIn()
    {
        var options = new RetryOptions { RetryNonIdempotentMethods = true };

        Assert.IsTrue(options.IsEligible(HttpMethod.Post));
        Assert.IsTrue(options.IsEligible(HttpMethod.Patch));
    }

    [TestMethod]
    public void DefaultStatusCodes_AreTheTransientOnes()
    {
        var codes = new RetryOptions().RetryStatusCodes;

        foreach (var code in new[] { 408, 429, 500, 502, 503, 504 })
            Assert.Contains((HttpStatusCode)code, codes);
        Assert.DoesNotContain(HttpStatusCode.NotFound, codes);
        Assert.DoesNotContain(HttpStatusCode.BadRequest, codes);
    }
}
