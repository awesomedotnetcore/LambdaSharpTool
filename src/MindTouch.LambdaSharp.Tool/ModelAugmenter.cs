/*
 * MindTouch λ#
 * Copyright (C) 2018 MindTouch, Inc.
 * www.mindtouch.com  oss@mindtouch.com
 *
 * For community documentation and downloads visit mindtouch.com;
 * please review the licensing section.
 *
 * Licensed under the Apache License, Version 2.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at
 *
 *     http://www.apache.org/licenses/LICENSE-2.0
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 */

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using MindTouch.LambdaSharp.Tool.Internal;
using MindTouch.LambdaSharp.Tool.Model;
using MindTouch.LambdaSharp.Tool.Model.AST;
using Newtonsoft.Json;

namespace MindTouch.LambdaSharp.Tool {

    public class ModelAugmenter : AModelProcessor {

        //--- Types ---
        private class ApiRoute {

            //--- Properties ---
            public string Method { get; set; }
            public string[] Path { get; set; }
            public ApiGatewaySourceIntegration Integration { get; set; }
            public ModuleBuilderEntry<FunctionParameter> Function { get; set; }
            public string OperationName { get; set; }
            public bool? ApiKeyRequired { get; set; }
        }

        //--- Fields ---
        private ModuleBuilder _builder;
        private List<ApiRoute> _apiGatewayRoutes;

        //--- Constructors ---
        public ModelAugmenter(Settings settings, string sourceFilename) : base(settings, sourceFilename) { }

