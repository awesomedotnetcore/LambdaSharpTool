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
using System.Text;
using System.Text.RegularExpressions;
using MindTouch.LambdaSharp.Tool.Model;
using MindTouch.LambdaSharp.Tool.Model.AST;

namespace MindTouch.LambdaSharp.Tool {
    using Fn = Humidifier.Fn;
    using Condition = Humidifier.Condition;

    public class ModelParserException : Exception {

        //--- Constructors ---
        public ModelParserException(string message) : base(message) { }
    }

    public class ModelConverter : AModelProcessor {

        //--- Constants ---
        private const string CUSTOM_RESOURCE_PREFIX = "Custom::";

        //--- Fields ---
        private Module _module;

        //--- Constructors ---
        public ModelConverter(Settings settings, string sourceFilename) : base(settings, sourceFilename) { }

        //--- Methods ---
        public Module Process(ModuleNode module) {

            // convert module file
            try {
                return Convert(module);
            } catch(Exception e) {
                AddError(e);
                return null;
            }
        }

        private Module Convert(ModuleNode module) {

            // initialize module
            _module = new Module {
                Name = module.Name,
                Version = VersionInfo.Parse(module.Version),
                Description = module.Description,
                Pragmas = module.Pragmas,
                Functions = new List<Function>()
            };

            // append the version to the module description
            if(_module.Description != null) {
                _module.Description = _module.Description.TrimEnd() + $" (v{module.Version})";
            }

            // convert parameters
            var parameters = new List<AParameter>();
            parameters.AddRange(AtLocation("Inputs", () => ConvertInputs(module.Inputs), null) ?? new List<AParameter>());

            // add LambdaSharp Module Options
            var section = "LambdaSharp Module Options";
            parameters.AddRange(AtLocation("Inputs", () => ConvertInputs(new InputNode[] {
                new InputNode {
                    Input = "ModuleSecrets",
                    Scope = "Module",
                    Section = section,
                    Label = "Secret Keys (ARNs)",
                    Description = "Comma-separated list of optional secret keys",
                    Default = ""
                }
            }), null) ?? new List<AParameter>());

            // add LambdaSharp Module Internal Dependencies
            section = "LambdaSharp Dependencies";
            parameters.AddRange(AtLocation("Inputs", () => ConvertInputs(new InputNode[] {
                new InputNode {
                    Import = "LambdaSharp::DeadLetterQueueArn",
                    Scope = "Module",
                    Section = section,
                    Label = "Dead Letter Queue (ARN)",
                    Description = "Dead letter queue for functions"
                },
                new InputNode {
                    Import = "LambdaSharp::LoggingStreamArn",
                    Scope = "Module",
                    Section = section,
                    Label = "Logging Stream (ARN)",
                    Description = "Logging kinesis stream for functions"
                },
                new InputNode {
                    Import = "LambdaSharp::DefaultSecretKeyArn",
                    Scope = "Module",
                    Section = section,
                    Label = "Secret Key (ARN)",
                    Description = "Default secret key for functions"
                }
            }), null) ?? new List<AParameter>());

            // add LambdaSharp Deployment Settings
            section = "LambdaSharp Deployment Settings (DO NOT MODIFY)";
            parameters.AddRange(AtLocation("Inputs", () => ConvertInputs(new InputNode[] {
                new InputNode {
                    Input = "DeploymentBucketName",
                    Scope = "Module",
                    Section = section,
                    Label = "S3 Bucket Name",
                    Description = "Source deployment S3 bucket name"
                },
                new InputNode {
                    Input = "DeploymentBucketPath",
                    Scope = "Module",
                    Section = section,
                    Label = "S3 Bucket Path",
                    Description = "Source deployment S3 bucket path"
                },
                new InputNode {
                    Input = "Tier",
                    Scope = "Module",
                    Section = section,
                    Label = "Tier",
                    Description = "Module deployment tier"
                },
                new InputNode {
                    Input = "TierLowercase",
                    Scope = "Module",
                    Section = section,
                    Label = "Tier (lowercase)",
                    Description = "Module deployment tier (lowercase)"
                }
            }), null) ?? new List<AParameter>());
            parameters.AddRange(AtLocation("Variables", () => ConvertParameters(module.Variables, ParameterScope.Module), null) ?? new List<AParameter>());
            parameters.AddRange(AtLocation("Parameters", () => ConvertParameters(module.Parameters, ParameterScope.Function), null) ?? new List<AParameter>());
            _module.Parameters = parameters;

            // convert secrets (NOTE: must happen AFTER parameters are initialized)
            var secretIndex = 0;
            _module.Secrets = AtLocation("Secrets", () => module.Secrets
                .Select(secret => ConvertSecret(++secretIndex, secret))
                .Where(secret => secret != null)
                .ToList()
            , new List<object>());

            // add default secrets key that is imported from the input parameters
            _module.Secrets.Add(_module.GetParameter("LambdaSharp::DefaultSecretKeyArn").Reference);

            // create functions
            var functionIndex = 0;
            _module.Functions = AtLocation("Functions", () => module.Functions
                .Select(function => ConvertFunction(++functionIndex, function))
                .Where(function => function != null)
                .ToList()
            , null) ?? new List<Function>();

            // convert outputs
            var outputIndex = 0;
            _module.Outputs = AtLocation("Outputs", () => module.Outputs
                .Select(output => ConvertOutput(++outputIndex, output))
                .Where(output => output != null)
                .ToList()
            , null) ?? new List<AOutput>();

            // add module variables
            var moduleParameters = new List<AParameter> {
                new ValueParameter {
                    Scope = ParameterScope.Module,
                    Name = "Id",
                    ResourceName = "ModuleId",
                    Description = "LambdaSharp module id",
                    Reference = FnRef("AWS::StackName")
                },
                new ValueParameter {
                    Scope = ParameterScope.Module,
                    Name = "Name",
                    ResourceName = "ModuleName",
                    Description = "LambdaSharp module name",
                    Reference = _module.Name
                },
                new ValueParameter {
                    Scope = ParameterScope.Module,
                    Name = "Version",
                    ResourceName = "ModuleVersion",
                    Description = "LambdaSharp module version",
                    Reference = _module.Version.ToString()
                },
                new ValueParameter {
                    Scope = ParameterScope.Module,
                    Name = "DeadLetterQueueArn",
                    ResourceName = "ModuleDeadLetterQueueArn",
                    Description = "LambdaSharp Dead Letter Queue",
                    Reference = FnRef("LambdaSharp::DeadLetterQueueArn")
                },
                new ValueParameter {
                    Scope = ParameterScope.Module,
                    Name = "LoggingStreamArn",
                    ResourceName = "ModuleLoggingStreamArn",
                    Description = "LambdaSharp Logging Stream",
                    Reference = FnRef("LambdaSharp::LoggingStreamArn")
                }

                // TODO (2010-10-05, bjorg): add `Module::RestApi` as well?
                //  FnSub("https://${ModuleRestApi}.execute-api.${AWS::Region}.${AWS::URLSuffix}/LATEST/")
            };
            _module.Parameters.Add(new ValueParameter {
                Scope = ParameterScope.Module,
                Name = "Module",
                ResourceName = "Module",
                Description = "LambdaSharp module information",
                Reference = "",
                Parameters = moduleParameters
            });
            return _module;
        }

