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

namespace MindTouch.LambdaSharp.Tool.Build {
    using static ModelFunctions;

    public class ModelFunctionProcessor : AModelProcessor {

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
        private List<ApiRoute> _apiGatewayRoutes = new List<ApiRoute>();

        //--- Constructors ---
        public ModelFunctionProcessor(Settings settings, string sourceFilename) : base(settings, sourceFilename) { }

        //--- Methods ---
        public void Process(ModuleBuilder builder) {
            _builder = builder;

            // create module IAM role used by all functions
            var functions = _builder.Entries.OfType<FunctionEntry>().ToList();
            if(functions.Any()) {

                // add functions
                foreach(var function in functions) {
                    AddFunction(function);
                }

                // check if an API gateway needs to be created
                if(_apiGatewayRoutes.Any()) {
                    var moduleEntry = _builder.GetEntry("Module");

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
                        resourceArnAttribute: null,
                        dependsOn: null,
                        condition: null,
                        pragmas: null
                    );

                    // recursively create resources as needed
                    var apiMethods = new List<KeyValuePair<string, object>>();
                    AddApiResource(restApiEntry, FnRef(restApiEntry.ResourceName), FnGetAtt(restApiEntry.ResourceName, "RootResourceId"), 0, _apiGatewayRoutes, apiMethods);

                    // RestApi deployment depends on all methods and their hash (to force redeployment in case of change)
                    var methodSignature = string.Join("\n", apiMethods
                        .OrderBy(kv => kv.Key)
                        .Select(kv => JsonConvert.SerializeObject(kv.Value))
                    );
                    string methodsHash = methodSignature.ToMD5Hash();

                    // add RestApi url
                    _builder.AddVariable(
                        parent: restApiEntry,
                        name: "Url",
                        description: "Module REST API URL",
                        type: "String",
                        scope: null,
                        value: FnSub("https://${Module::RestApi}.execute-api.${AWS::Region}.${AWS::URLSuffix}/LATEST/"),
                        encryptionContext: null
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
                        resourceArnAttribute: null,
                        dependsOn: null,
                        condition: null,
                        pragmas: null
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
                        resourceArnAttribute: null,
                        dependsOn: null,
                        condition: null,
                        pragmas: null
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
                        resourceArnAttribute: null,
                        dependsOn: apiMethods.Select(kv => kv.Key).ToArray(),
                        condition: null,
                        pragmas: null
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
                        resourceArnAttribute: null,
                        dependsOn: new[] { "Module::RestApi::Account" },
                        condition: null,
                        pragmas: null
                    );
                }
            }
        }

