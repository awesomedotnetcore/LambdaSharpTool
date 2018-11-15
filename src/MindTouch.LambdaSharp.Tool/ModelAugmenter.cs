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
using System.Linq;
using MindTouch.LambdaSharp.Tool.Internal;
using MindTouch.LambdaSharp.Tool.Model;
using MindTouch.LambdaSharp.Tool.Model.AST;
using Newtonsoft.Json;

namespace MindTouch.LambdaSharp.Tool {

    public class ModelAugmenter : AModelProcessor {

        //--- Class Methods ---
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
        private List<ApiRoute> _apiGatewayRoutes;

        //--- Constructors ---
        public ModelAugmenter(Settings settings, string sourceFilename) : base(settings, sourceFilename) { }

        //--- Methods ---
        public void Augment(Module module) {

            // append the version to the module description
            if(module.Description != null) {
                module.Description = module.Description.TrimEnd() + $" (v{module.Version})";
            }

            // add module variables
            var moduleValue = new ValueParameter {
                Name = "Module",
                Description = "LambdaSharp module information",
                Reference = ""
            };
            module.AddResource(moduleValue);
            moduleValue.AddResource(new ValueParameter {
                Name = "Id",
                Description = "LambdaSharp module id",
                Reference = FnRef("AWS::StackName")
            });
            moduleValue.AddResource(new ValueParameter {
                Name = "Name",
                Description = "Module name",
                Reference = module.Name
            });
            moduleValue.AddResource(new ValueParameter {
                Name = "Version",
                Description = "Module version",
                Reference = module.Version.ToString()
            });

            // add LambdaSharp Module Options
            var section = "LambdaSharp Module Options";
            module.AddResource(new InputParameter {
                Name = "Secrets",
                Section = section,
                Label = "Secret Keys (ARNs)",
                Description = "Comma-separated list of optional secret keys",
                Default = ""
            });

            // add standard parameters (unless requested otherwise)
            if(!module.HasPragma("no-lambdasharp-dependencies")) {

                // add LambdaSharp Module Internal Dependencies
                section = "LambdaSharp Dependencies";
                module.AddImportParameter(
                    import: "LambdaSharp::DeadLetterQueueArn",
                    section: section,
                    label: "Dead Letter Queue (ARN)",
                    description: "Dead letter queue for functions"
                );
                module.AddImportParameter(
                    import: "LambdaSharp::LoggingStreamArn",
                    section: section,
                    label: "Logging Stream (ARN)",
                    description: "Logging kinesis stream for functions"
                );
                module.AddImportParameter(
                    import: "LambdaSharp::DefaultSecretKeyArn",
                    section: section,
                    label: "Secret Key (ARN)",
                    description: "Default secret key for functions"
                );
                module.Secrets.Add(module.GetResource("LambdaSharp::DefaultSecretKeyArn").Reference);

                // check if lambdasharp imports should be added
                moduleValue.AddResource(new ValueParameter {
                    Name = "DeadLetterQueueArn",
                    Description = "LambdaSharp Dead Letter Queue",
                    Reference = FnRef("LambdaSharp::DeadLetterQueueArn")
                });
                moduleValue.AddResource(new ValueParameter {
                    Name = "LoggingStreamArn",
                    Description = "LambdaSharp Logging Stream",
                    Reference = FnRef("LambdaSharp::LoggingStreamArn")
                });
            }

            // add LambdaSharp Deployment Settings
            section = "LambdaSharp Deployment Settings (DO NOT MODIFY)";
            module.AddResource(new InputParameter {
                Name = "DeploymentBucketName",
                Section = section,
                Label = "Deployment S3 Bucket",
                Description = "Source deployment S3 bucket name"
            });
            module.AddResource(new InputParameter {
                Name = "DeploymentPrefix",
                Section = section,
                Label = "Deployment Prefix",
                Description = "Module deployment prefix"
            });
            module.AddResource(new InputParameter {
                Name = "DeploymentPrefixLowercase",
                Section = section,
                Label = "Deployment Prefix (lowercase)",
                Description = "Module deployment prefix (lowercase)"
            });
            module.AddResource(new InputParameter {
                Name = "DeploymentParent",
                Section = section,
                Label = "Parent Stack Name",
                Description = "Parent stack name for nested deployments, blank otherwise",
                Default = ""
            });

            // add module registration
            if(module.HasModuleRegistration) {
                moduleValue.AddResource(new CloudFormationResourceParameter {
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
                moduleValue.AddResource(new CloudFormationResourceParameter {
                    Name = "Role",
                    Resource = CreateResource("AWS::IAM::Role", new Dictionary<string, object> {
                        ["AssumeRolePolicyDocument"] = new Dictionary<string, object> {
                            ["Version"] = "2012-10-17",
                            ["Statement"] = new List<object> {
                                new Dictionary<string, object> {
                                    ["Sid"] = "ModuleLambdaInvocation",
                                    ["Effect"] = "Allow",
                                    ["Principal"] = new Dictionary<string, object> {
                                        ["Service"] = "lambda.amazonaws.com"
                                    },
                                    ["Action"] = "sts:AssumeRole"
                                }
                            }
                        },
                        ["Policies"] = new List<object> {
                            new Dictionary<string, object> {
                                ["PolicyName"] = FnSub("${AWS::StackName}ModulePolicy"),
                                ["PolicyDocument"] = new Dictionary<string, object> {
                                    ["Version"] = "2012-10-17",
                                    ["Statement"] = module.ResourceStatements
                                }
                            }
                        }
                    })
                });

                // create function registration
                if(module.HasModuleRegistration) {
                    foreach(var function in functions.Where(f => f.HasFunctionRegistration)) {
                        function.AddResource(new CloudFormationResourceParameter {
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
                    AddApiResource(module, restApiName, FnRef(restApiName), FnGetAtt(restApiName, "RootResourceId"), 0, _apiGatewayRoutes, apiMethods);

                    // RestApi deployment depends on all methods and their hash (to force redeployment in case of change)
                    var methodSignature = string.Join("\n", apiMethods
                        .OrderBy(kv => kv.Key)
                        .Select(kv => JsonConvert.SerializeObject(kv.Value))
                    );
                    string methodsHash = methodSignature.ToMD5Hash();

                    // create a RestApi
                    var restApiVar = new CloudFormationResourceParameter {
                        Name = "RestApi",
                        Description = "Module REST API",
                        Resource = CreateResource("AWS::ApiGateway::RestApi", new Dictionary<string, object> {
                            ["Name"] = FnSub("${AWS::StackName} Module API"),
                            ["Description"] = "${Module::Name} API (v${Module::Version})",
                            ["FailOnWarnings"] = true
                        })
                    };
                    moduleValue.AddResource(restApiVar);

                    // add RestApi url
                    restApiVar.AddResource(new ValueParameter {
                        Name = "Url",
                        Description = "Module REST API Url",
                        Reference = FnSub("https://${Module::RestApi}.execute-api.${AWS::Region}.${AWS::URLSuffix}/LATEST/")
                    });

                    // create a RestApi role that can write logs
                    restApiVar.AddResource(new CloudFormationResourceParameter {
                        Name = "Role",
                        Description = "Module REST API Role",
                        Resource = CreateResource("AWS::IAM::Role", new Dictionary<string, object> {
                            ["AssumeRolePolicyDocument"] = new Dictionary<string, object> {
                                ["Version"] = "2012-10-17",
                                ["Statement"] = new object[] {
                                    new Dictionary<string, object> {
                                        ["Sid"] = "ModuleRestApiInvocation",
                                        ["Effect"] = "Allow",
                                        ["Principal"] = new Dictionary<string, object> {
                                            ["Service"] = "apigateway.amazonaws.com"
                                        },
                                        ["Action"] = "sts:AssumeRole"
                                    }
                                }
                            },
                            ["Policies"] = new object[] {
                                new Dictionary<string, object> {
                                    ["PolicyName"] = FnSub("${AWS::StackName}ModuleRestApiPolicy"),
                                    ["PolicyDocument"] = new Dictionary<string, object> {
                                        ["Version"] = "2012-10-17",
                                        ["Statement"] = new object[] {
                                            new Dictionary<string, object> {
                                                ["Sid"] = "ModuleRestApiLogging",
                                                ["Effect"] = "Allow",
                                                ["Action"] = new List<object> {
                                                    "logs:CreateLogGroup",
                                                    "logs:CreateLogStream",
                                                    "logs:DescribeLogGroups",
                                                    "logs:DescribeLogStreams",
                                                    "logs:PutLogEvents",
                                                    "logs:GetLogEvents",
                                                    "logs:FilterLogEvents"
                                                },
                                                ["Resource"] = "*"
                                            }
                                        }
                                    }
                                }
                            }

                        })
                    });

                    // create a RestApi account which uses the RestApi role
                    restApiVar.AddResource(new CloudFormationResourceParameter {
                        Name = "Account",
                        Description = "Module REST API Account",
                        Resource = CreateResource("AWS::ApiGateway::Account", new Dictionary<string, object> {
                            ["CloudWatchRoleArn"] = FnGetAtt("Module::RestApi::Role", "Arn")
                        })
                    });

                    // NOTE (2018-06-21, bjorg): the RestApi deployment resource depends on ALL methods resources having been created;
                    //  a new name is used for the deployment to force the stage to be updated
                    restApiVar.AddResource(new CloudFormationResourceParameter {
                        Name = "Deployment" + methodsHash,
                        Description = "Module REST API Deployment",
                        Resource = new Resource {
                            Type = "AWS::ApiGateway::Deployment",
                            Properties = new Dictionary<string, object> {
                                ["RestApiId"] = FnRef("Module::RestApi"),
                                ["Description"] = FnSub($"${{AWS::StackName}} API [{methodsHash}]")
                            },
                            DependsOn = null // TODO: depends on all AWS::ApiGateway::Method
                        }
                    });

                    // RestApi stage depends on API gateway deployment and API gateway account
                    // NOTE (2018-06-21, bjorg): the stage resource depends on the account resource having been granted
                    //  the necessary permissions for logging
                    restApiVar.AddResource(new CloudFormationResourceParameter {
                        Name = "Stage",
                        Description = "Module REST API Stage",
                        Resource = new Resource {
                            Type = "AWS::ApiGateway::Stage",
                            Properties = new Dictionary<string, object> {
                                ["RestApiId"] = FnRef("Module::RestApi"),
                                ["DeploymentId"] = FnRef("Module::RestApi::Deployment" + methodsHash),
                                ["StageName"] = "LATEST",
                                ["MethodSettings"] = new List<object> {
                                    new Dictionary<string, object> {
                                        ["DataTraceEnabled"] = true,
                                        ["HttpMethod"] = "*",
                                        ["LoggingLevel"] = "INFO",
                                        ["ResourcePath"] = "/*"
                                    }
                                }
                            },
                            DependsOn = new List<string> { "Module::RestApi::Account" }
                        }
                    });
                }
            }
        }

        private void AddApiResource(IResourceCollection resources, string parentPrefix, object restApiId, object parentId, int level, IEnumerable<ApiRoute> routes, List<KeyValuePair<string, object>> apiMethods) {

            // attach methods to resource id
            var methods = routes.Where(route => route.Path.Length == level).ToArray();
            foreach(var method in methods) {
                var methodName = parentPrefix + method.Method;
                IDictionary<string, object> apiMethod;
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
                resources.AddResource(new CloudFormationResourceParameter {
                    Name = method.Method,
                    Resource = CreateResource("AWS::ApiGateway::Method", apiMethod)
                });
                resources.AddResource(new CloudFormationResourceParameter {
                    Name = $"{method.Function.Name}{methodName}Permission",
                    Resource = CreateResource("AWS::Lambda::Permission", new Dictionary<string, object> {
                        ["Action"] = "lambda:InvokeFunction",
                        ["FunctionName"] = FnGetAtt(method.Function.Name, "Arn"),
                        ["Principal"] = "apigateway.amazonaws.com",
                        ["SourceArn"] = FnSub($"arn:aws:execute-api:${{AWS::Region}}:${{AWS::AccountId}}:${{ModuleRestApi}}/LATEST/{method.Method}/{string.Join("/", method.Path)}")
                    })
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
                resources.AddResource(new CloudFormationResourceParameter {
                    Name = newResourceName,
                    Resource = CreateResource("AWS::ApiGateway::Resource", new Dictionary<string, object> {
                        ["RestApiId"] = restApiId,
                        ["ParentId"] = parentId,
                        ["PathPart"] = subRoute.Key
                    })
                });
                AddApiResource(resources, parentPrefix + partName, restApiId, FnRef(newResourceName), level + 1, subRoute, apiMethods);
            }

            IDictionary<string, object> CreateRequestResponseApiMethod(ApiRoute method) {
                return new Dictionary<string, object> {
                    ["AuthorizationType"] = "NONE",
                    ["HttpMethod"] = method.Method,
                    ["OperationName"] = method.OperationName,
                    ["ApiKeyRequired"] = method.ApiKeyRequired,
                    ["ResourceId"] = parentId,
                    ["RestApiId"] = restApiId,
                    ["Integration"] = new Dictionary<string, object> {
                        ["Type"] = "AWS_PROXY",
                        ["IntegrationHttpMethod"] = "POST",
                        ["Uri"] = FnSub(
                            "arn:aws:apigateway:${AWS::Region}:lambda:path/2015-03-31/functions/${Arn}/invocations",
                            new Dictionary<string, object> {
                                ["Arn"] = FnGetAtt(method.Function.Name, "Arn")
                            }
                        )
                    }
                };
            }

            IDictionary<string, object> CreateSlackRequestApiMethod(ApiRoute method) {

                // NOTE (2018-06-06, bjorg): Slack commands have a 3sec timeout on invocation, which is rarely good enough;
                // instead we wire Slack command requests up as asynchronous calls; this way, we can respond with
                // a callback later and the integration works well all the time.
                return new Dictionary<string, object> {
                    ["AuthorizationType"] = "NONE",
                    ["HttpMethod"] = method.Method,
                    ["OperationName"] = method.OperationName,
                    ["ApiKeyRequired"] = method.ApiKeyRequired,
                    ["ResourceId"] = parentId,
                    ["RestApiId"] = restApiId,
                    ["Integration"] = new Dictionary<string, object> {
                        ["Type"] = "AWS",
                        ["IntegrationHttpMethod"] = "POST",
                        ["Uri"] = FnSub(
                            "arn:aws:apigateway:${AWS::Region}:lambda:path/2015-03-31/functions/${Arn}/invocations",
                            new Dictionary<string, object> {
                                ["Arn"] = FnGetAtt(method.Function.Name, "Arn")
                            }
                        ),
                        ["RequestParameters"] = new Dictionary<string, object> {
                            ["integration.request.header.X-Amz-Invocation-Type"] = "'Event'"
                        },
                        ["RequestTemplates"] = new Dictionary<string, object> {
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
                        ["IntegrationResponses"] = new object[] {
                            new Dictionary<string, object> {
                                ["StatusCode"] = 200,
                                ["ResponseTemplates"] = new Dictionary<string, object> {
                                    ["application/json"] =
@"{
""response_type"": ""in_channel"",
""text"": """"
}"
                                }
                            }
                        }
                    },
                    ["MethodResponses"] = new object[] {
                        new Dictionary<string, object> {
                            ["StatusCode"] = 200,
                            ["ResponseModels"] = new Dictionary<string, object> {
                                ["application/json"] = "Empty"
                            }
                        }
                    }
                };
            }
        }

        private void AddFunction(Module module, Function function) {

            // check if function has any SNS topic event sources
            foreach(var topicSource in function.Sources.OfType<TopicSource>()) {
                var parameter = (AResourceParameter)module.GetResource(topicSource.TopicName);
                EnumerateOrDefault(parameter.Resource.ResourceReferences, parameter.Reference, (suffix, arn) => {
                    function.AddResource(new CloudFormationResourceParameter {
                        Name = $"{parameter.ResourceName}SnsPermission{suffix}",
                        Resource = CreateResource("AWS::Lambda::Permission", new Dictionary<string, object> {
                            ["Action"] = "lambda:InvokeFunction",
                            ["SourceArn"] = arn,
                            ["FunctionName"] = FnGetAtt(function.Name, "Arn"),
                            ["Principal"] = "sns.amazonaws.com"
                        })
                    });
                    function.AddResource(new CloudFormationResourceParameter {
                        Name = $"{parameter.ResourceName}Subscription{suffix}",
                        Resource = CreateResource("AWS::SNS::Subscription", new Dictionary<string, object> {
                            ["Endpoint"] = FnGetAtt(function.Name, "Arn"),
                            ["Protocol"] = "lambda",
                            ["TopicArn"] = arn
                        })
                    });
                });
            }

            // check if function has any API gateway event sources
            var scheduleSources = function.Sources.OfType<ScheduleSource>().ToList();
            if(scheduleSources.Any()) {
                for(var i = 0; i < scheduleSources.Count; ++i) {
                    var name = "ScheduleEvent" + (i + 1).ToString();
                    function.AddResource(new CloudFormationResourceParameter {
                        Name = name,
                        Resource = CreateResource("AWS::Events::Rule", new Dictionary<string, object> {
                            ["ScheduleExpression"] = scheduleSources[i].Expression,
                            ["Targets"] = new object[] {
                                new Dictionary<string, object> {
                                    ["Id"] = FnSub("${AWS::StackName}" + name),
                                    ["Arn"] = FnGetAtt(function.Name, "Arn"),
                                    ["InputTransformer"] = new Dictionary<string, object> {
                                        ["InputPathsMap"] = new Dictionary<string, object> {
                                            ["version"] = "$.version",
                                            ["id"] = "$.id",
                                            ["source"] = "$.source",
                                            ["account"] = "$.account",
                                            ["time"] = "$.time",
                                            ["region"] = "$.region"
                                        },
                                        ["InputTemplate"] =
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
                        })
                    });
                    function.AddResource(new CloudFormationResourceParameter {
                        Name = name + "Permission",
                        Resource = CreateResource("AWS::Lambda::Permission", new Dictionary<string, object> {
                            ["Action"] = "lambda:InvokeFunction",
                            ["SourceArn"] = FnGetAtt(name, "Arn"),
                            ["FunctionName"] = FnGetAtt(function.Name, "Arn"),
                            ["Principal"] = "events.amazonaws.com"
                        })
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
                        Parameter = (AResourceParameter)module.GetResource(source.Bucket),
                        Source = source
                    }).ToLookup(tuple => tuple.Parameter.ResourceName)
                ) {
                    var functionS3Permission = $"{grp.Key}S3Permission";
                    var functionS3Subscription = $"{grp.Key}S3Subscription";
                    EnumerateOrDefault(grp.First().Parameter.Resource.ResourceReferences, FnGetAtt(grp.Key, "Arn"), (suffix, arn) => {
                        function.AddResource(new CloudFormationResourceParameter {
                            Name = functionS3Permission,
                            Resource = CreateResource("AWS::Lambda::Permission", new Dictionary<string, object> {
                                ["Action"] = "lambda:InvokeFunction",
                                ["SourceAccount"] = FnRef("AWS::AccountId"),
                                ["SourceArn"] = arn,
                                ["FunctionName"] = FnGetAtt(function.Name, "Arn"),
                                ["Principal"] = "s3.amazonaws.com"
                            })
                        });
                        function.AddResource(new CloudFormationResourceParameter {
                            Name = functionS3Subscription,
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
                    var parameter = (AResourceParameter)module.GetResource(source.Queue);
                    EnumerateOrDefault(parameter.Resource.ResourceReferences, FnGetAtt(parameter.ResourceName, "Arn"), (suffix, arn) => {
                        function.AddResource(new CloudFormationResourceParameter {
                            Name = $"{parameter.ResourceName}EventMapping{suffix}",
                            Resource = CreateResource("AWS::Lambda::EventSourceMapping", new Dictionary<string, object> {
                                ["BatchSize"] = source.BatchSize,
                                ["Enabled"] = true,
                                ["EventSourceArn"] = arn,
                                ["FunctionName"] = FnRef(function.Name)
                            })
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
                    function.AddResource(new CloudFormationResourceParameter {
                        Name = $"AlexaPermission{suffix}",
                        Resource = CreateResource("AWS::Lambda::Permission", new Dictionary<string, object> {
                            ["Action"] = "lambda:InvokeFunction",
                            ["FunctionName"] = FnGetAtt(function.Name, "Arn"),
                            ["Principal"] = "alexa-appkit.amazon.com",
                            ["EventSourceToken"] = eventSourceToken
                        })
                    });
                }
            }

            // check if function has any DynamoDB event sources
            var dynamoDbSources = function.Sources.OfType<DynamoDBSource>().ToList();
            if(dynamoDbSources.Any()) {
                foreach(var source in dynamoDbSources) {
                    var parameter = (AResourceParameter)module.GetResource(source.DynamoDB);
                    EnumerateOrDefault(parameter.Resource.ResourceReferences, FnGetAtt(parameter.ResourceName, "StreamArn"), (suffix, arn) => {
                        function.AddResource(new CloudFormationResourceParameter {
                            Name = $"{parameter.ResourceName}EventMapping{suffix}",
                            Resource = CreateResource("AWS::Lambda::EventSourceMapping", new Dictionary<string, object> {
                                ["BatchSize"] = source.BatchSize,
                                ["StartingPosition"] = source.StartingPosition,
                                ["Enabled"] = true,
                                ["EventSourceArn"] = arn,
                                ["FunctionName"] = FnRef(function.Name)
                            })
                        });
                    });
                }
            }

            // check if function has any Kinesis event sources
            var kinesisSources = function.Sources.OfType<KinesisSource>().ToList();
            if(kinesisSources.Any()) {
                foreach(var source in kinesisSources) {
                    var parameter = (AResourceParameter)module.GetResource(source.Kinesis);
                    EnumerateOrDefault(parameter.Resource.ResourceReferences, FnGetAtt(parameter.ResourceName, "Arn"), (suffix, arn) => {
                        function.AddResource(new CloudFormationResourceParameter {
                            Name = $"{parameter.ResourceName}EventMapping{suffix}",
                            Resource = CreateResource("AWS::Lambda::EventSourceMapping", new Dictionary<string, object> {
                                ["BatchSize"] = source.BatchSize,
                                ["StartingPosition"] = source.StartingPosition,
                                ["Enabled"] = true,
                                ["EventSourceArn"] = arn,
                                ["FunctionName"] = FnRef(function.Name)
                            })
                        });
                    });
                }
            }
        }
    }
}