        private object ConvertSecret(int index, string secret) {
            return AtLocation($"[{index}]", () => {
                if(secret.StartsWith("arn:")) {

                    // decryption keys provided with their ARN can be added as is; no further steps required
                    return secret;
                }

                // assume key name is an alias and resolve it to its ARN
                try {
                    var response = Settings.KmsClient.DescribeKeyAsync($"alias/{secret}").Result;
                    return response.KeyMetadata.Arn;
                } catch(Exception e) {
                    AddError($"failed to resolve key alias: {secret}", e);
                    return null;
                }
            }, null);
        }

        private IList<AParameter> ConvertInputs(IList<InputNode> inputs) {
            var resultList = new List<AParameter>();
            if((inputs == null) || !inputs.Any()) {
                return resultList;
            }
            var index = 0;
            foreach(var input in inputs) {
                ++index;
                AtLocation(input.Input ?? input.Import, () => {
                    AInputParameter result = null;
                    if(input.Import != null) {
                        var parts = input.Import.Split("::", 2);

                        // find or create parent collection node
                        var parentParameter = resultList.FirstOrDefault(p => p.Name == parts[0]);
                        if(parentParameter == null) {
                            parentParameter = new ValueParameter {
                                Scope = ParameterScope.Module,
                                Name = parts[0],
                                ResourceName = parts[0],
                                Description = $"{parts[0]} cross-module references",
                                Reference = "",
                                Parameters = new List<AParameter>()
                            };
                            resultList.Add(parentParameter);
                        }

                        // create imported input
                        var resourceName = input.Import.Replace("::", "");
                        result = new ImportInputParameter {
                            Name = parts[1],
                            Type = input.Type,
                            ResourceName = resourceName,
                            Reference = FnIf(
                                $"{resourceName}IsImport",
                                FnImportValue(FnSub("${Tier}-${Import}", new Dictionary<string, object> {
                                    ["Import"] = FnSelect("1", FnSplit("$", FnRef(resourceName)))
                                })),
                                FnRef(resourceName)
                            ),
                            Import = input.Import
                        };

                        // check if a resource definition is associated with the import statement
                        if(input.Resource != null) {
                            result.Resource = ConvertResource(new List<object> { result.Reference }, input.Resource);
                        }
                        parentParameter.Parameters.Add(result);
                    } else {

                        // create regular input
                        result = new ValueInputParameter {
                            Name = input.Input,
                            ResourceName = input.Input,
                            Reference = FnRef(input.Input),
                            Default = input.Default,
                            ConstraintDescription = input.ConstraintDescription,
                            AllowedPattern = input.AllowedPattern,
                            AllowedValues = input.AllowedValues,
                            MaxLength = input.MaxLength,
                            MaxValue = input.MaxValue,
                            MinLength = input.MinLength,
                            MinValue = input.MinValue
                        };

                        // check if a resource definition is associated with the input statement
                        if(input.Resource != null) {
                            if(input.Default == "") {
                                result.Reference = FnIf(
                                    $"{result.Name}Created",
                                    ResourceMapping.GetArnReference(input.Resource.Type, $"{result.Name}CreatedInstance"),
                                    FnRef(result.Name)
                                );
                            }
                            result.Resource = ConvertResource(new List<object> { result.Reference }, input.Resource);
                        }
                    }
                    if(result != null) {

                        // set AParameter fields
                        result.Scope = Enum.Parse<ParameterScope>(input.Scope ?? "Function");
                        result.Description = input.Description;

                        // set AInputParamete fields
                        result.Type = input.Type ?? "String";
                        result.Section = input.Section ?? "Module Settings";
                        result.Label = input.Label ?? PrettifyLabel(input.Import ?? input.Input);
                        result.NoEcho = input.NoEcho;

                        // add result, unless it's an cross-module reference
                        if(input.Import == null) {
                            resultList.Add(result);
                        }

                        // local function
                        string PrettifyLabel(string name) {
                            var builder = new StringBuilder();
                            var isUppercase = true;
                            foreach(var c in name) {
                                if(char.IsDigit(c)) {
                                    if(!isUppercase) {
                                        builder.Append(' ');
                                    }
                                    isUppercase = true;
                                    builder.Append(c);
                                } else if(char.IsLetter(c)) {
                                    if(isUppercase) {
                                        isUppercase = char.IsUpper(c);
                                        builder.Append(c);
                                    } else {
                                        if(isUppercase = char.IsUpper(c)) {
                                            builder.Append(' ');
                                        }
                                        builder.Append(c);
                                    }
                                }
                            }
                            return builder.ToString();
                        }
                    }
                });
            }
            return resultList;
        }

