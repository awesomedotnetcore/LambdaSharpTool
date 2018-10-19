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
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Humidifier;
using Humidifier.Json;
using Humidifier.Logs;
using MindTouch.LambdaSharp.Tool.Internal;
using MindTouch.LambdaSharp.Tool.Model;
using Newtonsoft.Json;

namespace MindTouch.LambdaSharp.Tool {
    using ApiGateway = Humidifier.ApiGateway;
    using Events = Humidifier.Events;
    using IAM = Humidifier.IAM;
    using Lambda = Humidifier.Lambda;
    using SNS = Humidifier.SNS;

    public class ModelGenerator : AModelProcessor {

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

        //--- Class Methods ---
        void EnumerateOrDefault(IList<object> items, object single, Action<string, object> callback) {
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

        //--- Fields ---

        private Module _module;
        private Stack _stack;
        private List<Statement> _resourceStatements;
        private List<ApiRoute> _apiGatewayRoutes;

        //--- Constructors ---
        public ModelGenerator(Settings settings, string sourceFilename) : base(settings, sourceFilename) { }

        //--- Methods ---
        public string Generate(Module module) {
            _module = module;

            // stack header
            _stack = new Stack {
                AWSTemplateFormatVersion = "2010-09-09",
                Description = _module.Description
            };

            // add module registration
            if(_module.HasModuleRegistration) {
                _stack.Add("ModuleRegistration", new LambdaSharpResource("LambdaSharp::Register::Module") {
                    ["Tier"] = Fn.Ref("Tier"),
                    ["ModuleId"] = Fn.Ref("AWS::StackName"),
                    ["ModuleName"] = _module.Name,
                    ["ModuleVersion"] = _module.Version.ToString()
                });
            }

            // create generic resource statement; additional resource statements can be added by resources
            _resourceStatements = new List<Statement> {
                new Statement {
                    Sid = "ModuleLogStreamAccess",
                    Effect = "Allow",

                    // TODO (2018-10-09, bjorg): we should be able to make the resource target a lot more
                    //  specific since we know all the function names already; and we create the log groups
                    //  for them.
                    Resource = "arn:aws:logs:*:*:*",
                    Action = new List<string> {
                        "logs:CreateLogStream",
                        "logs:PutLogEvents"
                    }
                }
            };

            // add decryption permission for requested keys
            _resourceStatements.Add(new Statement {
                Sid = "ModuleSecretsDecryption",
                Effect = "Allow",
                Resource = FnSplit(
                    ",",
                    FnIf(
                        "ModuleSecretsIsEmpty",
                        FnJoin(",", _module.Secrets),
                        FnJoin(
                            ",",
                            new List<object> {
                                FnJoin(",", _module.Secrets),
                                FnRef("ModuleSecrets")
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
            _stack.Add("ModuleSecretsIsEmpty", new Condition(Fn.Equals(Fn.Ref("ModuleSecrets"), "")));

            // add parameters
            var environmentRefVariables = new Dictionary<string, object>();
            foreach(var parameter in _module.Parameters) {
                AddParameter(parameter, "", environmentRefVariables);
            }

            // check if we need to create a module IAM role (only needed by functions)
            if(_module.Functions.Any()) {
                _apiGatewayRoutes = new List<ApiRoute>();

                // permissions needed for dead-letter queue
                _resourceStatements.Add(new Statement {
                    Sid = "ModuleDeadLetterQueueLogging",
                    Effect = "Allow",
                    Resource = _module.GetParameter("LambdaSharp::DeadLetterQueueArn").Reference,
                    Action = new List<string> {
                        "sqs:SendMessage"
                    }
                });

                // permissions needed for lambda functions to exist in a VPC
                if(_module.Functions.Any(function => function.VPC != null)) {
                    _resourceStatements.Add(new Statement {
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

                // create CloudWatch Logs IAM role to invoke kinesis stream
                _stack.Add("CloudWatchLogsRole", new IAM.Role {
                    AssumeRolePolicyDocument = new PolicyDocument {
                        Version = "2012-10-17",
                        Statement = new List<Statement> {
                            new Statement {
                                Sid = "CloudWatchLogsKinesisInvocation",
                                Effect = "Allow",
                                Principal = new {
                                    Service = Fn.Sub("logs.${AWS::Region}.amazonaws.com")
                                },
                                Action = "sts:AssumeRole"
                            }
                        }
                    },
                    Policies = new List<IAM.Policy> {
                        new IAM.Policy {
                            PolicyName = Fn.Sub("${AWS::StackName}ModuleCloudWatchLogsPolicy"),
                            PolicyDocument = new PolicyDocument {
                                Version = "2012-10-17",
                                Statement = new List<Statement> {
                                    new Statement {
                                        Sid = "CloudWatchLogsKinesisPermissions",
                                        Effect = "Allow",
                                        Action = "kinesis:PutRecord",
                                        Resource = _module.GetParameter("LambdaSharp::LoggingStreamArn").Reference
                                    }
                                }
                            }
                        }
                    }
                });

                // create module IAM role used by all functions
                _stack.Add("ModuleRole", new IAM.Role {
                    AssumeRolePolicyDocument = new PolicyDocument {
                        Version = "2012-10-17",
                        Statement = new List<Statement> {
                            new Statement {
                                Sid = "ModuleLambdaInvocation",
                                Effect = "Allow",
                                Principal = new {
                                    Service = "lambda.amazonaws.com"
                                },
                                Action = "sts:AssumeRole"
                            }
                        }
                    },
                    Policies = new List<IAM.Policy> {
                        new IAM.Policy {
                            PolicyName = Fn.Sub("${AWS::StackName}ModulePolicy"),
                            PolicyDocument = new PolicyDocument {
                                Version = "2012-10-17",

                                // NOTE: additional resource statements can be added by resources
                                Statement = _resourceStatements
                            }
                        }
                    }
                });
                foreach(var function in _module.Functions) {
                    AddFunction(function, environmentRefVariables);
                }

                // check if an API gateway needs to be created
                if(_apiGatewayRoutes.Any()) {

                    // create a RestApi
                    var restApiName = "ModuleRestApi";
                    var restApiDescription = $"{_module.Name} API (v{_module.Version})";
                    _stack.Add(restApiName, new ApiGateway.RestApi {
                        Name = Fn.Sub("${AWS::StackName} Module API"),
                        Description = restApiDescription,
                        FailOnWarnings = true
                    });

                    // add output parameter to easily located API
                    _stack.Add("ModuleRestApi", new Humidifier.Output {
                        Description = restApiDescription,
                        Value = Fn.Sub("https://${ModuleRestApi}.execute-api.${AWS::Region}.${AWS::URLSuffix}/LATEST/")
                    });

                    // create a RestApi role that can write logs
                    var restApiRoleName = restApiName + "Role";
                    _stack.Add(restApiRoleName, new IAM.Role {
                        AssumeRolePolicyDocument = new PolicyDocument {
                            Version = "2012-10-17",
                            Statement = new List<Statement> {
                                new Statement {
                                    Sid = "ModuleRestApiInvocation",
                                    Effect = "Allow",
                                    Principal = new {
                                        Service = "apigateway.amazonaws.com"
                                    },
                                    Action = "sts:AssumeRole"
                                }
                            }
                        },
                        Policies = new List<IAM.Policy> {
                            new IAM.Policy {
                                PolicyName = Fn.Sub("${AWS::StackName}ModuleRestApiPolicy"),
                                PolicyDocument = new PolicyDocument {
                                    Version = "2012-10-17",
                                    Statement = new List<Statement> {
                                        new Statement {
                                            Sid = "ModuleRestApiLogging",
                                            Effect = "Allow",
                                            Action = new List<string> {
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
                                    }
                                }
                            }
                        }
                    });

                    // create a RestApi account which uses the RestApi role
                    var restApiAccountName = restApiName + "Account";
                    _stack.Add(restApiAccountName, new ApiGateway.Account {
                        CloudWatchRoleArn = Fn.GetAtt(restApiRoleName, "Arn")
                    });

                    // recursively create resources as needed
                    var apiMethods = new List<KeyValuePair<string, ApiGateway.Method>>();
                    AddApiResource(restApiName, Fn.Ref(restApiName), Fn.GetAtt(restApiName, "RootResourceId"), 0, _apiGatewayRoutes, apiMethods);

                    // RestApi deployment depends on all methods and their hash (to force redeployment in case of change)
                    var methodSignature = string.Join("\n", apiMethods
                        .OrderBy(kv => kv.Key)
                        .Select(kv => JsonConvert.SerializeObject(kv.Value))
                    );
                    string methodsHash = methodSignature.ToMD5Hash();
                    var restApiDeploymentName = restApiName + "Deployment" + methodsHash;

                    // NOTE (2018-06-21, bjorg): the RestApi deployment resource depends on ALL methods resources having been created
                    _stack.Add(restApiDeploymentName, new ApiGateway.Deployment {
                        RestApiId = Fn.Ref(restApiName),
                        Description = Fn.Sub($"${{AWS::StackName}} API [{methodsHash}]")
                    }, dependsOn: apiMethods.Select(kv => kv.Key).ToArray());

                    // RestApi stage depends on API gateway deployment and API gateway account
                    // NOTE (2018-06-21, bjorg): the stage resource depends on the account resource having been granted
                    //  the necessary permissions for logging
                    var restApiStageName = restApiName + "Stage";
                    _stack.Add(restApiStageName, new ApiGateway.Stage {
                        RestApiId = Fn.Ref(restApiName),
                        DeploymentId = Fn.Ref(restApiDeploymentName),
                        StageName = "LATEST",
                        MethodSettings = new List<ApiGateway.StageTypes.MethodSetting> {
                            new ApiGateway.StageTypes.MethodSetting {
                                DataTraceEnabled = true,
                                HttpMethod = "*",
                                LoggingLevel = "INFO",
                                ResourcePath = "/*"
                            }
                        }
                    }, dependsOn: new[] { restApiAccountName });
                }
            }

            // add outputs
            foreach(var output in module.Outputs) {
                switch(output) {
                case StackOutput stackOutput:
                    _stack.Add(stackOutput.Name, new Humidifier.Output {
                        Description = stackOutput.Description,
                        Value = stackOutput.Value
                    });
                    break;
                case ExportOutput exportOutput:
                    _stack.Add(exportOutput.ExportName, new Humidifier.Output {
                        Description = exportOutput.Description,
                        Value = exportOutput.Value,
                        Export = new Dictionary<string, dynamic> {
                            ["Name"] = Fn.Sub($"${{AWS::StackName}}::{exportOutput.ExportName}")
                        }
                    });
                    break;
                case CustomResourceHandlerOutput customResourceHandlerOutput:
                    _stack.Add($"{new string(customResourceHandlerOutput.CustomResourceName.Where(char.IsLetterOrDigit).ToArray())}Handler", new Humidifier.Output {
                        Description = customResourceHandlerOutput.Description,
                        Value = Fn.Ref(customResourceHandlerOutput.Handler),
                        Export = new Dictionary<string, dynamic> {
                            ["Name"] = Fn.Sub($"${{Tier}}-CustomResource-{customResourceHandlerOutput.CustomResourceName}")
                        }
                    });
                    break;
                default:
                    throw new InvalidOperationException($"cannot generate output for this type: {output?.GetType()}");
                }

            }

            // generate JSON template
            var template = new JsonStackSerializer().Serialize(_stack);

            // NOTE (2018-10-14, bjorg): Humidifier doesn't support adding the Metadata section yet; so
            //  we have to do by deserializing the document, adding it manually, and then serialize everything again.

            // add interface for presenting inputs
            var json = JsonConvert.DeserializeObject<Dictionary<string, object>>(template);
            json.Add("Metadata", new Dictionary<string, object> {
                ["AWS::CloudFormation::Interface"] = new Dictionary<string, object> {
                    ["ParameterLabels"] = _module.GetAllParameters().OfType<AInputParameter>().ToDictionary(input => input.ResourceName, input => new Dictionary<string, object> {
                        ["default"] = input.Label
                    }),
                    ["ParameterGroups"] = _module.GetAllParameters().OfType<AInputParameter>()
                        .GroupBy(input => input.Section)
                        .Select(section => new Dictionary<string, object> {
                            ["Label"] = new Dictionary<string, string> {
                                ["default"] = section.Key
                            },
                            ["Parameters"] = section.Select(input => input.ResourceName).ToList()
                        }
                    )
                }
            });
            template = JsonConvert.SerializeObject(json, Formatting.Indented);
            return template;

            // local functions
            void AddApiResource(string parentPrefix, object restApiId, object parentId, int level, IEnumerable<ApiRoute> routes, List<KeyValuePair<string, ApiGateway.Method>> apiMethods) {

                // attach methods to resource id
                var methods = routes.Where(route => route.Path.Length == level).ToArray();
                foreach(var method in methods) {
                    var methodName = parentPrefix + method.Method;
                    ApiGateway.Method apiMethod;
                    switch(method.Integration) {
                    case ApiGatewaySourceIntegration.RequestResponse:
                        apiMethod = new ApiGateway.Method {
                            AuthorizationType = "NONE",
                            HttpMethod = method.Method,
                            OperationName = method.OperationName,
                            ApiKeyRequired = method.ApiKeyRequired,
                            ResourceId = parentId,
                            RestApiId = restApiId,
                            Integration = new ApiGateway.MethodTypes.Integration {
                                Type = "AWS_PROXY",
                                IntegrationHttpMethod = "POST",
                                Uri = Fn.Sub(
                                    "arn:aws:apigateway:${AWS::Region}:lambda:path/2015-03-31/functions/${Arn}/invocations",
                                    new Dictionary<string, dynamic> {
                                        ["Arn"] = Fn.GetAtt(method.Function.Name, "Arn")
                                    }
                                )
                            }
                        };
                        break;
                    case ApiGatewaySourceIntegration.SlackCommand:

                        // NOTE (2018-06-06, bjorg): Slack commands have a 3sec timeout on invocation, which is rarely good enough;
                        // instead we wire Slack command requests up as asynchronous calls; this way, we can respond with
                        // a callback later and the integration works well all the time.
                        apiMethod = new ApiGateway.Method {
                            AuthorizationType = "NONE",
                            HttpMethod = method.Method,
                            OperationName = method.OperationName,
                            ApiKeyRequired = method.ApiKeyRequired,
                            ResourceId = parentId,
                            RestApiId = restApiId,
                            Integration = new ApiGateway.MethodTypes.Integration {
                                Type = "AWS",
                                IntegrationHttpMethod = "POST",
                                Uri = Fn.Sub(
                                    "arn:aws:apigateway:${AWS::Region}:lambda:path/2015-03-31/functions/${Arn}/invocations",
                                    new Dictionary<string, dynamic> {
                                        ["Arn"] = Fn.GetAtt(method.Function.Name, "Arn")
                                    }
                                ),
                                RequestParameters = new Dictionary<string, dynamic> {
                                    ["integration.request.header.X-Amz-Invocation-Type"] = "'Event'"
                                },
                                RequestTemplates = new Dictionary<string, dynamic> {
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
                                IntegrationResponses = new List<ApiGateway.MethodTypes.IntegrationResponse> {
                                    new ApiGateway.MethodTypes.IntegrationResponse {
                                        StatusCode = 200,
                                        ResponseTemplates = new Dictionary<string, dynamic> {
                                            ["application/json"] =
@"{
    ""response_type"": ""in_channel"",
    ""text"": """"
}"
                                        }
                                    }
                                }
                            },
                            MethodResponses = new List<ApiGateway.MethodTypes.MethodResponse> {
                                new ApiGateway.MethodTypes.MethodResponse {
                                    StatusCode = 200,
                                    ResponseModels = new Dictionary<string, dynamic> {
                                        ["application/json"] = "Empty"
                                    }
                                }
                            }
                        };
                        break;
                    default:
                        throw new NotImplementedException($"api integration {method.Integration} is not supported");
                    }
                    apiMethods.Add(new KeyValuePair<string, ApiGateway.Method>(methodName, apiMethod));
                    _stack.Add(methodName, apiMethod);
                    _stack.Add($"{method.Function.Name}{methodName}Permission", new Lambda.Permission {
                        Action = "lambda:InvokeFunction",
                        FunctionName = Fn.GetAtt(method.Function.Name, "Arn"),
                        Principal = "apigateway.amazonaws.com",
                        SourceArn = Fn.Sub($"arn:aws:execute-api:${{AWS::Region}}:${{AWS::AccountId}}:${{ModuleRestApi}}/LATEST/{method.Method}/{string.Join("/", method.Path)}")
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
                    _stack.Add(newResourceName, new ApiGateway.Resource {
                        RestApiId = restApiId,
                        ParentId = parentId,
                        PathPart = subRoute.Key
                    });
                    AddApiResource(parentPrefix + partName, restApiId, Fn.Ref(newResourceName), level + 1, subRoute, apiMethods);
                }
            }
        }

        private void AddFunction(Function function, IDictionary<string, object> environmentRefVariables) {

            // initialize function environment variables
            var environmentVariables = function.Environment.ToDictionary(kv => "STR_" + kv.Key.ToUpperInvariant(), kv => (dynamic)kv.Value);
            environmentVariables["TIER"] = Fn.Ref("Tier");
            environmentVariables["MODULE_NAME"] = _module.Name;
            environmentVariables["MODULE_ID"] = Fn.Ref("AWS::StackName");
            environmentVariables["MODULE_VERSION"] = _module.Version.ToString();
            environmentVariables["LAMBDA_NAME"] = function.Name;
            environmentVariables["LAMBDA_RUNTIME"] = function.Runtime;
            environmentVariables["DEADLETTERQUEUE"] = _module.GetParameter("LambdaSharp::DeadLetterQueueArn").Reference;
            environmentVariables["DEFAULTSECRETKEY"] = _module.GetParameter("LambdaSharp::DefaultSecretKeyArn").Reference;
            foreach(var environmentRefVariable in environmentRefVariables) {
                environmentVariables[environmentRefVariable.Key] = environmentRefVariable.Value;
            }

            // create function registration
            if(_module.HasModuleRegistration && function.HasFunctionRegistration) {
                var registrationName = $"{function.Name}Registration";
                _stack.Add($"{function.Name}Registration", new LambdaSharpResource("LambdaSharp::Register::Function") {
                    ["ModuleId"] = Fn.Ref("AWS::StackName"),
                    ["FunctionId"] = Fn.Ref(function.Name),
                    ["FunctionName"] = function.Name,
                    ["FunctionLogGroupName"] = Fn.Sub($"/aws/lambda/${{{function.Name}}}"),
                    ["FunctionPlatform"] = "AWS Lambda",
                    ["FunctionFramework"] = function.Runtime,
                    ["FunctionLanguage"] = "csharp",
                    ["FunctionMaxMemory"] = function.Memory,
                    ["FunctionMaxDuration"] = function.Timeout
                }, dependsOn: new[] { "ModuleRegistration" });
            }

            // check if function as a VPC configuration
            Lambda.FunctionTypes.VpcConfig vpcConfig = null;
            if(function.VPC != null) {
                vpcConfig = new Lambda.FunctionTypes.VpcConfig {
                    SubnetIds = function.VPC.SubnetIds,
                    SecurityGroupIds = function.VPC.SecurityGroupIds
                };
            }

            // create function definition
            _stack.Add(function.Name, new Lambda.Function {
                Description = function.Description,
                Runtime = function.Runtime,
                Handler = function.Handler,
                Timeout = function.Timeout,
                MemorySize = function.Memory,
                ReservedConcurrentExecutions = function.ReservedConcurrency,
                Role = Fn.GetAtt("ModuleRole", "Arn"),
                Code = new Lambda.FunctionTypes.Code {
                    S3Bucket = Fn.Ref("DeploymentBucketName"),
                    S3Key = Fn.Sub($"${{DeploymentBucketPath}}{_module.Name}/Assets/{Path.GetFileName(function.PackagePath)}")
                },
                DeadLetterConfig = new Lambda.FunctionTypes.DeadLetterConfig {
                    TargetArn = _module.GetParameter("LambdaSharp::DeadLetterQueueArn").Reference
                },
                Environment = new Lambda.FunctionTypes.Environment {
                    Variables = environmentVariables
                },
                VpcConfig = vpcConfig
            });

            // create function log-group with retention window
            var functionLogGroup = $"{function.Name}LogGroup";
            _stack.Add(functionLogGroup, new LogGroup {
                LogGroupName = Fn.Sub($"/aws/lambda/${{{function.Name}}}"),

                // TODO (2018-09-26, bjorg): make retention configurable
                //  see https://docs.aws.amazon.com/AmazonCloudWatchLogs/latest/APIReference/API_PutRetentionPolicy.html
                RetentionInDays = 7
            });
            if(function.HasFunctionRegistration) {
                _stack.Add($"{function.Name}LogGroupSubscription", new SubscriptionFilter {
                    DestinationArn = _module.GetParameter("LambdaSharp::LoggingStreamArn").Reference,
                    FilterPattern = "-\"*** \"",
                    LogGroupName = Fn.Ref(functionLogGroup),
                    RoleArn = Fn.GetAtt("CloudWatchLogsRole", "Arn")
                });
            }

            // check if function has any SNS topic event sources
            var topicSources = function.Sources.OfType<TopicSource>();
            if(topicSources.Any()) {
                foreach(var topicSource in topicSources) {
                    var parameter = (AResourceParameter)_module.GetParameter(topicSource.TopicName);
                    EnumerateOrDefault(parameter.Resource.ResourceReferences, parameter.Reference, (suffix, arn) => {
                        _stack.Add($"{function.Name}{topicSource.TopicName}SnsPermission{suffix}", new Lambda.Permission {
                            Action = "lambda:InvokeFunction",
                            SourceArn = arn,
                            FunctionName = Fn.GetAtt(function.Name, "Arn"),
                            Principal = "sns.amazonaws.com"
                        });
                        _stack.Add($"{function.Name}{topicSource.TopicName}Subscription{suffix}", new SNS.Subscription {
                            Endpoint = Fn.GetAtt(function.Name, "Arn"),
                            Protocol = "lambda",
                            TopicArn = arn
                        });
                    });
                }
            }

            // check if function has any API gateway event sources
            var scheduleSources = function.Sources.OfType<ScheduleSource>().ToList();
            if(scheduleSources.Any()) {
                for(var i = 0; i < scheduleSources.Count; ++i) {
                    var name = function.Name + "ScheduleEvent" + (i + 1).ToString();
                    _stack.Add(name, new Events.Rule {
                        ScheduleExpression = scheduleSources[i].Expression,
                        Targets = new List<Events.RuleTypes.Target> {
                            new Events.RuleTypes.Target {
                                Id = Fn.Sub($"${{AWS::StackName}}Module{name}ScheduleEvent"),
                                Arn = Fn.GetAtt(function.Name, "Arn"),
                                InputTransformer = new Events.RuleTypes.InputTransformer {
                                    InputPathsMap = new Dictionary<string, dynamic> {
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
                        }
                    });
                }
                _stack.Add(function.Name + "ScheduleEventPermission", new Lambda.Permission {
                    Action = "lambda:InvokeFunction",
                    SourceAccount = Fn.Ref("AWS::AccountId"),
                    FunctionName = Fn.GetAtt(function.Name, "Arn"),
                    Principal = "events.amazonaws.com"
                });
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
                foreach(var grp in s3Sources.ToLookup(source => source.Bucket)) {
                    var functionS3Permission = $"{function.Name}{grp.Key}S3Permission";
                    var functionS3Subscription = $"{function.Name}{grp.Key}S3Subscription";
                    _stack.Add(functionS3Permission, new Lambda.Permission {
                        Action = "lambda:InvokeFunction",
                        SourceAccount = Fn.Ref("AWS::AccountId"),
                        SourceArn = Fn.GetAtt(grp.Key, "Arn"),
                        FunctionName = Fn.GetAtt(function.Name, "Arn"),
                        Principal = "s3.amazonaws.com"
                    });
                    _stack.Add(functionS3Subscription, new LambdaSharpResource("LambdaSharp::S3::Subscription") {
                        ["BucketName"] = Fn.Ref(grp.Key),
                        ["FunctionArn"] = Fn.GetAtt(function.Name, "Arn"),
                        ["Filters"] = grp.Select(source => {
                            var filter = new Dictionary<string, object>() {
                                ["Events"] = source.Events,
                            };
                            if(source.Prefix != null) {
                                filter["Prefix"] = source.Prefix;
                            }
                            if(source.Suffix != null) {
                                filter["Suffix"] = source.Suffix;
                            }
                            return filter;
                        }).ToList()
                    });
                    _stack.AddDependsOn(functionS3Subscription, functionS3Permission);
                }
            }

            // check if function has any SQS event sources
            var sqsSources = function.Sources.OfType<SqsSource>().ToList();
            if(sqsSources.Any()) {
                foreach(var source in sqsSources) {
                    var parameter = (AResourceParameter)_module.GetParameter(source.Queue);
                    EnumerateOrDefault(parameter.Resource.ResourceReferences, Fn.GetAtt(parameter.ResourceName, "Arn"), (suffix, arn) => {
                        _stack.Add($"{function.Name}{source.Queue}EventMapping{suffix}", new Lambda.EventSourceMapping {
                            BatchSize = source.BatchSize,
                            Enabled = true,
                            EventSourceArn = arn,
                            FunctionName = Fn.Ref(function.Name)
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
                    _stack.Add($"{function.Name}AlexaPermission{suffix}", new Lambda.Permission {
                        Action = "lambda:InvokeFunction",
                        FunctionName = Fn.GetAtt(function.Name, "Arn"),
                        Principal = "alexa-appkit.amazon.com",
                        EventSourceToken = source.EventSourceToken
                    });
                }
            }

            // check if function has any DynamoDB event sources
            var dynamoDbSources = function.Sources.OfType<DynamoDBSource>().ToList();
            if(dynamoDbSources.Any()) {
                foreach(var source in dynamoDbSources) {
                    var parameter = (AResourceParameter)_module.GetParameter(source.DynamoDB);
                    EnumerateOrDefault(parameter.Resource.ResourceReferences, Fn.GetAtt(parameter.ResourceName, "StreamArn"), (suffix, arn) => {
                        _stack.Add($"{function.Name}{source.DynamoDB}EventMapping{suffix}", new Lambda.EventSourceMapping {
                            BatchSize = source.BatchSize,
                            StartingPosition = source.StartingPosition,
                            Enabled = true,
                            EventSourceArn = arn,
                            FunctionName = Fn.Ref(function.Name)
                        });
                    });
                }
            }

            // check if function has any Kinesis event sources
            var kinesisSources = function.Sources.OfType<KinesisSource>().ToList();
            if(kinesisSources.Any()) {
                foreach(var source in kinesisSources) {
                    var parameter = (AResourceParameter)_module.GetParameter(source.Kinesis);
                    EnumerateOrDefault(parameter.Resource.ResourceReferences, Fn.GetAtt(parameter.ResourceName, "Arn"), (suffix, arn) => {
                        _stack.Add($"{function.Name}{source.Kinesis}EventMapping{suffix}", new Lambda.EventSourceMapping {
                            BatchSize = source.BatchSize,
                            StartingPosition = source.StartingPosition,
                            Enabled = true,
                            EventSourceArn = arn,
                            FunctionName = Fn.Ref(function.Name)
                        });
                    });
                }
            }

            // check if function has any CloudFormation Macro event sources
            var macroSources = function.Sources.OfType<MacroSource>().ToList();
            if(macroSources.Any()) {
                foreach(var source in macroSources) {
                    _stack.Add($"{function.Name}{source.MacroName}Macro", new CustomResource("AWS::CloudFormation::Macro") {
                        ["Name"] = Fn.Sub("${Tier}-" + source.MacroName),
                        ["FunctionName"] = Fn.Ref(function.Name)
                    });
                }
            }
        }

        private void AddParameter(
            AParameter parameter,
            string envPrefix,
            IDictionary<string, object> environmentRefVariables
        ) {
            var fullEnvName = envPrefix + parameter.Name.ToUpperInvariant();
            var resourceName = parameter.ResourceName;
            switch(parameter) {
            case SecretParameter secretParameter:
                if(parameter.Scope == ParameterScope.Function) {
                    if(secretParameter.EncryptionContext?.Any() == true) {
                        environmentRefVariables["SEC_" + fullEnvName] = $"{secretParameter.Reference}|{string.Join("|", secretParameter.EncryptionContext.Select(kv => $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"))}";
                    } else {
                        environmentRefVariables["SEC_" + fullEnvName] = secretParameter.Reference;
                    }
                }
                break;
            case ValueParameter _:
            case ValueListParameter _:
                if(parameter.Scope == ParameterScope.Function) {
                    environmentRefVariables["STR_" + fullEnvName] = parameter.Reference;
                }
                break;
            case PackageParameter packageParameter:
                if(parameter.Scope == ParameterScope.Function) {
                    environmentRefVariables["STR_" + fullEnvName] = parameter.Reference;
                }
                _stack.Add(resourceName, new LambdaSharpResource("LambdaSharp::S3::Package") {
                    ["DestinationBucketName"] = Fn.Ref(packageParameter.DestinationBucketParameterName),
                    ["DestinationKeyPrefix"] = packageParameter.DestinationKeyPrefix,
                    ["SourceBucketName"] = Fn.Ref("DeploymentBucketName"),
                    ["SourcePackageKey"] = Fn.Sub($"${{DeploymentBucketPath}}{_module.Name}/Assets/{Path.GetFileName(packageParameter.PackagePath)}")
                });
                break;
            case ReferencedResourceParameter referenceResourceParameter: {
                    if(parameter.Scope == ParameterScope.Function) {
                        environmentRefVariables["STR_" + fullEnvName] = parameter.Reference;
                    }

                    // add permissions for resource
                    var resource = referenceResourceParameter.Resource;
                    if(resource.Allow?.Any() == true) {
                        _resourceStatements.Add(new Statement {
                            Sid = resourceName,
                            Effect = "Allow",
                            Resource = (resource.ResourceReferences.Count == 1)
                                ? resource.ResourceReferences.First()
                                : resource.ResourceReferences,
                            Action = resource.Allow
                        });
                    }
                }
                break;
            case CloudFormationResourceParameter cloudFormationResourceParameter: {
                    var resource = cloudFormationResourceParameter.Resource;
                    object resourceAsStatementFn;
                    var resourceAsParameterFn = Fn.Ref(resourceName);
                    Humidifier.Resource resourceTemplate;
                    if(resource.Type.StartsWith("Custom::")) {
                        resourceAsStatementFn = null;
                        resourceAsParameterFn = null;
                        resourceTemplate = new CustomResource(resource.Type, resource.Properties);
                    } else if(!ResourceMapping.TryParseResourceProperties(
                        resource.Type,
                        resource.ResourceReferences.First(),
                        resource.Properties,
                        out resourceAsStatementFn,
                        out resourceTemplate
                    )) {
                        throw new NotImplementedException($"resource type is not supported: {resource.Type}");
                    }
                    _stack.Add(resourceName, resourceTemplate, dependsOn: resource.DependsOn.ToArray());
                    if(resource.Allow?.Any() == true) {

                        // add permissions for resource
                        if(resourceAsStatementFn != null) {
                            _resourceStatements.Add(new Statement {
                                Sid = resourceName,
                                Effect = "Allow",
                                Resource = resourceAsStatementFn,
                                Action = resource.Allow
                            });
                        }
                    }
                    if((parameter.Scope == ParameterScope.Function) && (resourceAsParameterFn != null)) {
                        environmentRefVariables["STR_" + fullEnvName] = parameter.Reference;
                    }
                }
                break;
            case ValueInputParameter valueInputParameter: {
                    _stack.Add(resourceName, new Parameter {
                        Type = (valueInputParameter.Type == "Secret") ? "String" : valueInputParameter.Type,
                        Description = valueInputParameter.Description,
                        Default = valueInputParameter.Default,
                        ConstraintDescription = valueInputParameter.ConstraintDescription,
                        AllowedPattern = valueInputParameter.AllowedPattern,
                        AllowedValues = valueInputParameter.AllowedValues?.ToList(),
                        MaxLength = valueInputParameter.MaxLength,
                        MaxValue = valueInputParameter.MaxValue,
                        MinLength = valueInputParameter.MinLength,
                        MinValue = valueInputParameter.MinValue,
                        NoEcho = valueInputParameter.NoEcho
                    });

                    // check if a conditional resource definition is attached to the input parameter
                    var resource = valueInputParameter.Resource;
                    if(resource != null) {
                        object resourceAsStatementFn;

                        // create resource when no input is provided
                        if(valueInputParameter.Default == "") {
                            Humidifier.Resource resourceTemplate;
                            if(resource.Type.StartsWith("Custom::")) {
                                resourceAsStatementFn = null;
                                resourceTemplate = new CustomResource(resource.Type, resource.Properties);
                            } else if(!ResourceMapping.TryParseResourceProperties(
                                resource.Type,
                                parameter.Reference,
                                resource.Properties,
                                out resourceAsStatementFn,
                                out resourceTemplate
                            )) {
                                throw new NotImplementedException($"resource type is not supported: {resource.Type}");
                            }
                            var condition = $"{resourceName}Created";
                            _stack.Add(condition, new Condition(Fn.Equals(Fn.Ref(resourceName), "")));
                            _stack.Add($"{resourceName}CreatedInstance", resourceTemplate, condition: condition, dependsOn: resource.DependsOn.ToArray());
                        } else {

                            // resource must always be provided as an input
                            resourceAsStatementFn = ResourceMapping.ExpandResourceReference(resource.Type, resource.ResourceReferences.First());
                        }

                        // add requested permission to input resource
                        if(resource.Allow?.Any() == true) {
                            _resourceStatements.Add(new Statement {
                                Sid = resourceName,
                                Effect = "Allow",
                                Resource = resourceAsStatementFn,
                                Action = resource.Allow
                            });
                        }
                    }
                    if(parameter.Scope == ParameterScope.Function) {
                        if(valueInputParameter.Type == "Secret") {
                            environmentRefVariables["SEC_" + fullEnvName] = parameter.Reference;
                        } else {
                            environmentRefVariables["STR_" + fullEnvName] = parameter.Reference;
                        }
                    }
                }
                break;
            case ImportInputParameter importInputParameter:
                _stack.Add(resourceName, new Parameter {
                    Type = (importInputParameter.Type == "Secret") ? "String" : importInputParameter.Type,
                    Description = importInputParameter.Description,
                    Default = "$" + importInputParameter.Import,
                    ConstraintDescription = "must either be a cross-module import reference or a non-blank value",
                    AllowedPattern =  @"^.+$",
                    NoEcho = importInputParameter.NoEcho
                });
                _stack.Add($"{resourceName}IsImport", new Condition(Fn.Equals(Fn.Select("0", Fn.Split("$", Fn.Ref(resourceName))), "")));

                // check if we need to add permissions for imported resource (expected to be an ARN)
                if(importInputParameter.Resource != null) {
                    var resource = importInputParameter.Resource;
                    if(resource.Allow?.Any() == true) {
                        _resourceStatements.Add(new Statement {
                            Sid = resourceName,
                            Effect = "Allow",
                            Resource = ResourceMapping.ExpandResourceReference(resource.Type, resource.ResourceReferences.First()),
                            Action = resource.Allow
                        });
                    }
                }
                if(parameter.Scope == ParameterScope.Function) {
                    if(importInputParameter.Type == "Secret") {
                        environmentRefVariables["SEC_" + fullEnvName] = parameter.Reference;
                    } else {
                        environmentRefVariables["STR_" + fullEnvName] = parameter.Reference;
                    }
                }
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(parameter), parameter, "unknown parameter type");
            }

            // check if nested parameters need to be added
            if(parameter.Parameters?.Any() == true) {
                foreach(var nestedParameter in parameter.Parameters) {
                    AddParameter(
                        nestedParameter,
                        fullEnvName + "_",
                        environmentRefVariables
                    );
                }
            }
        }
    }
}