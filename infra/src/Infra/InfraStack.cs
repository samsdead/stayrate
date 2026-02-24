using System.Collections.Generic;
using Amazon.CDK;
using Amazon.CDK.AWS.Apigatewayv2;
using Amazon.CDK.AWS.CloudWatch;
using Amazon.CDK.AWS.CloudWatch.Actions;
using Amazon.CDK.AWS.CodeDeploy;
using Amazon.CDK.AWS.DynamoDB;
using Amazon.CDK.AWS.IAM;
using Amazon.CDK.AWS.Lambda;
using Amazon.CDK.AWS.S3;
using Amazon.CDK.AWS.SNS;
using Constructs;

namespace Infra;

public class InfraStack : Stack
{
    internal InfraStack(Construct scope, string id, IStackProps props = null) : base(scope, id, props)
    {
        var enableCodeDeployCanary = string.Equals(
            Node.TryGetContext("enableCodeDeployCanary")?.ToString(),
            "true",
            System.StringComparison.OrdinalIgnoreCase);

        var lambdaArtifactBucket = new CfnParameter(this, "LambdaArtifactBucket", new CfnParameterProps
        {
            Type = "String",
            Description = "S3 bucket containing the Lambda deployment artifact"
        });

        var lambdaArtifactKey = new CfnParameter(this, "LambdaArtifactKey", new CfnParameterProps
        {
            Type = "String",
            Description = "S3 object key for the Lambda deployment artifact zip"
        });

        var lambdaArtifactObjectVersion = new CfnParameter(this, "LambdaArtifactObjectVersion", new CfnParameterProps
        {
            Type = "String",
            Description = "S3 object version for the Lambda deployment artifact"
        });

        var bookingsTable = new Table(this, "BookingsTable", new TableProps
        {
            TableName = "stayrate-dev-bookings",
            PartitionKey = new Attribute { Name = "BookingId", Type = AttributeType.STRING },
            BillingMode = BillingMode.PAY_PER_REQUEST,
            RemovalPolicy = RemovalPolicy.DESTROY
        });

        var idempotencyTable = new Table(this, "IdempotencyTable", new TableProps
        {
            TableName = "stayrate-dev-idempotency",
            PartitionKey = new Attribute { Name = "IdempotencyKey", Type = AttributeType.STRING },
            BillingMode = BillingMode.PAY_PER_REQUEST,
            TimeToLiveAttribute = "ExpiresAtEpoch",
            RemovalPolicy = RemovalPolicy.DESTROY
        });

        var artifactBucket = Bucket.FromBucketName(this, "LambdaArtifactCodeBucket", lambdaArtifactBucket.ValueAsString);

        var apiLambda = new Function(this, "StayRateApiLambda", new FunctionProps
        {
            FunctionName = "stayrate-dev-api",
            Runtime = Runtime.PROVIDED_AL2023,
            Handler = "bootstrap",
            Architecture = Architecture.ARM_64,
            MemorySize = 512,
            Timeout = Duration.Seconds(15),
            LogGroup = new Amazon.CDK.AWS.Logs.LogGroup(this, "StayRateApiLambdaLogGroup", new Amazon.CDK.AWS.Logs.LogGroupProps
            {
                Retention = Amazon.CDK.AWS.Logs.RetentionDays.TWO_WEEKS
            }),
            Code = Code.FromBucket(
                artifactBucket,
                lambdaArtifactKey.ValueAsString,
                lambdaArtifactObjectVersion.ValueAsString),
            Environment = new Dictionary<string, string>
            {
                ["ASPNETCORE_ENVIRONMENT"] = "Cloud",
                ["Bookings__TableName"] = bookingsTable.TableName,
                ["Idempotency__TableName"] = idempotencyTable.TableName
            }
        });

        bookingsTable.GrantReadWriteData(apiLambda);
        idempotencyTable.GrantReadWriteData(apiLambda);

        var liveAlias = new Alias(this, "StayRateApiLiveAlias", new AliasProps
        {
            AliasName = "live",
            Version = apiLambda.CurrentVersion
        });

        var httpApi = new CfnApi(this, "StayRateHttpApi", new CfnApiProps
        {
            Name = "stayrate-dev-api",
            ProtocolType = "HTTP"
        });

        var integration = new Amazon.CDK.AWS.Apigatewayv2.CfnIntegration(this, "StayRateHttpApiLambdaIntegration", new Amazon.CDK.AWS.Apigatewayv2.CfnIntegrationProps
        {
            ApiId = httpApi.Ref,
            IntegrationType = "AWS_PROXY",
            IntegrationUri = liveAlias.FunctionArn,
            PayloadFormatVersion = "2.0"
        });

        _ = new CfnRoute(this, "StayRateHttpApiDefaultRoute", new CfnRouteProps
        {
            ApiId = httpApi.Ref,
            RouteKey = "$default",
            Target = $"integrations/{integration.Ref}"
        });

        _ = new CfnStage(this, "StayRateHttpApiDefaultStage", new CfnStageProps
        {
            ApiId = httpApi.Ref,
            StageName = "$default",
            AutoDeploy = true
        });

        liveAlias.AddPermission("AllowHttpApiInvoke", new Permission
        {
            Principal = new ServicePrincipal("apigateway.amazonaws.com"),
            Action = "lambda:InvokeFunction",
            SourceArn = $"arn:aws:execute-api:{Region}:{Account}:{httpApi.Ref}/*/$default"
        });

        var lambdaErrorsAlarm = new Alarm(this, "StayRateApiLambdaErrorsAlarm", new AlarmProps
        {
            AlarmName = "stayrate-dev-lambda-errors",
            Metric = liveAlias.MetricErrors(new MetricOptions
            {
                Period = Duration.Minutes(1),
                Statistic = "Sum"
            }),
            Threshold = 1,
            EvaluationPeriods = 1,
            DatapointsToAlarm = 1,
            TreatMissingData = TreatMissingData.NOT_BREACHING
        });

        var api5xxAlarm = new Alarm(this, "StayRateApi5xxAlarm", new AlarmProps
        {
            AlarmName = "stayrate-dev-api-5xx",
            Metric = new Metric(new MetricProps
            {
                Namespace = "AWS/ApiGateway",
                MetricName = "5xx",
                Period = Duration.Minutes(1),
                Statistic = "Sum",
                DimensionsMap = new Dictionary<string, string>
                {
                    ["ApiId"] = httpApi.Ref,
                    ["Stage"] = "$default"
                }
            }),
            Threshold = 1,
            EvaluationPeriods = 1,
            DatapointsToAlarm = 1,
            TreatMissingData = TreatMissingData.NOT_BREACHING
        });

        var alertsTopic = new Topic(this, "StayRateDevAlertsTopic", new TopicProps
        {
            TopicName = "stayrate-dev-alerts"
        });

        var snsAction = new SnsAction(alertsTopic);
        lambdaErrorsAlarm.AddAlarmAction(snsAction);
        api5xxAlarm.AddAlarmAction(snsAction);

        var dashboard = new Dashboard(this, "StayRateDevDashboard", new DashboardProps
        {
            DashboardName = "stayrate-dev"
        });
        dashboard.AddWidgets(
            new GraphWidget(new GraphWidgetProps
            {
                Title = "Lambda Errors",
                Left = [liveAlias.MetricErrors(new MetricOptions { Period = Duration.Minutes(1), Statistic = "Sum" })],
                Width = 12
            }),
            new GraphWidget(new GraphWidgetProps
            {
                Title = "API 5XX",
                Left = [new Metric(new MetricProps
                {
                    Namespace = "AWS/ApiGateway",
                    MetricName = "5xx",
                    Period = Duration.Minutes(1),
                    Statistic = "Sum",
                    DimensionsMap = new Dictionary<string, string>
                    {
                        ["ApiId"] = httpApi.Ref,
                        ["Stage"] = "$default"
                    }
                })],
                Width = 12
            }));
        dashboard.AddWidgets(
            new GraphWidget(new GraphWidgetProps
            {
                Title = "Lambda P95 Duration (ms)",
                Left = [liveAlias.MetricDuration(new MetricOptions { Period = Duration.Minutes(1), Statistic = "p95" })],
                Width = 24
            }));

        if (enableCodeDeployCanary)
        {
            var deploymentApplication = new LambdaApplication(this, "StayRateApiDeploymentApplication", new LambdaApplicationProps
            {
                ApplicationName = "stayrate-dev-api"
            });

            _ = new LambdaDeploymentGroup(this, "StayRateApiDeploymentGroup", new LambdaDeploymentGroupProps
            {
                Application = deploymentApplication,
                Alias = liveAlias,
                DeploymentConfig = LambdaDeploymentConfig.CANARY_10PERCENT_5MINUTES,
                Alarms = [lambdaErrorsAlarm, api5xxAlarm],
                AutoRollback = new AutoRollbackConfig
                {
                    FailedDeployment = true,
                    StoppedDeployment = true,
                    DeploymentInAlarm = true
                }
            });
        }

        _ = new Amazon.CDK.AWS.SSM.StringParameter(this, "BookingsTableNameParameter", new Amazon.CDK.AWS.SSM.StringParameterProps
        {
            ParameterName = "/stayrate/dev/bookings-table-name",
            StringValue = bookingsTable.TableName
        });

        _ = new Amazon.CDK.AWS.SSM.StringParameter(this, "IdempotencyTableNameParameter", new Amazon.CDK.AWS.SSM.StringParameterProps
        {
            ParameterName = "/stayrate/dev/idempotency-table-name",
            StringValue = idempotencyTable.TableName
        });

        _ = new CfnOutput(this, "BookingsTableName", new CfnOutputProps
        {
            Value = bookingsTable.TableName
        });

        _ = new CfnOutput(this, "IdempotencyTableName", new CfnOutputProps
        {
            Value = idempotencyTable.TableName
        });

        _ = new CfnOutput(this, "ApiEndpoint", new CfnOutputProps
        {
            Value = httpApi.AttrApiEndpoint
        });

        _ = new CfnOutput(this, "AlertsTopicArn", new CfnOutputProps
        {
            Value = alertsTopic.TopicArn
        });
    }
}