        private IList<AParameter> ConvertParameters(
            IList<ParameterNode> parameters,
            ParameterScope scope,
            string resourcePrefix = ""
        ) {
            var resultList = new List<AParameter>();
            if((parameters == null) || !parameters.Any()) {
                return resultList;
            }

            // convert all parameters
            var index = 0;
            foreach(var parameter in parameters) {
                ++index;
                var parameterName = parameter.Name ?? $"[{index}]";
                AParameter result = null;
                var resourceName = resourcePrefix + parameter.Name;
                AtLocation(parameterName, () => {
                    if(parameter.Secret != null) {

                        // encrypted value
                        AtLocation("Secret", () => {
                            result = new SecretParameter {
                                Scope = scope,
                                Name = parameter.Name,
                                Description = parameter.Description,
                                Secret = parameter.Secret,
                                EncryptionContext = parameter.EncryptionContext,
                                Reference = parameter.Secret
                            };
                        });
                    } else if(parameter.Values != null) {
                        if(parameter.Resource != null) {
                            AtLocation("Resource", () => {

                                // list of existing resources
                                var resource = ConvertResource(parameter.Values, parameter.Resource);
                                result = new ReferencedResourceParameter {
                                    Scope = scope,
                                    Name = parameter.Name,
                                    Description = parameter.Description,
                                    Resource = resource,
                                    Reference = FnJoin(",", resource.ResourceReferences)
                                };
                            });
                        } else {

                            // list of values
                            AtLocation("Values", () => {
                                result = new ValueListParameter {
                                    Scope = scope,
                                    Name = parameter.Name,
                                    Description = parameter.Description,
                                    Values = parameter.Values,
                                    Reference = FnJoin(",", parameter.Values)
                                };
                            });
                        }
                    } else if(parameter.Package != null) {

                        // package value
                        result = new PackageParameter {
                            Scope = scope,
                            Name = parameter.Name,
                            Description = parameter.Description,
                            DestinationBucketParameterName = parameter.Package.Bucket,
                            DestinationKeyPrefix = parameter.Package.Prefix ?? "",
                            PackagePath = parameter.Package.PackagePath,
                            Reference = FnGetAtt(resourceName, "Result")
                        };
                    } else if(parameter.Value != null) {
                        if(parameter.Resource != null) {
                            AtLocation("Resource", () => {

                                // existing resource
                                var resource = ConvertResource(new List<object> { parameter.Value }, parameter.Resource);
                                result = new ReferencedResourceParameter {
                                    Scope = scope,
                                    Name = parameter.Name,
                                    Description = parameter.Description,
                                    Resource = resource,
                                    Reference = FnJoin(",", resource.ResourceReferences)
                                };
                            });
                        } else {
                            result = new ValueParameter {
                                Scope = scope,
                                Name = parameter.Name,
                                Description = parameter.Description,
                                Reference = parameter.Value
                            };

                        }
                    } else if(parameter.Resource != null) {

                        // managed resource
                        AtLocation("Resource", () => {
                            result = new CloudFormationResourceParameter {
                                Scope = scope,
                                Name = parameter.Name,
                                Description = parameter.Description,
                                Resource = ConvertResource(new List<object>(), parameter.Resource),
                                Reference = ResourceMapping.GetReference(parameter.Resource.Type, resourceName)
                            };
                        });
                    }
                });

                // check if there are nested parameters
                if(parameter.Parameters != null) {
                    AtLocation("Parameters", () => {
                        var nestedParameters = ConvertParameters(
                            parameter.Parameters,
                            scope,
                            resourceName
                        );

                        // keep nested parameters only if they have values
                        if(nestedParameters.Any()) {

                            // create empty string parameter if collection has no value
                            result = result ?? new ValueParameter {
                                Scope = scope,
                                Name = parameter.Name,
                                ResourceName = resourcePrefix + parameter.Name,
                                Description = parameter.Description,
                                Reference = ""
                            };
                            result.Parameters = nestedParameters;
                        }
                    });
                }

                // add parameter
                if(result != null) {
                    result.ResourceName = resourceName;
                    resultList.Add(result);
                }
            }
            return resultList;
        }

