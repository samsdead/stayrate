using System.Collections.Generic;
using Amazon.CDK;
using Amazon.CDK.AWS.Apigatewayv2;
using Amazon.CDK.AWS.DynamoDB;
using Amazon.CDK.AWS.Lambda;
using Amazon.CDK.AWS.S3;
using Amazon.CDK.AWS.IAM;
using Constructs;

namespace Infra;

public class InfraStack : Stack
{
    internal InfraStack(Construct scope, string id, IStackProps props = null) : base(scope, id, props)
    {
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
            RemovalPolicy = RemovalPolicy.DESTROY
        });

        var apiLambda = new Function(this, "StayRateApiLambda", new FunctionProps
        {
            FunctionName = "stayrate-dev-api",
            Runtime = Runtime.PROVIDED_AL2023,
            Handler = "bootstrap",
            Architecture = Architecture.ARM_64,
            MemorySize = 512,
            Timeout = Duration.Seconds(15),
            Code = Code.FromBucket(
                Bucket.FromBucketName(this, "LambdaArtifactCodeBucket", lambdaArtifactBucket.ValueAsString),
                lambdaArtifactKey.ValueAsString),
            Environment = new Dictionary<string, string>
            {
                ["ASPNETCORE_ENVIRONMENT"] = "Cloud",
                ["Bookings__TableName"] = bookingsTable.TableName,
                ["Idempotency__TableName"] = idempotencyTable.TableName
            }
        });

        bookingsTable.GrantReadWriteData(apiLambda);
        idempotencyTable.GrantReadWriteData(apiLambda);

        var httpApi = new CfnApi(this, "StayRateHttpApi", new CfnApiProps
        {
            Name = "stayrate-dev-api",
            ProtocolType = "HTTP"
        });

        var integration = new CfnIntegration(this, "StayRateHttpApiLambdaIntegration", new CfnIntegrationProps
        {
            ApiId = httpApi.Ref,
            IntegrationType = "AWS_PROXY",
            IntegrationUri = apiLambda.FunctionArn,
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

        apiLambda.AddPermission("AllowHttpApiInvoke", new Permission
        {
            Principal = new ServicePrincipal("apigateway.amazonaws.com"),
            Action = "lambda:InvokeFunction",
            SourceArn = $"arn:aws:execute-api:{Region}:{Account}:{httpApi.Ref}/*/$default"
        });

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
    }
}
