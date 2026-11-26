using Amazon.CDK;

namespace Infra;

sealed class Program
{
    public static void Main(string[] args)
    {
        var app = new App();
        var env = new Amazon.CDK.Environment
        {
            Account = System.Environment.GetEnvironmentVariable("CDK_DEFAULT_ACCOUNT"),
            Region = System.Environment.GetEnvironmentVariable("CDK_DEFAULT_REGION")
        };
        _ = new InfraStack(app, "StayRateDevStack", "dev", new StackProps
        {
            Env = env,
            Synthesizer = new DefaultStackSynthesizer(new DefaultStackSynthesizerProps
            {
                GenerateBootstrapVersionRule = false
            })
        });
        _ = new InfraStack(app, "StayRateProdStack", "prod", new StackProps
        {
            Env = env,
            Synthesizer = new DefaultStackSynthesizer(new DefaultStackSynthesizerProps
            {
                GenerateBootstrapVersionRule = false
            })
        });
        app.Synth();
    }
}