        private Resource ConvertResource(IList<object> resourceReferences, ResourceNode resource) {

            // parse resource allowed operations
            var allowList = new List<string>();
            if(resource.Allow != null) {
                AtLocation("Allow", () => {
                    if(resource.Allow is string inlineValue) {

                        // inline values can be separated by `,`
                        allowList.AddRange(inlineValue.Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries));
                    } else if(resource.Allow is IList<object> allowed) {
                        allowList = allowed.Cast<string>().ToList();
                    } else {
                        AddError("invalid allow value");
                        return;
                    }

                    // resolve shorthands and de-duplicated statements
                    var allowSet = new HashSet<string>();
                    foreach(var allowStatement in allowList) {
                        if(allowStatement == "None") {

                            // nothing to do
                        } else if(allowStatement.Contains(':')) {

                            // AWS permission statements always contain a `:` (e.g `ssm:GetParameter`)
                            allowSet.Add(allowStatement);
                        } else if(ResourceMapping.TryResolveAllowShorthand(resource.Type, allowStatement, out IList<string> allowedList)) {
                            foreach(var allowed in allowedList) {
                                allowSet.Add(allowed);
                            }
                        } else {
                            AddError($"could not find IAM mapping for short-hand '{allowStatement}' on AWS type '{resource.Type}'");
                        }
                    }
                    allowList = allowSet.OrderBy(text => text).ToList();
                });
            }