        //--- Methods ---
        public void Augment(Module module) {
            _builder = new ModuleBuilder(Settings, SourceFilename, module);

            // add module variables
            var moduleValue = _builder.AddVariable("Module", "Module Variables", "");
            _builder.AddVariable("Module::Id", "Module ID", FnRef("AWS::StackName"));
            _builder.AddVariable("Module::Name", "Module Name", module.Name);
            _builder.AddVariable("Module::Version", "Module Version", module.Version.ToString());

            // add LambdaSharp Module Options
            var section = "LambdaSharp Module Options";
            _builder.AddInput(
                name: "Secrets",
                section: section,
                label: "Secret Keys (ARNs)",
                description: "Comma-separated list of optional secret keys",
                defaultValue: ""
            );

            // add decryption permission for secret
            _builder.AddResourceStatement(new Humidifier.Statement {
                Sid = "SecretsDecryption",
                Effect = "Allow",
                Resource = FnSplit(
                    ",",
                    FnIf(
                        "SecretsIsEmpty",
                        FnJoin(",", module.Secrets),
                        FnJoin(
                            ",",
                            new List<object> {
                                FnJoin(",", module.Secrets),
                                FnRef("Secrets")
                            }
                        )
                    )
                ),
                Action = new List<string> {
                    "kms:Decrypt",
                    "kms:Encrypt",
                    "kms:GenerateDataKey",
                    "kms:GenerateDataKeyWithoutPlaintext"
                }
            });
            _builder.AddCondition("SecretsIsEmpty", FnEquals(FnRef("Secrets"), ""));

            // add standard parameters (unless requested otherwise)
            if(!_builder.HasPragma("no-lambdasharp-dependencies")) {

                // add LambdaSharp Module Internal Dependencies
                section = "LambdaSharp Dependencies";
                _builder.AddImport(
                    import: "LambdaSharp::DeadLetterQueueArn",
                    section: section,
                    label: "Dead Letter Queue (ARN)",
                    description: "Dead letter queue for functions"
                );
                _builder.AddImport(
                    import: "LambdaSharp::LoggingStreamArn",
                    section: section,
                    label: "Logging Stream (ARN)",
                    description: "Logging kinesis stream for functions"
                );
                _builder.AddImport(
                    import: "LambdaSharp::DefaultSecretKeyArn",
                    section: section,
                    label: "Secret Key (ARN)",
                    description: "Default secret key for functions"
                );
                _builder.AddSecret(FnRef("Module::DefaultSecretKeyArn"));

                // add lambdasharp imports
                _builder.AddVariable("Module::DeadLetterQueueArn", "Module Dead Letter Queue (ARN)", FnRef("LambdaSharp::DeadLetterQueueArn"));
                _builder.AddVariable("Module::LoggingStreamArn", "Module Logging Stream (ARN)", FnRef("LambdaSharp::LoggingStreamArn"));
                _builder.AddVariable("Module::DefaultSecretKeyArn", "Module Default Secret Key (ARN)", FnRef("LambdaSharp::DefaultSecretKeyArn"));

                // permissions needed for dead-letter queue
                _builder.AddResourceStatement(new Humidifier.Statement {
                    Sid = "ModuleDeadLetterQueueLogging",
                    Effect = "Allow",
                    Resource = FnRef("Module::DeadLetterQueueArn"),
                    Action = new List<string> {
                        "sqs:SendMessage"
                    }
                });
            }

            // add LambdaSharp Deployment Settings
            section = "LambdaSharp Deployment Settings (DO NOT MODIFY)";
            _builder.AddInput(
                name: "DeploymentBucketName",
                section: section,
                label: "Deployment S3 Bucket",
                description: "Source deployment S3 bucket name"
            );
            _builder.AddInput(
                name: "DeploymentPrefix",
                section: section,
                label: "Deployment Prefix",
                description: "Module deployment prefix"
            );
            _builder.AddInput(
                name: "DeploymentPrefixLowercase",
                section: section,
                label: "Deployment Prefix (lowercase)",
                description: "Module deployment prefix (lowercase)"
            );
            _builder.AddInput(
                name: "DeploymentParent",
                section: section,
                label: "Parent Stack Name",
                description: "Parent stack name for nested deployments, blank otherwise",
                defaultValue: ""
            );

            // add module registration
            if(!_builder.HasPragma("no-module-registration")) {
                moduleValue.AddEntry(new HumidifierParameter {
                    Name = "Registration",
                    Resource = new Humidifier.CustomResource("LambdaSharp::Register::Module") {
                        ["ModuleId"] = FnRef("AWS::StackName"),
                        ["ModuleName"] = module.Name,
                        ["ModuleVersion"] = module.Version.ToString()
                    }
                });
            }

            // create module IAM role used by all functions
            var functions = module.GetAllEntriesOfType<FunctionParameter>()
                .Select(entry => new ModuleBuilderEntry<FunctionParameter>(_builder, entry))
                .ToList();
            if(functions.Any()) {

                // create module role
                moduleValue.AddEntry(new HumidifierParameter {
                    Name = "Role",
                    Resource = new Humidifier.IAM.Role {
                        AssumeRolePolicyDocument = new Humidifier.PolicyDocument {
                            Version = "2012-10-17",
                            Statement = new[] {
                                new Humidifier.Statement {
                                    Sid = "ModuleLambdaInvocation",
                                    Effect = "Allow",
                                    Principal = new Humidifier.Principal {
                                        Service = "lambda.amazonaws.com"
                                    },
                                    Action = "sts:AssumeRole"
                                }
                            }.ToList()
                        },
                        Policies = new[] {
                            new Humidifier.IAM.Policy {
                                PolicyName = FnSub("${AWS::StackName}ModulePolicy"),
                                PolicyDocument = new Humidifier.PolicyDocument {
                                    Version = "2012-10-17",
                                    Statement = module.ResourceStatements
                                }
                            }
                        }.ToList()
                    }
                });

                // permission needed for writing to log streams (but not for creating log groups!)
                _builder.AddResourceStatement(new Humidifier.Statement {
                    Sid = "ModuleLogStreamAccess",
                    Effect = "Allow",
                    Resource = "arn:aws:logs:*:*:*",
                    Action = new List<string> {
                        "logs:CreateLogStream",
                        "logs:PutLogEvents"
                    }
                });

                // permissions needed for lambda functions to exist in a VPC
                if(functions.Any(function => function.Resource.Function.VpcConfig != null)) {
                    _builder.AddResourceStatement(new Humidifier.Statement {
                        Sid = "ModuleVpcNetworkInterfaces",
                        Effect = "Allow",
                        Resource = "*",
                        Action = new List<string> {
                            "ec2:DescribeNetworkInterfaces",
                            "ec2:CreateNetworkInterface",
                            "ec2:DeleteNetworkInterface"
                        }
                    });
                }

                // create function registration
                if(module.HasModuleRegistration) {

                    // create CloudWatch Logs IAM role to invoke kinesis stream
                     moduleValue.AddEntry(new HumidifierParameter {
                        Name = "CloudWatchLogsRole",
                        Resource = new Humidifier.IAM.Role {
                            AssumeRolePolicyDocument = new Humidifier.PolicyDocument {
                                Version = "2012-10-17",
                                Statement = new[] {
                                    new Humidifier.Statement {
                                        Sid = "CloudWatchLogsKinesisInvocation",
                                        Effect = "Allow",
                                        Principal = new Humidifier.Principal {
                                            Service = FnSub("logs.${AWS::Region}.amazonaws.com")
                                        },
                                        Action = "sts:AssumeRole"
                                    }
                                }.ToList()
                            },
                            Policies = new[] {
                                new Humidifier.IAM.Policy {
                                    PolicyName = FnSub("${AWS::StackName}ModuleCloudWatchLogsPolicy"),
                                    PolicyDocument = new Humidifier.PolicyDocument {
                                        Version = "2012-10-17",
                                        Statement = new[] {
                                            new Humidifier.Statement {
                                                Sid = "CloudWatchLogsKinesisPermissions",
                                                Effect = "Allow",
                                                Action = "kinesis:PutRecord",
                                                Resource = FnRef("Module::LoggingStreamArn")
                                            }
                                        }.ToList()
                                    }
                                }
                            }.ToList()
                        }
                    });
                }

                // add functions
                foreach(var function in functions) {
                    AddFunction(module, function);
                }

                // check if RestApi resources need to be added
                if(functions.Any(f => f.Resource.Sources.OfType<ApiGatewaySource>().Any())) {
                    _apiGatewayRoutes = new List<ApiRoute>();

                    // check if an API gateway needs to be created
                    if(_apiGatewayRoutes.Any()) {
                        var restApiName = "ModuleRestApi";

                        // recursively create resources as needed
                        var apiMethods = new List<KeyValuePair<string, object>>();
                        AddApiResource(null, restApiName, FnRef(restApiName), FnGetAtt(restApiName, "RootResourceId"), 0, _apiGatewayRoutes, apiMethods);

                        // RestApi deployment depends on all methods and their hash (to force redeployment in case of change)
                        var methodSignature = string.Join("\n", apiMethods
                            .OrderBy(kv => kv.Key)
                            .Select(kv => JsonConvert.SerializeObject(kv.Value))
                        );
                        string methodsHash = methodSignature.ToMD5Hash();

                        // create a RestApi
                        var restApiVar = moduleValue.AddEntry(new HumidifierParameter {
                            Name = "RestApi",
                            Description = "Module REST API",
                            Resource = new Humidifier.ApiGateway.RestApi {
                                Name = FnSub("${AWS::StackName} Module API"),
                                Description = "${Module::Name} API (v${Module::Version})",
                                FailOnWarnings = true
                            }
                        });

                        // add RestApi url
                        _builder.AddVariable(
                            "Module::RestApi::Url",
                            "Module REST API URL",
                            FnSub("https://${Module::RestApi}.execute-api.${AWS::Region}.${AWS::URLSuffix}/LATEST/")
                        );

                        // create a RestApi role that can write logs
                        restApiVar.AddEntry(new HumidifierParameter {
                            Name = "Role",
                            Description = "Module REST API Role",
                            Resource = new Humidifier.IAM.Role {
                                AssumeRolePolicyDocument = new Humidifier.PolicyDocument {
                                    Version = "2012-10-17",
                                    Statement = new[] {
                                        new Humidifier.Statement {
                                            Sid = "ModuleRestApiInvocation",
                                            Effect = "Allow",
                                            Principal = new Humidifier.Principal {
                                                Service = "apigateway.amazonaws.com"
                                            },
                                            Action = "sts:AssumeRole"
                                        }
                                    }.ToList()
                                },
                                Policies = new[] {
                                    new Humidifier.IAM.Policy {
                                        PolicyName = FnSub("${AWS::StackName}ModuleRestApiPolicy"),
                                        PolicyDocument = new Humidifier.PolicyDocument {
                                            Version = "2012-10-17",
                                            Statement = new[] {
                                                new Humidifier.Statement {
                                                    Sid = "ModuleRestApiLogging",
                                                    Effect = "Allow",
                                                    Action = new[] {
                                                        "logs:CreateLogGroup",
                                                        "logs:CreateLogStream",
                                                        "logs:DescribeLogGroups",
                                                        "logs:DescribeLogStreams",
                                                        "logs:PutLogEvents",
                                                        "logs:GetLogEvents",
                                                        "logs:FilterLogEvents"
                                                    },
                                                    Resource = "*"
                                                }
                                            }.ToList()
                                        }
                                    }
                                }.ToList()

                            }
                        });

                        // create a RestApi account which uses the RestApi role
                        restApiVar.AddEntry(new HumidifierParameter {
                            Name = "Account",
                            Description = "Module REST API Account",
                            Resource = new Humidifier.ApiGateway.Account {
                                CloudWatchRoleArn = FnGetAtt("Module::RestApi::Role", "Arn")
                            }
                        });

                        // NOTE (2018-06-21, bjorg): the RestApi deployment resource depends on ALL methods resources having been created;
                        //  a new name is used for the deployment to force the stage to be updated
                        restApiVar.AddEntry(new HumidifierParameter {
                            Name = "Deployment" + methodsHash,
                            Description = "Module REST API Deployment",
                            Resource = new Humidifier.ApiGateway.Deployment {
                                RestApiId = FnRef("Module::RestApi"),
                                Description = FnSub($"${{AWS::StackName}} API [{methodsHash}]")
                            },
                            DependsOn = null // TODO: depends on all AWS::ApiGateway::Method
                        });

                        // RestApi stage depends on API gateway deployment and API gateway account
                        // NOTE (2018-06-21, bjorg): the stage resource depends on the account resource having been granted
                        //  the necessary permissions for logging
                        restApiVar.AddEntry(new HumidifierParameter {
                            Name = "Stage",
                            Description = "Module REST API Stage",
                            Resource = new Humidifier.ApiGateway.Stage {
                                RestApiId = FnRef("Module::RestApi"),
                                DeploymentId = FnRef("Module::RestApi::Deployment" + methodsHash),
                                StageName = "LATEST",
                                MethodSettings = new[] {
                                    new Humidifier.ApiGateway.StageTypes.MethodSetting {
                                        DataTraceEnabled = true,
                                        HttpMethod = "*",
                                        LoggingLevel = "INFO",
                                        ResourcePath = "/*"
                                    }
                                }.ToList()
                            },
                            DependsOn = new[] { "Module::RestApi::Account" }
                        });
                    }
                }
            }
        }

