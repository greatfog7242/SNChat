using SNChat.BuildTools;

namespace SNChat.Tests;

/// <summary>
/// Running the whole suite and running one named test want different reports:
/// across a suite the failing names are the useful part, while for a single test
/// the assertion message is the entire point of having asked.
/// </summary>
public class RunTestsToolTests
{
    /// <summary>The shape VSTest prints for a failing xUnit test.</summary>
    private const string FailingRun = """
        Test run for D:\proj\bin\Debug\net8.0\Proj.Tests.dll (.NETCoreApp,Version=v8.0)
          Failed Proj.Tests.MathTests.Adds_two_numbers [3 ms]
          Error Message:
           Assert.Equal() Failure: Values differ
           Expected: 4
           Actual:   5
          Stack Trace:
             at Proj.Tests.MathTests.Adds_two_numbers() in D:\proj\MathTests.cs:line 12
             at System.RuntimeMethodHandle.InvokeMethod(Object target, Void** arguments)
             at System.Reflection.MethodBaseInvoker.InvokeWithNoArgs(Object obj)
             at Xunit.Sdk.TestInvoker.AaaInternalPlumbing()
             at Xunit.Sdk.TestInvoker.MoreInternalPlumbing()
             at Xunit.Sdk.TestRunner.EvenMorePlumbing()

        Failed!  - Failed:     1, Passed:   210, Skipped:     0, Total:   211
        """;

    private static ProcessResult Failing() =>
        new() { ExitCode = 1, Output = FailingRun };

    [Fact]
    public void A_suite_run_names_the_failing_test_without_the_assertion()
    {
        // Across a whole suite the messages would swamp the context window, and
        // the names are what the model needs to decide where to look next.
        var report = RunTestsTool.Summarize(Failing(), "Proj.Tests", detailed: false);

        Assert.Contains("Adds_two_numbers", report);
        Assert.DoesNotContain("Expected: 4", report);
    }

    [Fact]
    public void Asking_for_one_test_returns_why_it_failed()
    {
        var report = RunTestsTool.Summarize(Failing(), "Proj.Tests", detailed: true);

        Assert.Contains("Adds_two_numbers", report);
        Assert.Contains("Assert.Equal() Failure", report);
        Assert.Contains("Expected: 4", report);
        Assert.Contains("Actual:   5", report);
    }

    [Fact]
    public void The_stack_keeps_the_code_under_test_and_drops_the_runner()
    {
        // The failing line is at the top; everything below it is xunit's own
        // plumbing and is the same for every test that ever fails.
        var detail = RunTestsTool.FailureDetail(FailingRun);

        Assert.Contains("MathTests.cs:line 12", detail);
        Assert.DoesNotContain("EvenMorePlumbing", detail);
    }

    [Fact]
    public void The_pass_fail_tally_is_reported()
    {
        Assert.Contains("Failed:     1", RunTestsTool.Summarize(Failing(), "Proj.Tests"));
    }

    [Fact]
    public void A_passing_run_says_so()
    {
        var result = new ProcessResult
        {
            ExitCode = 0,
            Output = "Passed!  - Failed:     0, Passed:   211, Skipped:     0, Total:   211"
        };

        Assert.Contains("passed", RunTestsTool.Summarize(result, "Proj.Tests"));
    }

    [Fact]
    public void Output_with_no_recognisable_failure_block_yields_no_detail()
    {
        // ctest and Gradle print their own shapes. Better to return nothing than
        // to invent structure that is not there.
        Assert.Equal(string.Empty, RunTestsTool.FailureDetail("1/1 Test #1: thing ..... Failed 0.01 sec"));
    }

    [Fact]
    public void A_suite_that_would_not_compile_reports_the_compiler_errors()
    {
        // No tests failed because none ran; the build error is the useful part.
        var result = new ProcessResult
        {
            ExitCode = 1,
            Output = @"D:\proj\MathTests.cs(12,5): error CS0103: The name 'foo' does not exist"
        };

        var report = RunTestsTool.Summarize(result, "Proj.Tests");

        Assert.Contains("CS0103", report);
    }
}