            // check if custom resource needs a service token to be imported
            AtLocation("Type", () => {
                if(!resource.Type.StartsWith("AWS::", StringComparison.Ordinal)) {
                    var customResourceName = resource.Type.StartsWith(CUSTOM_RESOURCE_PREFIX, StringComparison.Ordinal)
                        ? resource.Type.Substring(CUSTOM_RESOURCE_PREFIX.Length)
                        : resource.Type;
                    if(resource.Properties == null) {
                        resource.Properties = new Dictionary<string, object>();
                    }
                    if(!resource.Properties.ContainsKey("ServiceToken")) {
                        resource.Properties["ServiceToken"] = FnImportValue(FnSub($"${{Tier}}-CustomResource-{customResourceName}"));
                    }

                    // convert type name to a custom AWS resource type
                    resource.Type = "Custom::" + customResourceName;
                }
            });
            return new Resource {
                Type = resource.Type,
                ResourceReferences = resourceReferences,
                Allow = allowList,
                Properties = resource.Properties,
                DependsOn = resource.DependsOn ?? new List<string>()
            };
        }

        private Function ConvertFunction(int index, FunctionNode function) {
            return AtLocation(function.Name ?? $"[{index}]", () => {

                // append the version to the function description
                if(function.Description != null) {
                    function.Description = function.Description.TrimEnd() + $" (v{_module.Version})";
                }

                // initialize VPC configuration if provided
                FunctionVpc vpc = null;
                if(function.VPC?.Any() == true) {
                    if(
                        function.VPC.TryGetValue("SubnetIds", out var subnets)
                        && function.VPC.TryGetValue("SecurityGroupIds", out var securityGroups)
                    ) {
                        AtLocation("VPC", () => {
                            vpc = new FunctionVpc {
                                SubnetIds = subnets,
                                SecurityGroupIds = securityGroups
                            };
                        });
                    } else {
                        AddError("Lambda function contains a VPC definition that does not include SubnetIds or SecurityGroupIds");
                    }
                }

                // create function
                var eventIndex = 0;
                return new Function {
                    Name = function.Name,
                    Description = function.Description,
                    Sources = AtLocation("Sources", () => function.Sources?.Select(source => ConvertFunctionSource(function, ++eventIndex, source)).Where(evt => evt != null).ToList(), null) ?? new List<AFunctionSource>(),
                    PackagePath = function.PackagePath,
                    Handler = function.Handler,
                    Runtime = function.Runtime,
                    Memory = function.Memory,
                    Timeout = function.Timeout,
                    ReservedConcurrency = function.ReservedConcurrency,
                    VPC = vpc,
                    Environment = function.Environment ?? new Dictionary<string, object>(),
                    Pragmas = function.Pragmas
                };
            }, null);
        }