        private void AddApiResource(ModuleBuilderEntry<AResource> parent, string parentPrefix, object restApiId, object parentId, int level, IEnumerable<ApiRoute> routes, List<KeyValuePair<string, object>> apiMethods) {

            // attach methods to resource id
            var methods = routes.Where(route => route.Path.Length == level).ToArray();
            foreach(var method in methods) {
                var methodName = parentPrefix + method.Method;
                Humidifier.ApiGateway.Method apiMethod;
                switch(method.Integration) {
                case ApiGatewaySourceIntegration.RequestResponse:
                    apiMethod = CreateRequestResponseApiMethod(method);
                    break;
                case ApiGatewaySourceIntegration.SlackCommand:
                    apiMethod = CreateSlackRequestApiMethod(method);
                    break;
                default:
                    AddError($"api integration {method.Integration} is not supported");
                    continue;
                }
                apiMethods.Add(new KeyValuePair<string, object>(methodName, apiMethod));
                parent.AddEntry(new HumidifierParameter {
                    Name = method.Method,
                    Resource = apiMethod
                });
                parent.AddEntry(new HumidifierParameter {
                    Name = $"{methodName}Permission",
                    Resource = new Humidifier.Lambda.Permission {
                        Action = "lambda:InvokeFunction",
                        FunctionName = FnGetAtt(method.Function.FullName, "Arn"),
                        Principal = "apigateway.amazonaws.com",
                        SourceArn = FnSub($"arn:aws:execute-api:${{AWS::Region}}:${{AWS::AccountId}}:${{ModuleRestApi}}/LATEST/{method.Method}/{string.Join("/", method.Path)}")
                    }
                });
            }

            // create new resource for each route with a common path segment
            var subRoutes = routes.Where(route => route.Path.Length > level).ToLookup(route => route.Path[level]);
            foreach(var subRoute in subRoutes) {

                // remove special character from path segment and capitalize it
                var partName = new string(subRoute.Key.Where(c => char.IsLetterOrDigit(c)).ToArray());
                partName = char.ToUpperInvariant(partName[0]) + partName.Substring(1);

                // create a new resource
                var newResourceName = parentPrefix + partName + "Resource";
                var resource = parent.AddEntry(new HumidifierParameter {
                    Name = newResourceName,
                    Resource = new Humidifier.ApiGateway.Resource {
                        RestApiId = restApiId,
                        ParentId = parentId,
                        PathPart = subRoute.Key
                    }
                });
                AddApiResource(resource.Cast<AResource>(), parentPrefix + partName, restApiId, FnRef(newResourceName), level + 1, subRoute, apiMethods);
            }

            Humidifier.ApiGateway.Method CreateRequestResponseApiMethod(ApiRoute method) {
                return new Humidifier.ApiGateway.Method {
                    AuthorizationType = "NONE",
                    HttpMethod = method.Method,
                    OperationName = method.OperationName,
                    ApiKeyRequired = method.ApiKeyRequired,
                    ResourceId = parentId,
                    RestApiId = restApiId,
                    Integration = new Humidifier.ApiGateway.MethodTypes.Integration {
                        Type = "AWS_PROXY",
                        IntegrationHttpMethod = "POST",
                        Uri = FnSub(
                            "arn:aws:apigateway:${AWS::Region}:lambda:path/2015-03-31/functions/${Arn}/invocations",
                            new Dictionary<string, object> {
                                ["Arn"] = FnGetAtt(method.Function.FullName, "Arn")
                            }
                        )
                    }
                };
            }

            Humidifier.ApiGateway.Method CreateSlackRequestApiMethod(ApiRoute method) {

                // NOTE (2018-06-06, bjorg): Slack commands have a 3sec timeout on invocation, which is rarely good enough;
                // instead we wire Slack command requests up as asynchronous calls; this way, we can respond with
                // a callback later and the integration works well all the time.
                return new Humidifier.ApiGateway.Method {
                    AuthorizationType = "NONE",
                    HttpMethod = method.Method,
                    OperationName = method.OperationName,
                    ApiKeyRequired = method.ApiKeyRequired,
                    ResourceId = parentId,
                    RestApiId = restApiId,
                    Integration = new Humidifier.ApiGateway.MethodTypes.Integration {
                        Type = "AWS",
                        IntegrationHttpMethod = "POST",
                        Uri = FnSub($"arn:aws:apigateway:${{AWS::Region}}:lambda:path/2015-03-31/functions/${{{method.Function.ResourceName}.Arn}}/invocations"),
                        RequestParameters = new Dictionary<string, object> {
                            ["integration.request.header.X-Amz-Invocation-Type"] = "'Event'"
                        },
                        RequestTemplates = new Dictionary<string, object> {
                            ["application/x-www-form-urlencoded"] =
@"{
#foreach($token in $input.path('$').split('&'))
    #set($keyVal = $token.split('='))
    #set($keyValSize = $keyVal.size())
    #if($keyValSize == 2)
        #set($key = $util.escapeJavaScript($util.urlDecode($keyVal[0])))
        #set($val = $util.escapeJavaScript($util.urlDecode($keyVal[1])))
        ""$key"": ""$val""#if($foreach.hasNext),#end
    #end
#end
}"
                        },
                        IntegrationResponses = new[] {
                            new Humidifier.ApiGateway.MethodTypes.IntegrationResponse {
                                StatusCode = 200,
                                ResponseTemplates = new Dictionary<string, object> {
                                    ["application/json"] =
@"{
""response_type"": ""in_channel"",
""text"": """"
}"
                                }
                            }
                        }.ToList()
                    },
                    MethodResponses = new[] {
                        new Humidifier.ApiGateway.MethodTypes.MethodResponse {
                            StatusCode = 200,
                            ResponseModels = new Dictionary<string, object> {
                                ["application/json"] = "Empty"
                            }
                        }
                    }.ToList()
                };
            }
        }

        private void AddFunction(Module module, ModuleBuilderEntry<FunctionParameter> function) {

            // create function log-group with retention window
            var logGroup = function.AddEntry(new HumidifierParameter {
                Name = "LogGroup",
                Resource = new Humidifier.Logs.LogGroup {
                    LogGroupName = FnSub($"/aws/lambda/${{{function.LogicalId}}}"),

                    // TODO (2018-09-26, bjorg): make retention configurable
                    //  see https://docs.aws.amazon.com/AmazonCloudWatchLogs/latest/APIReference/API_PutRetentionPolicy.html
                    RetentionInDays = 7
                }
            });

            // add function sources
            for(var sourceIndex = 0; sourceIndex < function.Resource.Sources.Count; ++sourceIndex) {
                var source = function.Resource.Sources[sourceIndex];
                var sourceSuffix = (sourceIndex + 1).ToString();
                switch(source) {
                case TopicSource topicSource:
                    Enumerate(topicSource.TopicName, (suffix, _, arn) => {
                        function.AddEntry(new HumidifierParameter {
                            Name = $"Source{sourceSuffix}Subscription{suffix}",
                            Resource = new Humidifier.SNS.Subscription {
                                Endpoint = FnGetAtt(function.FullName, "Arn"),
                                Protocol = "lambda",
                                TopicArn = arn
                            }
                        });
                        function.AddEntry(new HumidifierParameter {
                            Name = $"Source{sourceSuffix}Permission{suffix}",
                            Resource = new Humidifier.Lambda.Permission {
                                Action = "lambda:InvokeFunction",
                                SourceArn = arn,
                                FunctionName = FnGetAtt(function.FullName, "Arn"),
                                Principal = "sns.amazonaws.com"
                            }
                        });
                    });
                    break;
                case ScheduleSource scheduleSource: {
                        var schedule = function.AddEntry(new HumidifierParameter {
                            Name = $"Source{sourceSuffix}ScheduleEvent",
                            Resource = new Humidifier.Events.Rule {
                                ScheduleExpression = scheduleSource.Expression,
                                Targets = new[] {
                                    new Humidifier.Events.RuleTypes.Target {
                                        Id = FnSub($"${{AWS::StackName}}-{function.LogicalId}Source{sourceSuffix}ScheduleEvent"),
                                        Arn = FnGetAtt(function.FullName, "Arn"),
                                        InputTransformer = new Humidifier.Events.RuleTypes.InputTransformer {
                                            InputPathsMap = new Dictionary<string, object> {
                                                ["version"] = "$.version",
                                                ["id"] = "$.id",
                                                ["source"] = "$.source",
                                                ["account"] = "$.account",
                                                ["time"] = "$.time",
                                                ["region"] = "$.region"
                                            },
                                            InputTemplate =
@"{
    ""Version"": <version>,
    ""Id"": <id>,
    ""Source"": <source>,
    ""Account"": <account>,
    ""Time"": <time>,
    ""Region"": <region>,
    ""tName"": """ + scheduleSource.Name + @"""
}"
                                        }
                                    }
                                }.ToList()
                            }
                        });
                        function.AddEntry(new HumidifierParameter {
                            Name = $"Source{sourceSuffix}Permission",
                            Resource = new Humidifier.Lambda.Permission {
                                Action = "lambda:InvokeFunction",
                                SourceArn = FnGetAtt(schedule.FullName, "Arn"),
                                FunctionName = FnGetAtt(function.FullName, "Arn"),
                                Principal = "events.amazonaws.com"
                            }
                        });
                    }
                    break;
                case ApiGatewaySource apiGatewaySource:
                    _apiGatewayRoutes.Add(new ApiRoute {
                        Method = apiGatewaySource.Method,
                        Path = apiGatewaySource.Path,
                        Integration = apiGatewaySource.Integration,
                        Function = function,
                        OperationName = apiGatewaySource.OperationName,
                        ApiKeyRequired = apiGatewaySource.ApiKeyRequired
                    });
                    break;
                case S3Source s3Source:
                    Enumerate(s3Source.Bucket, (suffix, _, arn) => {
                        var permission = function.AddEntry(new HumidifierParameter {
                            Name = $"Source{sourceSuffix}Permission",
                            Resource = new Humidifier.Lambda.Permission {
                                Action = "lambda:InvokeFunction",
                                SourceAccount = FnRef("AWS::AccountId"),
                                SourceArn = arn,
                                FunctionName = FnGetAtt(function.FullName, "Arn"),
                                Principal = "s3.amazonaws.com"
                            }
                        });
                        function.AddEntry(new HumidifierParameter {
                            Name = $"Source{sourceSuffix}Subscription",
                            Resource = new Humidifier.CustomResource("LambdaSharp::S3::Subscription") {
                                ["BucketArn"] = arn,
                                ["FunctionArn"] = FnGetAtt(function.FullName, "Arn"),
                                ["Filters"] = new List<object> {

                                    // TODO (2018-11-18, bjorg): we need to group filters from the same function for the same bucket
                                    ConvertS3Source()
                                }
                            },
                            DependsOn = new[] { permission.FullName }
                        });

                        // local function
                        Dictionary<string, object> ConvertS3Source() {
                            var filter = new Dictionary<string, object> {
                                ["Events"] = s3Source.Events
                            };
                            if(s3Source.Prefix != null) {
                                filter["Prefix"] = s3Source.Prefix;
                            }
                            if(s3Source.Suffix != null) {
                                filter["Suffix"] = s3Source.Suffix;
                            }
                            return filter;
                        }
                    });
                    break;
                case SqsSource sqsSource:
                    Enumerate(sqsSource.Queue, (suffix, _, arn) => {
                        function.AddEntry(new HumidifierParameter {
                            Name = $"Source{sourceSuffix}EventMapping{suffix}",
                            Resource = new Humidifier.Lambda.EventSourceMapping {
                                BatchSize = sqsSource.BatchSize,
                                Enabled = true,
                                EventSourceArn = arn,
                                FunctionName = FnRef(function.FullName)
                            }
                        });
                    });
                    break;
                case AlexaSource alexaSource: {

                        // check if we need to create a conditional expression for a non-literal token
                        var eventSourceToken = alexaSource.EventSourceToken;
                        if(eventSourceToken is string token) {
                            if(token == "*") {
                                eventSourceToken = null;
                            }
                        } else if(eventSourceToken != null) {

                            // create conditional expression toi allow "*" values
                            var condition = $"{function.LogicalId}Source{sourceSuffix}AlexaIsBlank";
                            eventSourceToken = FnIf(
                                condition,
                                FnRef("AWS::NoValue"),
                                alexaSource.EventSourceToken
                            );
                            module.Conditions.Add(condition, FnEquals(alexaSource.EventSourceToken, "*"));
                        }
                        function.AddEntry(new HumidifierParameter {
                            Name = $"Source{sourceSuffix}AlexaPermission",
                            Resource = new Humidifier.Lambda.Permission {
                                Action = "lambda:InvokeFunction",
                                FunctionName = FnGetAtt(function.FullName, "Arn"),
                                Principal = "alexa-appkit.amazon.com",
                                EventSourceToken = eventSourceToken
                            }
                        });
                    }
                    break;
                case DynamoDBSource dynamoDbSource:
                    Enumerate(dynamoDbSource.DynamoDB, (suffix, _, arn) => {
                        function.AddEntry(new HumidifierParameter {
                            Name = $"Source{sourceSuffix}EventMapping{suffix}",
                            Resource = new Humidifier.Lambda.EventSourceMapping {
                                BatchSize = dynamoDbSource.BatchSize,
                                StartingPosition = dynamoDbSource.StartingPosition,
                                Enabled = true,
                                EventSourceArn = arn,
                                FunctionName = FnRef(function.FullName)
                            }
                        });
                    });
                    break;
                case KinesisSource kinesisSource:
                    Enumerate(kinesisSource.Kinesis, (suffix, _, arn) => {
                        function.AddEntry(new HumidifierParameter {
                            Name = $"Source{sourceSuffix}EventMapping{suffix}",
                            Resource = new Humidifier.Lambda.EventSourceMapping {
                                BatchSize = kinesisSource.BatchSize,
                                StartingPosition = kinesisSource.StartingPosition,
                                Enabled = true,
                                EventSourceArn = arn,
                                FunctionName = FnRef(function.FullName)
                            }
                        });
                    });
                    break;
                default:
                    throw new ApplicationException($"unrecognized function source type '{source?.GetType()}' for source #{sourceSuffix}");
                }
            }

            // check if function should be registered
            if(module.HasModuleRegistration && function.Resource.HasFunctionRegistration) {
                function.AddEntry(new HumidifierParameter {
                    Name = "Registration",
                    Resource = new Humidifier.CustomResource("LambdaSharp::Register::Function") {
                        ["ModuleId"] = FnRef("AWS::StackName"),
                        ["FunctionId"] = FnRef(function.FullName),
                        ["FunctionName"] = function.Resource.Name,
                        ["FunctionLogGroupName"] = FnSub($"/aws/lambda/${{{function.LogicalId}}}"),
                        ["FunctionPlatform"] = "AWS Lambda",
                        ["FunctionFramework"] = function.Resource.Function.Runtime,
                        ["FunctionLanguage"] = function.Resource.Language,
                        ["FunctionMaxMemory"] = function.Resource.Function.MemorySize,
                        ["FunctionMaxDuration"] = function.Resource.Function.Timeout
                    },
                    DependsOn = new[] { "Module::Registration" }
                });
                var logSubscription = function.AddEntry(new HumidifierParameter {
                    Name = "LogGroupSubscription",
                    Resource = new Humidifier.Logs.SubscriptionFilter {
                        DestinationArn = FnRef("Module::LoggingStreamArn"),
                        FilterPattern = "-\"*** \"",
                        LogGroupName = FnRef(logGroup.FullName),
                        RoleArn = FnGetAtt("Module::CloudWatchLogsRole", "Arn")
                    }
                });
            }
        }

        private void Enumerate(string fullName, Action<string, AResource, object> action) {
            var entry = _builder.GetEntry(fullName).Resource;
            var variable = FnRef(fullName);
            if(variable is IList list) {
                switch(list.Count) {
                case 0:
                    action("", entry, variable);
                    break;
                case 1:
                    action("", entry, list[0]);
                    break;
                default:
                    for(var i = 0; i < list.Count; ++i) {
                        action((i + 1).ToString(), entry, list[i]);
                    }
                    break;
                }
            } else {
                action("", entry, variable);
            }
        }
   }
}