using System.Collections.Generic;
using Amazon.CDK;
using Amazon.CDK.AWS.Apigatewayv2;
using Amazon.CDK.AWS.CloudWatch;
using Amazon.CDK.AWS.CloudWatch.Actions;
using Amazon.CDK.AWS.CodeDeploy;
using Amazon.CDK.AWS.DynamoDB;
using Amazon.CDK.AWS.IAM;
using Amazon.CDK.AWS.Lambda;
using Amazon.CDK.AWS.Logs;
using Amazon.CDK.AWS.S3;
using Amazon.CDK.AWS.SNS;
using Amazon.CDK.AWS.SNS.Subscriptions;
using Constructs;

namespace Infra;

public class InfraStack : Stack
{
    internal InfraStack(Construct scope, string id, string environmentName, IStackProps props = null) : base(scope, id, props)
    {
        var envName = environmentName.Trim().ToLowerInvariant();
        var resourcePrefix = $"stayrate-{envName}";
        var isProd = string.Equals(envName, "prod", System.StringComparison.Ordinal);
        var removalPolicy = isProd ? RemovalPolicy.RETAIN : RemovalPolicy.DESTROY;
        var lambdaRetention = isProd ? RetentionDays.ONE_MONTH : RetentionDays.TWO_WEEKS;
        var alertEmail = Node.TryGetContext("alertEmail")?.ToString();
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

        var bookingCatalog = new CfnParameter(this, "BookingCatalogJson", new CfnParameterProps
        {
            Type = "String",
            Default = "{}",
            MaxLength = 3000,
            NoEcho = true,
            Description = "JSON containing server-owned booking rates"
        });
        var slackSecret = new CfnParameter(this, "SlackWebhookSecretArn", new CfnParameterProps
        {
            Type = "String",
            Default = "",
            AllowedPattern = "^$|^arn:[^:]+:secretsmanager:[^:]+:[0-9]{12}:secret:.+$",
            NoEcho = true,
            Description = "Optional full Secrets Manager ARN for the Slack webhook URL"
        });
        var slackEnabled = new CfnCondition(this, "SlackRelayEnabled", new CfnConditionProps
        {
            Expression = Fn.ConditionNot(Fn.ConditionEquals(slackSecret.ValueAsString, ""))
        });

        var serviceRoleBoundary = new CfnParameter(this, "ServiceRoleBoundaryArn", new CfnParameterProps
        {
            Type = "String",
            AllowedPattern = "^arn:[^:]+:iam::[0-9]{12}:policy/.+$",
            Description = "Permissions boundary for stack-managed IAM service roles"
        });

        void ApplyRoleBoundary(IRole role)
        {
            if (role.Node.DefaultChild is not CfnRole managedRole)
                throw new System.InvalidOperationException("A stack-managed service role is required.");

            managedRole.PermissionsBoundary = serviceRoleBoundary.ValueAsString;
        }

        var bookingsTable = new Table(this, "BookingsTable", new TableProps
        {
            TableName = $"{resourcePrefix}-bookings",
            PartitionKey = new Attribute { Name = "BookingId", Type = AttributeType.STRING },
            BillingMode = BillingMode.PAY_PER_REQUEST,
            PointInTimeRecoverySpecification = isProd
                ? new PointInTimeRecoverySpecification
                {
                    PointInTimeRecoveryEnabled = true
                }
                : null,
            RemovalPolicy = removalPolicy
        });

        var idempotencyTable = new Table(this, "IdempotencyTable", new TableProps
        {
            TableName = $"{resourcePrefix}-idempotency",
            PartitionKey = new Attribute { Name = "IdempotencyKey", Type = AttributeType.STRING },
            BillingMode = BillingMode.PAY_PER_REQUEST,
            TimeToLiveAttribute = "ExpiresAtEpoch",
            PointInTimeRecoverySpecification = isProd
                ? new PointInTimeRecoverySpecification
                {
                    PointInTimeRecoveryEnabled = true
                }
                : null,
            RemovalPolicy = removalPolicy
        });

        var artifactBucket = Bucket.FromBucketName(this, "LambdaArtifactCodeBucket", lambdaArtifactBucket.ValueAsString);

        var apiLambda = new Function(this, "StayRateApiLambda", new FunctionProps
        {
            FunctionName = $"{resourcePrefix}-api",
            Runtime = Runtime.PROVIDED_AL2023,
            Handler = "bootstrap",
            Architecture = Architecture.ARM_64,
            MemorySize = 512,
            Timeout = Duration.Seconds(15),
            LogGroup = new LogGroup(this, "StayRateApiLambdaLogGroup", new LogGroupProps
            {
                Retention = lambdaRetention
            }),
            Code = Code.FromBucket(
                artifactBucket,
                lambdaArtifactKey.ValueAsString,
                lambdaArtifactObjectVersion.ValueAsString),
            Environment = new Dictionary<string, string>
            {
                ["ASPNETCORE_ENVIRONMENT"] = isProd ? "Production" : "Cloud",
                ["Persistence__Provider"] = "DynamoDb",
                ["Bookings__TableName"] = bookingsTable.TableName,
                ["Idempotency__TableName"] = idempotencyTable.TableName,
                ["STAYRATE_BOOKING_CATALOG_JSON"] = bookingCatalog.ValueAsString
            }
        });

        ApplyRoleBoundary(apiLambda.Role);
        apiLambda.AddToRolePolicy(new PolicyStatement(new PolicyStatementProps
        {
            Actions = ["dynamodb:GetItem", "dynamodb:PutItem"],
            Resources = [bookingsTable.TableArn, idempotencyTable.TableArn]
        }));
        apiLambda.AddToRolePolicy(new PolicyStatement(new PolicyStatementProps
        {
            Actions = ["dynamodb:ConditionCheckItem"],
            Resources = [idempotencyTable.TableArn]
        }));

        var liveAlias = new Alias(this, "StayRateApiLiveAlias", new AliasProps
        {
            AliasName = "live",
            Version = apiLambda.CurrentVersion
        });

        var httpApi = new CfnApi(this, "StayRateHttpApi", new CfnApiProps
        {
            Name = $"{resourcePrefix}-api",
            ProtocolType = "HTTP"
        });

        var integration = new Amazon.CDK.AWS.Apigatewayv2.CfnIntegration(this, "StayRateHttpApiLambdaIntegration", new Amazon.CDK.AWS.Apigatewayv2.CfnIntegrationProps
        {
            ApiId = httpApi.Ref,
            IntegrationType = "AWS_PROXY",
            IntegrationUri = liveAlias.FunctionArn,
            PayloadFormatVersion = "2.0"
        });

        const string bookingQuoteRouteKey = "POST /api/v1/bookings/quote";
        const string bookingRouteKey = "POST /api/v1/bookings/confirm";
        var routes = new[]
        {
            CreateRoute("Health", "GET /health", "NONE"),
            CreateRoute("Ready", "GET /ready", "NONE"),
            CreateRoute("Quote", "POST /api/v1/quote", "NONE"),
            CreateRoute("BookingQuote", bookingQuoteRouteKey, "AWS_IAM"),
            CreateRoute("ConfirmBooking", bookingRouteKey, "AWS_IAM")
        };
        var stage = new CfnStage(this, "StayRateHttpApiDefaultStage", new CfnStageProps
        {
            ApiId = httpApi.Ref,
            StageName = "$default",
            AutoDeploy = true,
            DefaultRouteSettings = new CfnStage.RouteSettingsProperty
            {
                ThrottlingRateLimit = isProd ? 25 : 10,
                ThrottlingBurstLimit = isProd ? 50 : 20
            },
            RouteSettings = new Dictionary<string, object>
            {
                [bookingQuoteRouteKey] = new CfnStage.RouteSettingsProperty
                {
                    ThrottlingRateLimit = isProd ? 5 : 2,
                    ThrottlingBurstLimit = isProd ? 10 : 4
                },
                [bookingRouteKey] = new CfnStage.RouteSettingsProperty
                {
                    ThrottlingRateLimit = isProd ? 5 : 2,
                    ThrottlingBurstLimit = isProd ? 10 : 4
                }
            }
        });
        foreach (var route in routes)
            stage.AddResourceDependency(route);

        CfnRoute CreateRoute(string id, string routeKey, string authorizationType)
        {
            var methodAndPath = routeKey.Replace(" /", "/");
            liveAlias.AddPermission($"Invoke{id}", new Permission
            {
                Principal = new ServicePrincipal("apigateway.amazonaws.com"),
                Action = "lambda:InvokeFunction",
                SourceArn = $"arn:{Partition}:execute-api:{Region}:{Account}:{httpApi.Ref}/$default/{methodAndPath}"
            });
            return new CfnRoute(this, $"StayRateHttpApi{id}Route", new CfnRouteProps
            {
                ApiId = httpApi.Ref,
                RouteKey = routeKey,
                AuthorizationType = authorizationType,
                Target = $"integrations/{integration.Ref}"
            });
        }

        var lambdaErrorsAlarm = new Alarm(this, "StayRateApiLambdaErrorsAlarm", new AlarmProps
        {
            AlarmName = $"{resourcePrefix}-lambda-errors",
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
            AlarmName = $"{resourcePrefix}-api-5xx",
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
            TopicName = $"{resourcePrefix}-alerts"
        });
        var alertRelayLambda = new Function(this, "StayRateAlertsRelayLambda", new FunctionProps
        {
            FunctionName = $"{resourcePrefix}-alerts-relay",
            Runtime = Runtime.PYTHON_3_12,
            Handler = "index.handler",
            Timeout = Duration.Seconds(10),
            LogGroup = new LogGroup(this, "StayRateAlertsRelayLambdaLogGroup", new LogGroupProps
            {
                Retention = lambdaRetention
            }),
            Code = Code.FromInline("""
                import http.client
                import json
                import os
                from urllib.parse import urlsplit

                import boto3
                from botocore.config import Config

                secrets = boto3.client("secretsmanager", config=Config(
                    connect_timeout=2, read_timeout=3, retries={"total_max_attempts": 1}
                ))

                def handler(event, context):
                    secret_arn = os.environ.get("SLACK_WEBHOOK_SECRET_ARN", "")
                    if not secret_arn:
                        return {"status": "disabled"}

                    url = urlsplit(secrets.get_secret_value(SecretId=secret_arn)["SecretString"].strip())
                    if (url.scheme, url.netloc) != ("https", "hooks.slack.com") or not url.path.startswith("/services/"):
                        raise RuntimeError("Invalid Slack webhook configuration")

                    message = json.loads(event["Records"][0]["Sns"]["Message"])
                    body = json.dumps({"text": f"{message['AlarmName']}: {message['NewStateValue']}"})
                    connection = http.client.HTTPSConnection("hooks.slack.com", timeout=5)
                    try:
                        connection.request("POST", url.path, body, {"Content-Type": "application/json"})
                        response = connection.getresponse()
                        if response.status >= 300:
                            raise RuntimeError("Slack rejected the alert")
                    except (OSError, http.client.HTTPException):
                        raise RuntimeError("Could not send the Slack alert") from None
                    finally:
                        connection.close()

                    return {"status": "sent"}
                """),
            Environment = new Dictionary<string, string>
            {
                ["SLACK_WEBHOOK_SECRET_ARN"] = slackSecret.ValueAsString
            }
        });

        ApplyRoleBoundary(alertRelayLambda.Role);

        var slackAccess = new CfnPolicy(this, "StayRateSlackSecretAccess", new CfnPolicyProps
        {
            PolicyName = $"{resourcePrefix}-slack-webhook",
            Roles = [alertRelayLambda.Role.RoleName],
            PolicyDocument = new PolicyDocument(new PolicyDocumentProps
            {
                Statements =
                [
                    new PolicyStatement(new PolicyStatementProps
                    {
                        Actions = ["secretsmanager:GetSecretValue"],
                        Resources = [slackSecret.ValueAsString]
                    })
                ]
            }).ToJSON()
        });
        slackAccess.CfnOptions.Condition = slackEnabled;
        alertsTopic.AddSubscription(new LambdaSubscription(alertRelayLambda));
        if (!string.IsNullOrWhiteSpace(alertEmail))
            alertsTopic.AddSubscription(new EmailSubscription(alertEmail));

        var snsAction = new SnsAction(alertsTopic);
        lambdaErrorsAlarm.AddAlarmAction(snsAction);
        api5xxAlarm.AddAlarmAction(snsAction);

        var dashboard = new Dashboard(this, "StayRateDevDashboard", new DashboardProps
        {
            DashboardName = resourcePrefix
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
        dashboard.AddWidgets(
            new GraphWidget(new GraphWidgetProps
            {
                Title = "API Request Count",
                Left = [new Metric(new MetricProps
                {
                    Namespace = "AWS/ApiGateway",
                    MetricName = "Count",
                    Period = Duration.Minutes(1),
                    Statistic = "Sum",
                    DimensionsMap = new Dictionary<string, string>
                    {
                        ["ApiId"] = httpApi.Ref,
                        ["Stage"] = "$default"
                    }
                })],
                Width = 12
            }),
            new GraphWidget(new GraphWidgetProps
            {
                Title = "API P95 Latency (ms)",
                Left = [new Metric(new MetricProps
                {
                    Namespace = "AWS/ApiGateway",
                    MetricName = "Latency",
                    Period = Duration.Minutes(1),
                    Statistic = "p95",
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
                Title = "DynamoDB Throttled Requests",
                Left = [
                    bookingsTable.MetricThrottledRequestsForOperation("PutItem", new MetricOptions
                    {
                        Period = Duration.Minutes(1),
                        Statistic = "Sum"
                    }),
                    idempotencyTable.MetricThrottledRequestsForOperation("PutItem", new MetricOptions
                    {
                        Period = Duration.Minutes(1),
                        Statistic = "Sum"
                    }),
                    idempotencyTable.MetricThrottledRequestsForOperation("GetItem", new MetricOptions
                    {
                        Period = Duration.Minutes(1),
                        Statistic = "Sum"
                    })
                ],
                Width = 24
            }));

        if (enableCodeDeployCanary)
        {
            var deploymentApplication = new LambdaApplication(this, "StayRateApiDeploymentApplication", new LambdaApplicationProps
            {
                ApplicationName = $"{resourcePrefix}-api"
            });

            var deploymentGroup = new LambdaDeploymentGroup(this, "StayRateApiDeploymentGroup", new LambdaDeploymentGroupProps
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
            ApplyRoleBoundary(deploymentGroup.Role);
        }

        _ = new Amazon.CDK.AWS.SSM.StringParameter(this, "BookingsTableNameParameter", new Amazon.CDK.AWS.SSM.StringParameterProps
        {
            ParameterName = $"/stayrate/{envName}/bookings-table-name",
            StringValue = bookingsTable.TableName
        });

        _ = new Amazon.CDK.AWS.SSM.StringParameter(this, "IdempotencyTableNameParameter", new Amazon.CDK.AWS.SSM.StringParameterProps
        {
            ParameterName = $"/stayrate/{envName}/idempotency-table-name",
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

        _ = new CfnOutput(this, "AlertsRelayFunctionName", new CfnOutputProps
        {
            Value = alertRelayLambda.FunctionName
        });
    }
}