        private AFunctionSource ConvertFunctionSource(FunctionNode function, int index, FunctionSourceNode source) {
            return AtLocation<AFunctionSource>($"{index}", () => {
                if(source.Topic != null) {
                    return new TopicSource {
                        TopicName = source.Topic
                    };
                }
                if(source.Schedule != null) {
                    return AtLocation("Schedule", () => {
                        return new ScheduleSource {
                            Expression = source.Schedule,
                            Name = source.Name
                        };
                    }, null);
                }
                if(source.Api != null) {
                    return AtLocation("Api", () => {

                        // extract http method from route
                        var api = source.Api.Trim();
                        var pathSeparatorIndex = api.IndexOfAny(new[] { ':', ' ' });
                        if(pathSeparatorIndex < 0) {
                            AddError("invalid api format");
                            return new ApiGatewaySource {
                                Method = "ANY",
                                Path = new string[0],
                                Integration = ApiGatewaySourceIntegration.RequestResponse
                            };
                        }
                        var method = api.Substring(0, pathSeparatorIndex).ToUpperInvariant();
                        if(method == "*") {
                            method = "ANY";
                        }
                        var path = api.Substring(pathSeparatorIndex + 1).TrimStart().Split('/', StringSplitOptions.RemoveEmptyEntries);

                        // parse integration into a valid enum
                        var integration = AtLocation("Integration", () => Enum.Parse<ApiGatewaySourceIntegration>(source.Integration ?? "RequestResponse", ignoreCase: true), ApiGatewaySourceIntegration.Unsupported);
                        return new ApiGatewaySource {
                            Method = method,
                            Path = path,
                            Integration = integration,
                            OperationName = source.OperationName,
                            ApiKeyRequired = source.ApiKeyRequired
                        };
                    }, null);
                }
                if(source.SlackCommand != null) {
                    return AtLocation("SlackCommand", () => {

                        // parse integration into a valid enum
                        return new ApiGatewaySource {
                            Method = "POST",
                            Path = source.SlackCommand.Split('/', StringSplitOptions.RemoveEmptyEntries),
                            Integration = ApiGatewaySourceIntegration.SlackCommand,
                            OperationName = source.OperationName
                        };
                    }, null);
                }
                if(source.S3 != null) {
                    return new S3Source {
                        Bucket = source.S3,
                        Events = source.Events ?? new List<string> {

                            // default S3 events to listen to
                            "s3:ObjectCreated:*"
                        },
                        Prefix = source.Prefix,
                        Suffix = source.Suffix
                    };
                }
                if(source.Sqs != null) {
                    return new SqsSource {
                        Queue = source.Sqs,
                        BatchSize = source.BatchSize ?? 10
                    };
                }
                if(source.Alexa != null) {
                    var alexaSkillId = source.Alexa;
                    if((source.Alexa is string alexa) && (string.IsNullOrWhiteSpace(alexa) || (alexa == "*"))) {
                        alexaSkillId = null;
                    }
                    return new AlexaSource {
                        EventSourceToken = alexaSkillId
                    };
                }
                if(source.DynamoDB != null) {
                    return new DynamoDBSource {
                        DynamoDB = source.DynamoDB,
                        BatchSize = source.BatchSize ?? 100,
                        StartingPosition = source.StartingPosition ?? "LATEST"
                    };
                }
                if(source.Kinesis != null) {
                    return new KinesisSource {
                        Kinesis = source.Kinesis,
                        BatchSize = source.BatchSize ?? 100,
                        StartingPosition = source.StartingPosition ?? "LATEST"
                    };
                }
                if(source.Macro != null) {
                    var macroName = source.Macro;
                    if((macroName == "") || (macroName == "*")) {
                        macroName = function.Name;
                    }
                    return new MacroSource {
                        MacroName = macroName
                    };
                }
                return null;
            }, null);
        }

        private AOutput ConvertOutput(int index, OutputNode output) {
            return AtLocation<AOutput>(output.Output ?? $"[{index}]", () => {
                if(output.Output != null) {
                    return new StackOutput {
                        Name = output.Output,
                        Description = output.Description,
                        Value = output.Value
                    };
                }
                if(output.Export != null) {
                    var value = output.Value;
                    var description = output.Description;
                    if(value == null) {

                        // NOTE: if no value is provided, we expect the export name to correspond to a
                        //  parameter name; if it does, we export the !Ref value of that parameter; in
                        //  addition, we assume its description if none is provided.

                        value = FnRef(output.Export);
                        if(description == null) {
                            var parameter = _module.Parameters.First(p => p.Name == output.Export);
                            description = parameter.Description;
                        }
                    }
                    return new ExportOutput {
                        ExportName = output.Export,
                        Description = description,
                        Value = value
                    };
                }
                if(output.CustomResource != null) {
                    return new CustomResourceHandlerOutput {
                        CustomResourceName = output.CustomResource,
                        Description = output.Description,
                        Handler = output.Handler
                    };
                }
                throw new ModelParserException("invalid output type");
            }, null);
        }
    }
}