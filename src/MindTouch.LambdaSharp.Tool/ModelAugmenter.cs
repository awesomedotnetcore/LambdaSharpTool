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

namespace Humidifier {

    public class Principal {

        //--- Fields ---
        public object Service;
    }
}

namespace MindTouch.LambdaSharp.Tool {

    public class ModelAugmenter : AModelProcessor {

        //--- Types ---
        private class ApiRoute {

            //--- Properties ---
            public string Method { get; set; }
            public string[] Path { get; set; }
            public ApiGatewaySourceIntegration Integration { get; set; }
            public Function Function { get; set; }
            public string OperationName { get; set; }
            public bool? ApiKeyRequired { get; set; }
        }

        //--- Fields ---
        private ModuleBuilder _module;
        private List<ApiRoute> _apiGatewayRoutes;

        //--- Constructors ---
        public ModelAugmenter(Settings settings, string sourceFilename) : base(settings, sourceFilename) { }

        //--- Methods ---
        public void Augment(Module module) {
            _module = new ModuleBuilder(Settings, SourceFilename, module);

            // add module variables
            var moduleValue = _module.AddEntry(null, new ValueParameter {
                Name = "Module",
                Reference = ""
            });
            _module.AddVariable("Module::Id", FnRef("AWS::StackName"));
            _module.AddVariable("Module::Name", module.Name);
            _module.AddVariable("Module::Version", module.Version.ToString());

            // add LambdaSharp Module Options
            var section = "LambdaSharp Module Options";
            _module.AddInput(
                name: "Secrets",
                section: section,
                label: "Secret Keys (ARNs)",
                description: "Comma-separated list of optional secret keys",
                defaultValue: ""
            );

            // add decryption permission for secret
            _module.AddResourceStatement(new Humidifier.Statement {
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
            _module.AddCondition("SecretsIsEmpty", FnEquals(FnRef("Secrets"), ""));

            // add standard parameters (unless requested otherwise)
            if(!_module.HasPragma("no-lambdasharp-dependencies")) {

                // add LambdaSharp Module Internal Dependencies
                section = "LambdaSharp Dependencies";
                _module.AddImport(
                    import: "LambdaSharp::DeadLetterQueueArn",
                    section: section,
                    label: "Dead Letter Queue (ARN)",
                    description: "Dead letter queue for functions"
                );
                _module.AddImport(
                    import: "LambdaSharp::LoggingStreamArn",
                    section: section,
                    label: "Logging Stream (ARN)",
                    description: "Logging kinesis stream for functions"
                );
                _module.AddImport(
                    import: "LambdaSharp::DefaultSecretKeyArn",
                    section: section,
                    label: "Secret Key (ARN)",
                    description: "Default secret key for functions"
                );
                _module.AddSecret(FnRef("Module::DefaultSecretKeyArn"));

                // add lambdasharp imports
                _module.AddVariable("Module::DeadLetterQueueArn", FnRef("LambdaSharp::DeadLetterQueueArn"));
                _module.AddVariable("Module::LoggingStreamArn", FnRef("LambdaSharp::LoggingStreamArn"));
                _module.AddVariable("Module::DefaultSecretKeyArn", FnRef("LambdaSharp::DefaultSecretKeyArn"));

                // permissions needed for dead-letter queue
                _module.AddResourceStatement(new Humidifier.Statement {
                    Sid = "ModuleDeadLetterQueueLogging",
                    Effect = "Allow",
                    Resource = _module.GetVariable("Module::DeadLetterQueueArn").Reference,
                    Action = new List<string> {
                        "sqs:SendMessage"
                    }
                });
            }

            // add LambdaSharp Deployment Settings
            section = "LambdaSharp Deployment Settings (DO NOT MODIFY)";
            _module.AddInput(
                name: "DeploymentBucketName",
                section: section,
                label: "Deployment S3 Bucket",
                description: "Source deployment S3 bucket name"
            );
            _module.AddInput(
                name: "DeploymentPrefix",
                section: section,
                label: "Deployment Prefix",
                description: "Module deployment prefix"
            );
            _module.AddInput(
                name: "DeploymentPrefixLowercase",
                section: section,
                label: "Deployment Prefix (lowercase)",
                description: "Module deployment prefix (lowercase)"
            );
            _module.AddInput(
                name: "DeploymentParent",
                section: section,
                label: "Parent Stack Name",
                description: "Parent stack name for nested deployments, blank otherwise",
                defaultValue: ""
            );

            // add module registration
            if(module.HasModuleRegistration) {
                _module.AddEntry(moduleValue, new CloudFormationResourceParameter {
                    Name = "Registration",
                    Resource = CreateResource("LambdaSharp::Register::Module", new Dictionary<string, object> {
                        ["ModuleId"] = FnRef("AWS::StackName"),
                        ["ModuleName"] = module.Name,
                        ["ModuleVersion"] = module.Version.ToString()
                    })
                });
            }

            // create module IAM role used by all functions
            var functions = module.GetAllResources().OfType<Function>();
            if(functions.Any()) {

                // create module role
                _module.AddEntry(moduleValue, new HumidifierParameter {
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

                // create generic resource statement; additional resource statements can be added by resources
                _module.AddResourceStatement(new Humidifier.Statement {
                    Sid = "ModuleLogStreamAccess",
                    Effect = "Allow",
                    Resource = "arn:aws:logs:*:*:*",
                    Action = new List<string> {
                        "logs:CreateLogStream",
                        "logs:PutLogEvents"
                    }
                });

                // permissions needed for lambda functions to exist in a VPC
                if(functions.Any(function => function.VPC != null)) {
                    _module.AddResourceStatement(new Humidifier.Statement {
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
                     var cloudwatchLogsRole = _module.AddEntry(moduleValue, new HumidifierParameter {
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
                                                Resource = _module.GetVariable("Module::LoggingStreamArn").Reference
                                            }
                                        }.ToList()
                                    }
                                }
                            }.ToList()
                        }
                    });

                    foreach(var function in functions.Where(f => f.HasFunctionRegistration).ToList()) {
                        _module.AddEntry(function, new CloudFormationResourceParameter {
                            Name = "Registration",
                            Resource = CreateResource("LambdaSharp::Register::Function", new Dictionary<string, object> {
                                ["ModuleId"] = FnRef("AWS::StackName"),
                                ["FunctionId"] = FnRef(function.Name),
                                ["FunctionName"] = function.Name,
                                ["FunctionLogGroupName"] = FnSub($"/aws/lambda/${{{function.Name}}}"),
                                ["FunctionPlatform"] = "AWS Lambda",
                                ["FunctionFramework"] = function.Runtime,
                                ["FunctionLanguage"] = function.Language,
                                ["FunctionMaxMemory"] = function.Memory,
                                ["FunctionMaxDuration"] = function.Timeout
                            }, dependsOn: new List<string> { "Module::Registration" })
                        });

                        // create function log-group with retention window
                        var logGroup = _module.AddEntry(function, new HumidifierParameter {
                            Name = "LogGroup",
                            Resource = new Humidifier.Logs.LogGroup {
                                LogGroupName = FnSub($"/aws/lambda/${{{function.LogicalId}}}"),

                                // TODO (2018-09-26, bjorg): make retention configurable
                                //  see https://docs.aws.amazon.com/AmazonCloudWatchLogs/latest/APIReference/API_PutRetentionPolicy.html
                                RetentionInDays = 7
                            }
                        });
                        var logSubscription = _module.AddEntry(function, new HumidifierParameter {
                            Name = "LogGroupSubscription",
                            Resource = new Humidifier.Logs.SubscriptionFilter {
                                DestinationArn = FnRef("Module::LoggingStreamArn"),
                                FilterPattern = "-\"*** \"",
                                LogGroupName = FnRef(logGroup.FullName),
                                RoleArn = FnGetAtt(cloudwatchLogsRole.FullName, "Arn")
                            }
                        });
                    }
                }
            }

            // check if RestApi resources need to be added
            if(module.Resources.OfType<Function>().Any(function => function.Sources.OfType<ApiGatewaySource>().Any())) {
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
                    var restApiVar = _module.AddEntry(moduleValue, new HumidifierParameter {
                        Name = "RestApi",
                        Description = "Module REST API",
                        Resource = new Humidifier.ApiGateway.RestApi {
                            Name = FnSub("${AWS::StackName} Module API"),
                            Description = "${Module::Name} API (v${Module::Version})",
                            FailOnWarnings = true
                        }
                    });

                    // add RestApi url
                    _module.AddVariable("Module::RestApi::Url", FnSub("https://${Module::RestApi}.execute-api.${AWS::Region}.${AWS::URLSuffix}/LATEST/"));

                    // create a RestApi role that can write logs
                    _module.AddEntry(restApiVar, new HumidifierParameter {
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
                    _module.AddEntry(restApiVar, new HumidifierParameter {
                        Name = "Account",
                        Description = "Module REST API Account",
                        Resource = new Humidifier.ApiGateway.Account {
                            CloudWatchRoleArn = FnGetAtt("Module::RestApi::Role", "Arn")
                        }
                    });

                    // NOTE (2018-06-21, bjorg): the RestApi deployment resource depends on ALL methods resources having been created;
                    //  a new name is used for the deployment to force the stage to be updated
                    _module.AddEntry(restApiVar, new HumidifierParameter {
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
                    _module.AddEntry(restApiVar, new HumidifierParameter {
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

        private void AddApiResource(AResource parent, string parentPrefix, object restApiId, object parentId, int level, IEnumerable<ApiRoute> routes, List<KeyValuePair<string, object>> apiMethods) {

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
                _module.AddEntry(parent, new HumidifierParameter {
                    Name = method.Method,
                    Resource = apiMethod
                });
                _module.AddEntry(parent, new HumidifierParameter {
                    Name = $"{method.Function.Name}{methodName}Permission",
                    Resource = new Humidifier.Lambda.Permission {
                        Action = "lambda:InvokeFunction",
                        FunctionName = FnGetAtt(method.Function.Name, "Arn"),
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
                var resource = _module.AddEntry(parent, new HumidifierParameter {
                    Name = newResourceName,
                    Resource = new Humidifier.ApiGateway.Resource {
                        RestApiId = restApiId,
                        ParentId = parentId,
                        PathPart = subRoute.Key
                    }
                });
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
                                ["Arn"] = FnGetAtt(method.Function.Name, "Arn")
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

        private void AddFunction(Module module, Function function) {

            // check if function has any SNS topic event sources
            foreach(var topicSource in function.Sources.OfType<TopicSource>()) {
                Enumerate(topicSource.TopicName, (suffix, parameter, arn) => {
                    _module.AddEntry(function, new HumidifierParameter {
                        Name = $"{parameter.LogicalId}SnsPermission{suffix}",
                        Resource = new Humidifier.Lambda.Permission {
                            Action = "lambda:InvokeFunction",
                            SourceArn = arn,
                            FunctionName = FnGetAtt(function.Name, "Arn"),
                            Principal = "sns.amazonaws.com"
                        }
                    });
                    _module.AddEntry(function, new HumidifierParameter {
                        Name = $"{parameter.LogicalId}Subscription{suffix}",
                        Resource = new Humidifier.SNS.Subscription {
                            Endpoint = FnGetAtt(function.Name, "Arn"),
                            Protocol = "lambda",
                            TopicArn = arn
                        }
                    });
                });
            }

            // check if function has any API gateway event sources
            var scheduleSources = function.Sources.OfType<ScheduleSource>().ToList();
            if(scheduleSources.Any()) {
                for(var i = 0; i < scheduleSources.Count; ++i) {
                    var name = "ScheduleEvent" + (i + 1).ToString();
                    _module.AddEntry(function, new HumidifierParameter {
                        Name = name,
                        Resource = new Humidifier.Events.Rule {
                            ScheduleExpression = scheduleSources[i].Expression,
                            Targets = new[] {
                                new Humidifier.Events.RuleTypes.Target {
                                    Id = FnSub("${AWS::StackName}" + name),
                                    Arn = FnGetAtt(function.Name, "Arn"),
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
    ""tName"": """ + scheduleSources[i].Name + @"""
    }"
                                    }
                                }
                            }.ToList()
                        }
                    });
                    _module.AddEntry(function, new HumidifierParameter {
                        Name = name + "Permission",
                        Resource = new Humidifier.Lambda.Permission {
                            Action = "lambda:InvokeFunction",
                            SourceArn = FnGetAtt(name, "Arn"),
                            FunctionName = FnGetAtt(function.Name, "Arn"),
                            Principal = "events.amazonaws.com"
                        }
                    });
                }
            }

            // check if function has any API gateway event sources
            var apiSources = function.Sources.OfType<ApiGatewaySource>().ToList();
            if(apiSources.Any()) {
                foreach(var apiEvent in apiSources) {
                    _apiGatewayRoutes.Add(new ApiRoute {
                        Method = apiEvent.Method,
                        Path = apiEvent.Path,
                        Integration = apiEvent.Integration,
                        Function = function,
                        OperationName = apiEvent.OperationName,
                        ApiKeyRequired = apiEvent.ApiKeyRequired
                    });
                }
            }

            // check if function has any S3 event sources
            var s3Sources = function.Sources.OfType<S3Source>().ToList();
            if(s3Sources.Any()) {
                foreach(var grp in s3Sources
                    .Select(source => new {
                        FullName = source.Bucket,
                        Source = source
                    }).ToLookup(tuple => tuple.FullName)
                ) {
                    Enumerate(grp.Key, (suffix, parameter, arn) => {
                        var functionS3Permission = $"{parameter.LogicalId}S3Permission";
                        var functionS3Subscription = $"{parameter.LogicalId}S3Subscription";
                        _module.AddEntry(function, new HumidifierParameter {
                            Name = functionS3Permission + suffix,
                            Resource = new Humidifier.Lambda.Permission {
                                Action = "lambda:InvokeFunction",
                                SourceAccount = FnRef("AWS::AccountId"),
                                SourceArn = arn,
                                FunctionName = FnGetAtt(function.Name, "Arn"),
                                Principal = "s3.amazonaws.com"
                            }
                        });
                        _module.AddEntry(function, new CloudFormationResourceParameter {
                            Name = functionS3Subscription + suffix,
                            Resource = CreateResource("LambdaSharp::S3::Subscription", new Dictionary<string, object> {
                                ["BucketArn"] = arn,
                                ["FunctionArn"] = FnGetAtt(function.Name, "Arn"),
                                ["Filters"] = grp.Select(tuple => {
                                    var filter = new Dictionary<string, object>() {
                                        ["Events"] = tuple.Source.Events,
                                    };
                                    if(tuple.Source.Prefix != null) {
                                        filter["Prefix"] = tuple.Source.Prefix;
                                    }
                                    if(tuple.Source.Suffix != null) {
                                        filter["Suffix"] = tuple.Source.Suffix;
                                    }
                                    return filter;
                                }).ToArray()
                            }, dependsOn: new List<string> { functionS3Permission })
                        });
                    });
                }
            }

            // check if function has any SQS event sources
            var sqsSources = function.Sources.OfType<SqsSource>().ToList();
            if(sqsSources.Any()) {
                foreach(var source in sqsSources) {
                    Enumerate(source.Queue, (suffix, parameter, arn) => {
                        _module.AddEntry(function, new HumidifierParameter {
                            Name = $"{parameter.LogicalId}EventMapping{suffix}",
                            Resource = new Humidifier.Lambda.EventSourceMapping {
                                BatchSize = source.BatchSize,
                                Enabled = true,
                                EventSourceArn = arn,
                                FunctionName = FnRef(function.Name)
                            }
                        });
                    });
                }
            }

            // check if function has any Alexa event sources
            var alexaSources = function.Sources.OfType<AlexaSource>().ToList();
            if(alexaSources.Any()) {
                var index = 0;
                foreach(var source in alexaSources) {
                    ++index;
                    var suffix = index.ToString();

                    // check if we need to create a conditional expression for a non-literal token
                    var eventSourceToken = source.EventSourceToken;
                    if(eventSourceToken is string token) {
                        if(token == "*") {
                            eventSourceToken = null;
                        }
                    } else if(eventSourceToken != null) {
                        var condition = $"{function.Name}AlexaIsBlank{suffix}";
                        eventSourceToken = FnIf(
                            condition,
                            FnRef("AWS::NoValue"),
                            source.EventSourceToken
                        );
                        module.Conditions.Add(condition, FnEquals(source.EventSourceToken, "*"));
                    }
                    _module.AddEntry(function, new HumidifierParameter {
                        Name = $"AlexaPermission{suffix}",
                        Resource = new Humidifier.Lambda.Permission {
                            Action = "lambda:InvokeFunction",
                            FunctionName = FnGetAtt(function.Name, "Arn"),
                            Principal = "alexa-appkit.amazon.com",
                            EventSourceToken = eventSourceToken
                        }
                    });
                }
            }

            // check if function has any DynamoDB event sources
            var dynamoDbSources = function.Sources.OfType<DynamoDBSource>().ToList();
            if(dynamoDbSources.Any()) {
                foreach(var source in dynamoDbSources) {
                    Enumerate(source.DynamoDB, (suffix, parameter, arn) => {
                        _module.AddEntry(function, new HumidifierParameter {
                            Name = $"{parameter.LogicalId}EventMapping{suffix}",
                            Resource = new Humidifier.Lambda.EventSourceMapping {
                                BatchSize = source.BatchSize,
                                StartingPosition = source.StartingPosition,
                                Enabled = true,
                                EventSourceArn = arn,
                                FunctionName = FnRef(function.Name)
                            }
                        });
                    });
                }
            }

            // check if function has any Kinesis event sources
            var kinesisSources = function.Sources.OfType<KinesisSource>().ToList();
            if(kinesisSources.Any()) {
                foreach(var source in kinesisSources) {
                    Enumerate(source.Kinesis, (suffix, parameter, arn) => {
                        _module.AddEntry(function, new HumidifierParameter {
                            Name = $"{parameter.LogicalId}EventMapping{suffix}",
                            Resource = new Humidifier.Lambda.EventSourceMapping {
                                BatchSize = source.BatchSize,
                                StartingPosition = source.StartingPosition,
                                Enabled = true,
                                EventSourceArn = arn,
                                FunctionName = FnRef(function.Name)
                            }
                        });
                    });
                }
            }
        }

        private void EnumerateOrDefault(IList<object> items, object single, Action<string, object> callback) {
            switch(items.Count) {
            case 0:
                callback("", single);
                break;
            case 1:
                callback("", items.First());
                break;
            default:
                for(var i = 0; i < items.Count; ++i) {
                    callback((i + 1).ToString(), items[i]);
                }
                break;
            }
        }

        private void Enumerate(string fullName, Action<string, AResource, object> action) {
            var entry = _module.GetEntry(fullName);
            var variable = _module.GetVariable(fullName);
            if(variable.Reference is IList list) {
                switch(list.Count) {
                case 0:
                    action("", entry, variable.Reference);
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
                action("", entry, variable.Reference);
            }
        }
   }
}