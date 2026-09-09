using BenchmarkDotNet.Running;

namespace EventHorizon.LfuCache.Benchmarks;

public static class Program
{
    public static void Main(string[] args)
    {
        if (TryGetScenarioReportPath(args, out var outputPath))
        {
            ScenarioReportRunner.Write(outputPath);
            return;
        }

        BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
    }

    private static bool TryGetScenarioReportPath(string[] args, out string outputPath)
    {
        for (var index = 0; index < args.Length; index++)
        {
            var argument = args[index];
            if (StringComparer.OrdinalIgnoreCase.Equals(argument, "--scenario-report"))
            {
                if (index + 1 >= args.Length || string.IsNullOrWhiteSpace(args[index + 1]))
                {
                    throw new ArgumentException("--scenario-report requires an output path.");
                }

                outputPath = args[index + 1];
                return true;
            }

            const string prefix = "--scenario-report=";
            if (argument.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                outputPath = argument[prefix.Length..];
                if (string.IsNullOrWhiteSpace(outputPath))
                {
                    throw new ArgumentException("--scenario-report requires an output path.");
                }

                return true;
            }
        }

        outputPath = string.Empty;
        return false;
    }
}