        private void AddApiResource(AModuleEntry parent, object restApiId, object parentId, int level, IEnumerable<ApiRoute> routes, List<KeyValuePair<string, object>> apiMethods) {

            // create methods at this route level to parent id
            var methods = routes.Where(route => route.Path.Length == level).ToArray();
            foreach(var method in methods) {
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

                // add API method entry
                var methodEntry = _builder.AddResource(
                    parent: parent,
                    name: method.Method,
                    description: null,
                    scope: null,
                    resource: apiMethod,
                    resourceArnAttribute: null,
                    dependsOn: null,
                    condition: null,
                    pragmas: null
                );

                // add permission to API method to invoke lambda
                _builder.AddResource(
                    parent: methodEntry,
                    name: "Permission",
                    description: null,
                    scope: null,
                    resource: new Humidifier.Lambda.Permission {
                        Action = "lambda:InvokeFunction",
                        FunctionName = FnGetAtt(method.Function.ResourceName, "Arn"),
                        Principal = "apigateway.amazonaws.com",
                        SourceArn = FnSub($"arn:aws:execute-api:${{AWS::Region}}:${{AWS::AccountId}}:${{Module::RestApi}}/LATEST/{method.Method}/{string.Join("/", method.Path)}")
                    },
                    resourceArnAttribute: null,
                    dependsOn: null,
                    condition: null,
                    pragmas: null
                );
                apiMethods.Add(new KeyValuePair<string, object>(methodEntry.ResourceName, apiMethod));
            }

            // find sub-routes and group common sub-route prefix
            var subRoutes = routes.Where(route => route.Path.Length > level).ToLookup(route => route.Path[level]);
            foreach(var subRoute in subRoutes) {

                // remove special character from path segment and capitalize it
                var partName = subRoute.Key.ToIdentifier();
                partName = char.ToUpperInvariant(partName[0]) + ((partName.Length > 1) ? partName.Substring(1) : "");

                // create a new parent resource to attach methods or sub-resource to
                var resource = _builder.AddResource(
                    parent: parent,
                    name: partName + "Resource",
                    description: null,
                    scope: null,
                    resource: new Humidifier.ApiGateway.Resource {
                        RestApiId = restApiId,
                        ParentId = parentId,
                        PathPart = subRoute.Key
                    },
                    resourceArnAttribute: null,
                    dependsOn: null,
                    condition: null,
                    pragmas: null
                );
                AddApiResource(resource, restApiId, FnRef(resource.ResourceName), level + 1, subRoute, apiMethods);
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
                        Uri = FnSub($"arn:aws:apigateway:${{AWS::Region}}:lambda:path/2015-03-31/functions/${{{method.Function.ResourceName}.Arn}}/invocations")
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

        private void AddFunction(FunctionEntry function) {

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
                                Endpoint = FnGetAtt(function.ResourceName, "Arn"),
                                Protocol = "lambda",
                                TopicArn = arn
                            },
                            resourceArnAttribute: null,
                            dependsOn: null,
                            condition: null,
                            pragmas: null
                        );
                        _builder.AddResource(
                            parent: function,
                            name: $"Source{sourceSuffix}Permission{suffix}",
                            description: null,
                            scope: null,
                            resource: new Humidifier.Lambda.Permission {
                                Action = "lambda:InvokeFunction",
                                SourceArn = arn,
                                FunctionName = FnGetAtt(function.ResourceName, "Arn"),
                                Principal = "sns.amazonaws.com"
                            },
                            resourceArnAttribute: null,
                            dependsOn: null,
                            condition: null,
                            pragmas: null
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
                                        Arn = FnGetAtt(function.ResourceName, "Arn"),
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
                            resourceArnAttribute: null,
                            dependsOn: null,
                            condition: null,
                            pragmas: null
                        );
                        _builder.AddResource(
                            parent: function,
                            name: $"Source{sourceSuffix}Permission",
                            description: null,
                            scope: null,
                            resource: new Humidifier.Lambda.Permission {
                                Action = "lambda:InvokeFunction",
                                SourceArn = FnGetAtt(schedule.ResourceName, "Arn"),
                                FunctionName = FnGetAtt(function.ResourceName, "Arn"),
                                Principal = "events.amazonaws.com"
                            },
                            resourceArnAttribute: null,
                            dependsOn: null,
                            condition: null,
                            pragmas: null
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
                                FunctionName = FnGetAtt(function.ResourceName, "Arn"),
                                Principal = "s3.amazonaws.com"
                            },
                            resourceArnAttribute: null,
                            dependsOn: null,
                            condition: null,
                            pragmas: null
                        );
                        _builder.AddResource(
                            parent: function,
                            name: $"Source{sourceSuffix}Subscription",
                            description: null,
                            scope: null,
                            resource: new Humidifier.CustomResource("LambdaSharp::S3::Subscription") {
                                ["BucketArn"] = arn,
                                ["FunctionArn"] = FnGetAtt(function.ResourceName, "Arn"),
                                ["Filters"] = new List<object> {

                                    // TODO (2018-11-18, bjorg): we need to group filters from the same function for the same bucket
                                    ConvertS3Source()
                                }
                            },
                            resourceArnAttribute: null,
                            dependsOn: new[] { permission.FullName },
                            condition: null,
                            pragmas: null
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
                                FunctionName = FnRef(function.ResourceName)
                            },
                            resourceArnAttribute: null,
                            dependsOn: null,
                            condition: null,
                            pragmas: null
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

                            // create conditional expression to allow "*" values
                            var condition = $"{function.LogicalId}Source{sourceSuffix}AlexaIsBlank";
                            eventSourceToken = FnIf(
                                condition,
                                FnRef("AWS::NoValue"),
                                alexaSource.EventSourceToken
                            );
                            _builder.AddCondition(condition, FnEquals(alexaSource.EventSourceToken, "*"));
                        }
                        _builder.AddResource(
                            parent: function,
                            name: $"Source{sourceSuffix}AlexaPermission",
                            description: null,
                            scope: null,
                            resource: new Humidifier.Lambda.Permission {
                                Action = "lambda:InvokeFunction",
                                FunctionName = FnGetAtt(function.ResourceName, "Arn"),
                                Principal = "alexa-appkit.amazon.com",
                                EventSourceToken = eventSourceToken
                            },
                            resourceArnAttribute: null,
                            dependsOn: null,
                            condition: null,
                            pragmas: null
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
                                FunctionName = FnRef(function.ResourceName)
                            },
                            resourceArnAttribute: null,
                            dependsOn: null,
                            condition: null,
                            pragmas: null
                        );
                    }, entry => FnGetAtt(entry.ResourceName, "StreamArn"));
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
                                FunctionName = FnRef(function.ResourceName)
                            },
                            resourceArnAttribute: null,
                            dependsOn: null,
                            condition: null,
                            pragmas: null
                        );
                    });
                    break;
                default:
                    throw new ApplicationException($"unrecognized function source type '{source?.GetType()}' for source #{sourceSuffix}");
                }
            }
        }

        private void Enumerate(string fullName, Action<string, AModuleEntry, object> action, Func<AResourceEntry, object> getReference = null) {
            if(!_builder.TryGetEntry(fullName, out AModuleEntry entry)) {
                AddError($"could not find function source: '{fullName}'");
                return;
            }
            if(entry is AResourceEntry resource) {
                action("", entry, getReference?.Invoke(resource) ?? entry.GetExportReference());
            } else if(entry.Reference is IList list) {
                switch(list.Count) {
                case 0:
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
                action("", entry, entry.GetExportReference());
            }
        }
   }
}