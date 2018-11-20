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
            public FunctionEntry Function { get; set; }
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
            var moduleEntry = _builder.AddValue(
                parent: null,
                name: "Module",
                description: "Module Variables",
                reference: "",
                scope: null,
                isSecret: false
            );
            _builder.AddValue(
                parent: moduleEntry,
                name: "Id",
                description: "Module ID",
                reference: FnRef("AWS::StackName"),
                scope: null,
                isSecret: false
            );
            _builder.AddValue(
                parent: moduleEntry,
                name: "Name",
                description: "Module Name",
                module.Name,
                scope: null,
                isSecret: false
            );
            _builder.AddValue(
                parent: moduleEntry,
                name: "Version",
                description: "Module Version",
                module.Version.ToString(),
                scope: null,
                isSecret: false
            );

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
                _builder.AddValue(
                    parent: moduleEntry,
                    name: "DeadLetterQueueArn",
                    description: "Module Dead Letter Queue (ARN)",
                    reference: FnRef("LambdaSharp::DeadLetterQueueArn"),
                    scope: null,
                    isSecret: false
                );
                _builder.AddValue(
                    parent: moduleEntry,
                    name: "LoggingStreamArn",
                    description: "Module Logging Stream (ARN)",
                    reference: FnRef("LambdaSharp::LoggingStreamArn"),
                    scope: null,
                    isSecret: false
                );
                _builder.AddValue(
                    parent: moduleEntry,
                    name: "DefaultSecretKeyArn",
                    description: "Module Default Secret Key (ARN)",
                    reference: FnRef("LambdaSharp::DefaultSecretKeyArn"),
                    scope: null,
                    isSecret: false
                );

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
                _builder.AddResource(
                    parent: moduleEntry,
                    name: "Registration",
                    description: null,
                    scope: null,
                    resource: new Humidifier.CustomResource("LambdaSharp::Register::Module") {
                        ["ModuleId"] = FnRef("AWS::StackName"),
                        ["ModuleName"] = module.Name,
                        ["ModuleVersion"] = module.Version.ToString()
                    },
                    dependsOn: null,
                    condition: null
                );
            }

            // create module IAM role used by all functions
            var functions = module.Entries.OfType<FunctionEntry>().ToList();
            if(functions.Any()) {

                // create module role
                _builder.AddResource(
                    parent: moduleEntry,
                    name: "Role",
                    description: null,
                    scope: null,
                    resource: new Humidifier.IAM.Role {
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
                    },
                    dependsOn: null,
                    condition: null
                );

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
                if(functions.Any(function => function.Function.VpcConfig != null)) {
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
                     _builder.AddResource(
                        parent: moduleEntry,
                        name: "CloudWatchLogsRole",
                        description: null,
                        scope: null,
                        resource:  new Humidifier.IAM.Role {
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
                        },
                        dependsOn: null,
                        condition: null
                    );
                }

                // add functions
                foreach(var function in functions) {
                    AddFunction(module, function);
                }

                // check if RestApi resources need to be added
                if(functions.Any(f => f.Sources.OfType<ApiGatewaySource>().Any())) {
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
                        var restApiEntry = _builder.AddResource(
                            parent: moduleEntry,
                            name: "RestApi",
                            description: "Module REST API",
                            scope: null,
                            resource: new Humidifier.ApiGateway.RestApi {
                                Name = FnSub("${AWS::StackName} Module API"),
                                Description = "${Module::Name} API (v${Module::Version})",
                                FailOnWarnings = true
                            },
                            dependsOn: null,
                            condition: null
                        );

                        // add RestApi url
                        _builder.AddValue(
                            restApiEntry,
                            name: "Url",
                            description: "Module REST API URL",
                            scope: null,
                            reference: FnSub("https://${Module::RestApi}.execute-api.${AWS::Region}.${AWS::URLSuffix}/LATEST/"),
                            isSecret: false
                        );

                        // create a RestApi role that can write logs
                        _builder.AddResource(
                            parent: restApiEntry,
                            name: "Role",
                            description: "Module REST API Role",
                            scope: null,
                            resource: new Humidifier.IAM.Role {
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
                            },
                            dependsOn: null,
                            condition: null
                        );

                        // create a RestApi account which uses the RestApi role
                        _builder.AddResource(
                            parent: restApiEntry,
                            name: "Account",
                            description: "Module REST API Account",
                            scope: null,
                            resource: new Humidifier.ApiGateway.Account {
                                CloudWatchRoleArn = FnGetAtt("Module::RestApi::Role", "Arn")
                            },
                            dependsOn: null,
                            condition: null
                        );

                        // NOTE (2018-06-21, bjorg): the RestApi deployment resource depends on ALL methods resources having been created;
                        //  a new name is used for the deployment to force the stage to be updated
                        _builder.AddResource(
                            parent: restApiEntry,
                            name: "Deployment" + methodsHash,
                            description: "Module REST API Deployment",
                            scope: null,
                            resource: new Humidifier.ApiGateway.Deployment {
                                RestApiId = FnRef("Module::RestApi"),
                                Description = FnSub($"${{AWS::StackName}} API [{methodsHash}]")
                            },
                            dependsOn: null, // TODO: depends on all AWS::ApiGateway::Method
                            condition: null
                        );

                        // RestApi stage depends on API gateway deployment and API gateway account
                        // NOTE (2018-06-21, bjorg): the stage resource depends on the account resource having been granted
                        //  the necessary permissions for logging
                        _builder.AddResource(
                            parent: restApiEntry,
                            name: "Stage",
                            description: "Module REST API Stage",
                            scope: null,
                            resource: new Humidifier.ApiGateway.Stage {
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
                            dependsOn: new[] { "Module::RestApi::Account" },
                            condition: null
                        );
                    }
                }
            }
        }

        private void AddApiResource(AModuleEntry parent, string parentPrefix, object restApiId, object parentId, int level, IEnumerable<ApiRoute> routes, List<KeyValuePair<string, object>> apiMethods) {

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
                _builder.AddResource(
                    parent: parent,
                    name: method.Method,
                    description: null,
                    scope: null,
                    resource: apiMethod,
                    dependsOn: null,
                    condition: null
                );
                _builder.AddResource(
                    parent: parent,
                    name: $"{methodName}Permission",
                    description: null,
                    scope: null,
                    resource: new Humidifier.Lambda.Permission {
                        Action = "lambda:InvokeFunction",
                        FunctionName = FnGetAtt(method.Function.FullName, "Arn"),
                        Principal = "apigateway.amazonaws.com",
                        SourceArn = FnSub($"arn:aws:execute-api:${{AWS::Region}}:${{AWS::AccountId}}:${{ModuleRestApi}}/LATEST/{method.Method}/{string.Join("/", method.Path)}")
                    },
                    dependsOn: null,
                    condition: null
                );
            }

            // create new resource for each route with a common path segment
            var subRoutes = routes.Where(route => route.Path.Length > level).ToLookup(route => route.Path[level]);
            foreach(var subRoute in subRoutes) {

                // remove special character from path segment and capitalize it
                var partName = new string(subRoute.Key.Where(c => char.IsLetterOrDigit(c)).ToArray());
                partName = char.ToUpperInvariant(partName[0]) + partName.Substring(1);

                // create a new resource
                var newResourceName = parentPrefix + partName + "Resource";
                var resource = _builder.AddResource(
                    parent: parent,
                    name: newResourceName,
                    description: null,
                    scope: null,
                    resource: new Humidifier.ApiGateway.Resource {
                        RestApiId = restApiId,
                        ParentId = parentId,
                        PathPart = subRoute.Key
                    },
                    dependsOn: null,
                    condition: null
                );
                AddApiResource(resource, parentPrefix + partName, restApiId, FnRef(newResourceName), level + 1, subRoute, apiMethods);
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

        private void AddFunction(Module module, FunctionEntry function) {

            // create function log-group with retention window
            var logGroup = _builder.AddResource(
                parent: function,
                name: "LogGroup",
                description: null,
                scope: null,
                resource: new Humidifier.Logs.LogGroup {
                    LogGroupName = FnSub($"/aws/lambda/${{{function.LogicalId}}}"),

                    // TODO (2018-09-26, bjorg): make retention configurable
                    //  see https://docs.aws.amazon.com/AmazonCloudWatchLogs/latest/APIReference/API_PutRetentionPolicy.html
                    RetentionInDays = 7
                },
                dependsOn: null,
                condition: null
            );

            // add function sources
            for(var sourceIndex = 0; sourceIndex < function.Sources.Count; ++sourceIndex) {
                var source = function.Sources[sourceIndex];
                var sourceSuffix = (sourceIndex + 1).ToString();
                switch(source) {
                case TopicSource topicSource:
                    Enumerate(topicSource.TopicName, (suffix, _, arn) => {
                        _builder.AddResource(
                            parent: function,
                            name: $"Source{sourceSuffix}Subscription{suffix}",
                            description: null,
                            scope: null,
                            resource: new Humidifier.SNS.Subscription {
                                Endpoint = FnGetAtt(function.FullName, "Arn"),
                                Protocol = "lambda",
                                TopicArn = arn
                            },
                            dependsOn: null,
                            condition: null
                        );
                        _builder.AddResource(
                            parent: function,
                            name: $"Source{sourceSuffix}Permission{suffix}",
                            description: null,
                            scope: null,
                            resource: new Humidifier.Lambda.Permission {
                                Action = "lambda:InvokeFunction",
                                SourceArn = arn,
                                FunctionName = FnGetAtt(function.FullName, "Arn"),
                                Principal = "sns.amazonaws.com"
                            },
                            dependsOn: null,
                            condition: null
                        );
                    });
                    break;
                case ScheduleSource scheduleSource: {
                        var schedule = _builder.AddResource(
                            parent: function,
                            name: $"Source{sourceSuffix}ScheduleEvent",
                            description: null,
                            scope: null,
                            resource: new Humidifier.Events.Rule {
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
                            },
                            dependsOn: null,
                            condition: null
                        );
                        _builder.AddResource(
                            parent: function,
                            name: $"Source{sourceSuffix}Permission",
                            description: null,
                            scope: null,
                            resource: new Humidifier.Lambda.Permission {
                                Action = "lambda:InvokeFunction",
                                SourceArn = FnGetAtt(schedule.FullName, "Arn"),
                                FunctionName = FnGetAtt(function.FullName, "Arn"),
                                Principal = "events.amazonaws.com"
                            },
                            dependsOn: null,
                            condition: null
                        );
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
                        var permission = _builder.AddResource(
                            parent: function,
                            name: $"Source{sourceSuffix}Permission",
                            description: null,
                            scope: null,
                            resource: new Humidifier.Lambda.Permission {
                                Action = "lambda:InvokeFunction",
                                SourceAccount = FnRef("AWS::AccountId"),
                                SourceArn = arn,
                                FunctionName = FnGetAtt(function.FullName, "Arn"),
                                Principal = "s3.amazonaws.com"
                            },
                            dependsOn: null,
                            condition: null
                        );
                        _builder.AddResource(
                            parent: function,
                            name: $"Source{sourceSuffix}Subscription",
                            description: null,
                            scope: null,
                            resource: new Humidifier.CustomResource("LambdaSharp::S3::Subscription") {
                                ["BucketArn"] = arn,
                                ["FunctionArn"] = FnGetAtt(function.FullName, "Arn"),
                                ["Filters"] = new List<object> {

                                    // TODO (2018-11-18, bjorg): we need to group filters from the same function for the same bucket
                                    ConvertS3Source()
                                }
                            },
                            dependsOn: new[] { permission.FullName },
                            condition: null
                        );

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
                        _builder.AddResource(
                            parent: function,
                            name: $"Source{sourceSuffix}EventMapping{suffix}",
                            description: null,
                            scope: null,
                            resource: new Humidifier.Lambda.EventSourceMapping {
                                BatchSize = sqsSource.BatchSize,
                                Enabled = true,
                                EventSourceArn = arn,
                                FunctionName = FnRef(function.FullName)
                            },
                            dependsOn: null,
                            condition: null
                        );
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
                        _builder.AddResource(
                            parent: function,
                            name: $"Source{sourceSuffix}AlexaPermission",
                            description: null,
                            scope: null,
                            resource: new Humidifier.Lambda.Permission {
                                Action = "lambda:InvokeFunction",
                                FunctionName = FnGetAtt(function.FullName, "Arn"),
                                Principal = "alexa-appkit.amazon.com",
                                EventSourceToken = eventSourceToken
                            },
                            dependsOn: null,
                            condition: null
                        );
                    }
                    break;
                case DynamoDBSource dynamoDbSource:
                    Enumerate(dynamoDbSource.DynamoDB, (suffix, _, arn) => {
                        _builder.AddResource(
                            parent: function,
                            name: $"Source{sourceSuffix}EventMapping{suffix}",
                            description: null,
                            scope: null,
                            resource: new Humidifier.Lambda.EventSourceMapping {
                                BatchSize = dynamoDbSource.BatchSize,
                                StartingPosition = dynamoDbSource.StartingPosition,
                                Enabled = true,
                                EventSourceArn = arn,
                                FunctionName = FnRef(function.FullName)
                            },
                            dependsOn: null,
                            condition: null
                        );
                    });
                    break;
                case KinesisSource kinesisSource:
                    Enumerate(kinesisSource.Kinesis, (suffix, _, arn) => {
                        _builder.AddResource(
                            parent: function,
                            name: $"Source{sourceSuffix}EventMapping{suffix}",
                            description: null,
                            scope: null,
                            resource: new Humidifier.Lambda.EventSourceMapping {
                                BatchSize = kinesisSource.BatchSize,
                                StartingPosition = kinesisSource.StartingPosition,
                                Enabled = true,
                                EventSourceArn = arn,
                                FunctionName = FnRef(function.FullName)
                            },
                            dependsOn: null,
                            condition: null
                        );
                    });
                    break;
                default:
                    throw new ApplicationException($"unrecognized function source type '{source?.GetType()}' for source #{sourceSuffix}");
                }
            }

            // check if function should be registered
            if(module.HasModuleRegistration && function.HasFunctionRegistration) {
                _builder.AddResource(
                    parent: function,
                    name: "Registration",
                    description: null,
                    scope: null,
                    resource: new Humidifier.CustomResource("LambdaSharp::Register::Function") {
                        ["ModuleId"] = FnRef("AWS::StackName"),
                        ["FunctionId"] = FnRef(function.FullName),
                        ["FunctionName"] = function.Name,
                        ["FunctionLogGroupName"] = FnSub($"/aws/lambda/${{{function.LogicalId}}}"),
                        ["FunctionPlatform"] = "AWS Lambda",
                        ["FunctionFramework"] = function.Function.Runtime,
                        ["FunctionLanguage"] = function.Language,
                        ["FunctionMaxMemory"] = function.Function.MemorySize,
                        ["FunctionMaxDuration"] = function.Function.Timeout
                    },
                    dependsOn: new[] { "Module::Registration" },
                    condition: null
                );
                var logSubscription = _builder.AddResource(
                    parent: function,
                    name: "LogGroupSubscription",
                    description: null,
                    scope: null,
                    resource: new Humidifier.Logs.SubscriptionFilter {
                        DestinationArn = FnRef("Module::LoggingStreamArn"),
                        FilterPattern = "-\"*** \"",
                        LogGroupName = FnRef(logGroup.FullName),
                        RoleArn = FnGetAtt("Module::CloudWatchLogsRole", "Arn")
                    },
                    dependsOn: null,
                    condition: null
                );
            }
        }

        private void Enumerate(string fullName, Action<string, AModuleEntry, object> action) {
            var entry = _builder.GetEntry(fullName);
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