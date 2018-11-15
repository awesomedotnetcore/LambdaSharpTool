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
using MindTouch.LambdaSharp.Tool.Internal;
using MindTouch.LambdaSharp.Tool.Model;
using MindTouch.LambdaSharp.Tool.Model.AST;

namespace MindTouch.LambdaSharp.Tool {
    using Fn = Humidifier.Fn;
    using Condition = Humidifier.Condition;

    public class ModelConverter : AModelProcessor {

        //--- Constants ---
        private const string CUSTOM_RESOURCE_PREFIX = "Custom::";

        //--- Fields ---
        private Module _module;

        //--- Constructors ---
        public ModelConverter(Settings settings, string sourceFilename) : base(settings, sourceFilename) { }

        //--- Methods ---
        public Module Process(ModuleNode module) {

            // convert module definition
            try {
                return Convert(module);
            } catch(Exception e) {
                AddError(e);
                return null;
            }
        }

        private Module Convert(ModuleNode module) {

            // initialize module
            var secrets = new List<object>();
            var resources = new List<AResource>();
            var outputs = new List<AOutput>();
            _module = new Module {
                Name = module.Module,
                Version = VersionInfo.Parse(module.Version),
                Description = module.Description,
                Pragmas = module.Pragmas,
                Secrets = secrets,
                Resources = resources,
                Outputs = outputs,
                Conditions = new Dictionary<string, object>()
            };

            // convert collections
            AddToList("Secrets", secrets, module.Secrets, ConvertSecret);
            AddToList("Inputs", resources, module.Inputs, ConvertInput);
            AddToList("Outputs", outputs, module.Outputs, ConvertOutput);
            AddToList("Variables", resources, module.Variables, (index, parameter) => ConvertParameter(index, parameter));
            AddToList("Functions", resources, module.Functions, ConvertFunction);
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
                    var response = Settings.KmsClient.DescribeKeyAsync(secret).Result;
                    return response.KeyMetadata.Arn;
                } catch(Exception e) {
                    AddError($"failed to resolve key alias: {secret}", e);
                    return null;
                }
            }, null);
        }

        private AParameter ConvertInput(int index, InputNode input) {
            var type = DeterminNodeType("input", index, input, InputNode.FieldCheckers, InputNode.FieldCombinations, new[] { "Parameter", "Import" });
            switch(type) {
            case "Parameter":
                return AtLocation(input.Parameter, () => CreateParameter(input), null);
            case "Import":
                return AtLocation<AParameter>(input.Import, () => {
                    CreateImport(input);
                    return null;;
                }, null);
            }
            return null;
        }

        private List<string> ConvertScope(object scope) {
            return AtLocation("Scope", () => {
                return (scope == null)
                    ? new List<string>()
                    : ConvertToStringList(scope);
            }, new List<string>());
        }

        private AParameter ConvertParameter(
            int index,
            ParameterNode parameter,
            string resourcePrefix = ""
        ) {
            var resourceName = resourcePrefix + parameter.Var;
            var type = DeterminNodeType("variable", index, parameter, ParameterNode.FieldCheckers, ParameterNode.FieldCombinations, new[] {
                "Var.Resource",
                "Var.Reference",
                "Var.Value",
                "Var.Secret",
                "Var.Empty",
                "Package"
            });
            AParameter result = null;
            switch(type) {
            case "Var.Resource":

                // managed resource
                result = AtLocation(parameter.Var, () => {
                    var reference = (parameter.Resource.ArnAttribute != null)
                        ? FnGetAtt(resourceName, parameter.Resource.ArnAttribute)
                        : ResourceMapping.GetArnReference(parameter.Resource.Type, resourceName);
                    return new CloudFormationResourceParameter {
                        Scope = ConvertScope(parameter.Scope),
                        Name = parameter.Var,
                        ResourceName = resourceName,
                        Description = parameter.Description,
                        Resource = ConvertResource(new List<object>(), parameter.Resource),
                        Reference = reference
                    };
                }, null);
                break;
            case "Var.Reference":

                // existing resource
                result = AtLocation(parameter.Var, () => {
                    var resource = ConvertResource((parameter.Value as IList<object>) ?? new List<object> { parameter.Value }, parameter.Resource);
                    return new ReferencedResourceParameter {
                        Scope = ConvertScope(parameter.Scope),
                        Name = parameter.Var,
                        ResourceName = resourceName,
                        Description = parameter.Description,
                        Resource = resource,
                        Reference = FnJoin(",", resource.ResourceReferences)
                    };
                }, null);
                break;
            case "Var.Value":

                // plain value
                result = AtLocation(parameter.Var, () => new ValueParameter {
                    Scope = ConvertScope(parameter.Scope),
                    Name = parameter.Var,
                    ResourceName = resourceName,
                    Description = parameter.Description,
                    Reference = (parameter.Value is IList<object> values)
                        ? FnJoin(",", values)
                        : parameter.Value
                }, null);
                break;
            case "Var.Secret":

                // encrypted value
                result = AtLocation(parameter.Var, () => new SecretParameter {
                    Scope = ConvertScope(parameter.Scope),
                    Name = parameter.Var,
                    ResourceName = resourceName,
                    Description = parameter.Description,
                    Secret = parameter.Secret,
                    EncryptionContext = parameter.EncryptionContext,
                    Reference = parameter.Secret
                }, null);
                break;
            case "Var.Empty":

                // empty node
                result = AtLocation(parameter.Var, () => new ValueParameter {
                    Scope = ConvertScope(parameter.Scope),
                    Name = parameter.Var,
                    ResourceName = resourceName,
                    Description = parameter.Description,
                    Reference = ""
                }, null);
                break;
            case "Package":

                // package value
                result = AtLocation(parameter.Package, () => new PackageParameter {
                    Scope = ConvertScope(parameter.Scope),
                    Name = parameter.Package,
                    ResourceName = resourceName,
                    Description = parameter.Description,
                    DestinationBucketParameterName = parameter.Bucket,
                    DestinationKeyPrefix = parameter.Prefix ?? "",
                    SourceFilepath = parameter.Files,
                    Reference = FnGetAtt(resourceName, "Url")
                }, null);
                break;
            default:
                return null;
            }
            ConvertParameters();
            return result;

            // local functions
            void ConvertParameters() {
                if(result != null) {
                    AddToList("Variables", result, parameter.Variables, (i, p) => ConvertParameter(i, p, resourceName));
                }
            }
        }

        private Resource ConvertResource(IList<object> resourceReferences, ResourceNode resource) {

            // parse resource allowed operations
            var allowList = new List<string>();
            AtLocation("Resource", () => {
                if(resource.Allow != null) {
                    AtLocation("Allow", () => {
                        allowList.AddRange(ConvertToStringList(resource.Allow));

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
                            resource.Properties["ServiceToken"] = FnImportValue(FnSub($"${{DeploymentPrefix}}CustomResource-{customResourceName}"));
                        }

                        // convert type name to a custom AWS resource type
                        resource.Type = "Custom::" + customResourceName;
                    }
                });
            });
            return new Resource {
                Type = resource.Type,
                ResourceReferences = resourceReferences,
                Allow = allowList,
                Properties = resource.Properties,
                DependsOn = ConvertToStringList(resource.DependsOn)
            };
        }

        private Function ConvertFunction(int index, FunctionNode function) {
            return AtLocation(function.Function ?? $"[{index}]", () => {

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
                        AddError("Lambda function contains a VPC definition that does not include 'SubnetIds' or 'SecurityGroupIds' attributes");
                    }
                }

                // create function
                var eventIndex = 0;
                return new Function {
                    Name = function.Function,
                    Description = function.Description,
                    Memory = function.Memory,
                    Timeout = function.Timeout,
                    Project = function.Project,
                    Handler = function.Handler,
                    Runtime = function.Runtime,
                    Language = function.Language,
                    ReservedConcurrency = function.ReservedConcurrency,
                    VPC = vpc,

                    // TODO (2018-11-10, bjorg): don't put generator logic into the converter
                    Environment = function.Environment.ToDictionary(kv => "STR_" + kv.Key.Replace("::", "_").ToUpperInvariant(), kv => kv.Value) ?? new Dictionary<string, object>(),
                    Sources = AtLocation("Sources", () => function.Sources?.Select(source => ConvertFunctionSource(function, ++eventIndex, source)).Where(evt => evt != null).ToList(), null) ?? new List<AFunctionSource>(),
                    Pragmas = function.Pragmas
                };
            }, null);
        }

        private AFunctionSource ConvertFunctionSource(FunctionNode function, int index, FunctionSourceNode source) {
            var type = DeterminNodeType("source", index, source, FunctionSourceNode.FieldCheckers, FunctionSourceNode.FieldCombinations, new[] {
                "Api",
                "Schedule",
                "S3",
                "SlackCommand",
                "Topic",
                "Sqs",
                "Alexa",
                "DynamoDB",
                "Kinesis",
            });
            switch(type) {
            case "Api":
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
            case "Schedule":
                return AtLocation("Schedule", () => new ScheduleSource {
                    Expression = source.Schedule,
                    Name = source.Name
                }, null);
            case "S3":
                return AtLocation("S3", () => new S3Source {
                    Bucket = source.S3,
                    Events = source.Events ?? new List<string> {

                        // default S3 events to listen to
                        "s3:ObjectCreated:*"
                    },
                    Prefix = source.Prefix,
                    Suffix = source.Suffix
                }, null);
            case "SlackCommand":
                return AtLocation("SlackCommand", () => new ApiGatewaySource {
                    Method = "POST",
                    Path = source.SlackCommand.Split('/', StringSplitOptions.RemoveEmptyEntries),
                    Integration = ApiGatewaySourceIntegration.SlackCommand,
                    OperationName = source.OperationName
                }, null);
            case "Topic":
                return AtLocation("Topic", () => new TopicSource {
                    TopicName = source.Topic
                }, null);
            case "Sqs":
                return AtLocation("Sqs", () => new SqsSource {
                    Queue = source.Sqs,
                    BatchSize = source.BatchSize ?? 10
                }, null);
            case "Alexa":
                return AtLocation("Alexa", () => new AlexaSource {
                    EventSourceToken = source.Alexa
                }, null);
            case "DynamoDB":
                return AtLocation("DynamoDB", () => new DynamoDBSource {
                    DynamoDB = source.DynamoDB,
                    BatchSize = source.BatchSize ?? 100,
                    StartingPosition = source.StartingPosition ?? "LATEST"
                }, null);
            case "Kinesis":
                return AtLocation("Kinesis", () => new KinesisSource {
                    Kinesis = source.Kinesis,
                    BatchSize = source.BatchSize ?? 100,
                    StartingPosition = source.StartingPosition ?? "LATEST"
                }, null);
            }
            return null;
        }

        private AOutput ConvertOutput(int index, OutputNode output) {
            var type = DeterminNodeType("output", index, output, OutputNode.FieldCheckers, OutputNode.FieldCombinations, new[] { "Export", "CustomResource", "Macro" });
            switch(type) {
            case "Export":
                return AtLocation(output.Export, () => new ExportOutput {
                    Name = output.Export,
                    Description = output.Description,
                    Value = output.Value
                }, null);
            case "CustomResource":
                return AtLocation(output.CustomResource, () => new CustomResourceHandlerOutput {
                    CustomResourceName = output.CustomResource,
                    Description = output.Description,
                    Handler = output.Handler
                }, null);
            case "Macro":
                return AtLocation(output.Macro, () => new MacroOutput {
                    Macro = output.Macro,
                    Description = output.Description,
                    Handler = output.Handler
                }, null);
            }
            return null;
        }

        private AParameter CreateParameter(InputNode input) {

            // create regular input
            var result = new InputParameter {
                Name = input.Parameter,
                ResourceName = input.Parameter,
                Reference = FnRef(input.Parameter),
                Default = input.Default,
                ConstraintDescription = input.ConstraintDescription,
                AllowedPattern = input.AllowedPattern,
                AllowedValues = input.AllowedValues,
                MaxLength = input.MaxLength,
                MaxValue = input.MaxValue,
                MinLength = input.MinLength,
                MinValue = input.MinValue,

                // set AParameter fields
                Scope = ConvertScope(input.Scope),
                Description = input.Description,

                // set AInputParamete fields
                Type = input.Type ?? "String",
                Section = input.Section ?? "Module Settings",
                Label = input.Label ?? StringEx.PascalCaseToLabel(input.Parameter),
                NoEcho = input.NoEcho
            };

            // check if a resource definition is associated with the input statement
            if(input.Resource != null) {
                if(input.Default != null) {
                    result.Reference = FnIf(
                        $"{result.Name}Created",
                        ResourceMapping.GetArnReference(input.Resource.Type, $"{result.Name}CreatedInstance"),
                        FnRef(result.Name)
                    );
                }
                result.Resource = ConvertResource(new List<object> { result.Reference }, input.Resource);
            }
            return result;
        }

        private void CreateImport(InputNode input) {
            var result = _module.AddImportParameter(
                import: input.Import,
                type: input.Type,
                scope: ConvertScope(input.Scope),
                description: input.Description,
                section: input.Section,
                label: input.Label,
                noEcho: input.NoEcho
            );

            // check if a resource definition is associated with the import statement
            if(input.Resource != null) {
                result.Resource = ConvertResource(new List<object> { result.Reference }, input.Resource);
            }
        }

        private string DeterminNodeType<T>(
            string label,
            int index,
            T instance,
            Dictionary<string, Func<T, bool>> fieldChecker,
            Dictionary<string, IEnumerable<string>> fieldCombinations,
            IEnumerable<string> expectedTypes
        ) {
            return AtLocation($"[{index}]", () => {

                // find all declaration field with a non-null value; use alphabetical order for consistency
                var matches = fieldCombinations
                    .OrderBy(kv => kv.Key)
                    .Where(kv => {
                        if(!fieldChecker.TryGetValue(kv.Key, out Func<T, bool> checker)) {
                            throw new InvalidOperationException($"missing field checker for '{kv.Key}'");
                        }
                        return checker(instance);
                    })
                    .ToArray();
                switch(matches.Length) {
                case 0:
                    AddError($"unknown {label} type");
                    return null;
                case 1:

                    // good to go
                    break;
                default:
                    AddError($"ambiguous {label} type: {string.Join(", ", matches.Select(kv => kv.Key))}");
                    return null;
                }

                // validate match
                var match = matches.First();
                foreach(var checker in fieldChecker.Where(kv =>
                    (kv.Key != match.Key
                    && !match.Value.Contains(kv.Key))
                    && kv.Value(instance)
                )) {
                    AddError($"'{checker.Key}' cannot be used with '{match.Key}'");
                }
                if(!expectedTypes.Contains(match.Key)) {
                    AddError($"unexpected node type: {match.Key}");
                    return null;

                }
                return match.Key;
            }, null);
        }

        private void AddToList<TFrom>(string location, IResourceCollection resources, IEnumerable<TFrom> values, Func<int, TFrom, AResource> convert) {
            if(values?.Any() != true) {
                return;
            }
            AtLocation(location, () => {
                var index = 0;
                foreach(var value in values) {
                    var result = convert(++index, value);
                    if(result != null) {
                        resources.AddResource(result);
                    }
                }
            });
        }

        private void AddToList<TFrom, TTo>(string location, IList<TTo> resources, IEnumerable<TFrom> values, Func<int, TFrom, TTo> convert) {
            if(values?.Any() != true) {
                return;
            }
            AtLocation(location, () => {
                var index = 0;
                foreach(var value in values) {
                    var result = convert(++index, value);
                    if(result != null) {
                        resources.Add(result);
                    }
                }
            });
        }
    }
}