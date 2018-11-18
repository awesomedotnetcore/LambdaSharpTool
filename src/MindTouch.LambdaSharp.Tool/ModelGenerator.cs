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

        //--- Fields ---
        private Module _module;
        private Stack _stack;

        //--- Constructors ---
        public ModelGenerator(Settings settings, string sourceFilename) : base(settings, sourceFilename) { }

        //--- Methods ---
        public string Generate(Module module) {
            _module = module;

            // stack header
            _stack = new Stack {
                AWSTemplateFormatVersion = "2010-09-09",
                Description = (_module.Description != null)
                    ? _module.Description.TrimEnd() + $" (v{_module.Version})"
                    : null
            };

            // add conditions
            foreach(var condition in _module.Conditions) {
                _stack.Add(condition.Key, new Condition(condition.Value));
            }
            _stack.Add($"ModuleIsNotNested", new Condition(Fn.Equals(Fn.Ref("DeploymentParent"), "")));

            // add parameters
            foreach(var parameter in _module.Resources) {
                AddResource(parameter);
            }

            // add outputs
            _stack.Add("ModuleName", new Humidifier.Output {
                Value = _module.Name
            });
            _stack.Add("ModuleVersion", new Humidifier.Output {
                Value = _module.Version.ToString()
            });
            foreach(var output in module.Outputs) {
                switch(output) {
                case ExportOutput exportOutput:
                    _stack.Add(exportOutput.Name, new Humidifier.Output {
                        Description = exportOutput.Description,
                        Value = exportOutput.Value
                    });
                    _stack.Add($"{exportOutput.Name}Export", new Humidifier.Output {
                        Description = exportOutput.Description,
                        Value = exportOutput.Value,
                        Export = new Dictionary<string, dynamic> {
                            ["Name"] = Fn.Sub($"${{AWS::StackName}}::{exportOutput.Name}")
                        },
                        Condition = "ModuleIsNotNested"
                    });
                    break;
                case CustomResourceHandlerOutput customResourceHandlerOutput:
                    _stack.Add($"{new string(customResourceHandlerOutput.CustomResourceName.Where(char.IsLetterOrDigit).ToArray())}Handler", new Humidifier.Output {
                        Description = customResourceHandlerOutput.Description,
                        Value = Fn.Ref(customResourceHandlerOutput.Handler),
                        Export = new Dictionary<string, dynamic> {
                            ["Name"] = Fn.Sub($"${{DeploymentPrefix}}CustomResource-{customResourceHandlerOutput.CustomResourceName}")
                        }
                    });
                    break;
                case MacroOutput macroOutput:
                    _stack.Add($"{macroOutput.Macro}Macro", new CustomResource("AWS::CloudFormation::Macro") {

                        // TODO (2018-10-30, bjorg): we may want to set 'LogGroupName' and 'LogRoleARN' as well
                        ["Name"] = Fn.Sub("${DeploymentPrefix}" + macroOutput.Macro),
                        ["Description"] = macroOutput.Description ?? "",
                        ["FunctionName"] = Fn.Ref(macroOutput.Handler)
                    });
                    break;
                default:
                    throw new InvalidOperationException($"cannot generate output for this type: {output?.GetType()}");
                }
            }

            // add interface for presenting inputs
            _stack.AddTemplateMetadata("AWS::CloudFormation::Interface", new Dictionary<string, object> {
                ["ParameterLabels"] = _module.GetAllResources().OfType<InputParameter>().ToDictionary(input => input.LogicalId, input => new Dictionary<string, object> {
                    ["default"] = input.Label
                }),
                ["ParameterGroups"] = _module.GetAllResources().OfType<InputParameter>()
                    .GroupBy(input => input.Section)
                    .Select(section => new Dictionary<string, object> {
                        ["Label"] = new Dictionary<string, string> {
                            ["default"] = section.Key
                        },
                        ["Parameters"] = section.Select(input => input.LogicalId).ToList()
                    }
                )
            });

            // add module manifest
            _stack.AddTemplateMetadata("LambdaSharp::Manifest", new Dictionary<string, object> {
                ["Version"] = "2018-10-22",
                ["ModuleName"] = _module.Name,
                ["ModuleVersion"] = _module.Version.ToString(),
                ["Pragmas"] = _module.Pragmas
            });

            // generate JSON template
            var template = new JsonStackSerializer().Serialize(_stack);
            return template;
        }

        private void AddGrants() {
            foreach(var grant in _module.Grants) {
                _module.ResourceStatements.Add(new Statement {
                    Sid = grant.Sid,
                    Effect = "Allow",
                    Resource = grant.References,
                    Action = grant.Allow
                });
            }
        }

        private void AddResource(AResource definition) {
            var fullEnvName = definition.FullName.Replace("::", "_").ToUpperInvariant();
            var logicalId = definition.LogicalId;
            switch(definition) {
            case SecretParameter secretParameter:
                AddEnvironmentParameter("Secret", GetReference());
                break;
            case ValueParameter _:
                AddEnvironmentParameter("String", GetReference());
                break;
            case PackageParameter packageParameter:
                AddEnvironmentParameter("String", GetReference());

                // this CloudFormation resource can only be created once the file packager has run
                _stack.Add(logicalId, new LambdaSharpResource("LambdaSharp::S3::Package") {
                    ["DestinationBucketName"] = Fn.Ref(packageParameter.DestinationBucketParameterName),
                    ["DestinationKeyPrefix"] = packageParameter.DestinationKeyPrefix,
                    ["SourceBucketName"] = Fn.Ref("DeploymentBucketName"),
                    ["SourcePackageKey"] = Fn.Sub($"Modules/{_module.Name}/Assets/{Path.GetFileName(packageParameter.PackagePath)}")
                });
                break;
            case ReferencedResourceParameter referenceResourceParameter:
                AddEnvironmentParameter("String", GetReference());
                break;
            case CloudFormationResourceParameter cloudFormationResourceParameter: {
                    var resource = cloudFormationResourceParameter.Resource;
                    Humidifier.Resource resourceTemplate;
                    if(resource.Type.StartsWith("Custom::")) {
                        resourceTemplate = new CustomResource(resource.Type, resource.Properties);
                    } else if(!ResourceMapping.TryParseResourceProperties(
                        resource.Type,
                        GetReference(),
                        resource.Properties,
                        out _,
                        out resourceTemplate
                    )) {
                        throw new NotImplementedException($"resource type is not supported: {resource.Type}");
                    }
                    _stack.Add(logicalId, resourceTemplate, condition: resource.Condition, dependsOn: resource.DependsOn.ToArray());
                    AddEnvironmentParameter("String", GetReference());
                }
                break;
            case HumidifierParameter humidifierParameter: {
                    var resourceTemplate = humidifierParameter.Resource;
                    _stack.Add(logicalId, resourceTemplate, condition: humidifierParameter.Condition, dependsOn: humidifierParameter.DependsOn.ToArray());
                    AddEnvironmentParameter("String", GetReference());
                }
                break;
            case InputParameter valueInputParameter: {
                    _stack.Add(logicalId, new Parameter {
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
                    AddEnvironmentParameter(valueInputParameter.Type, GetReference());
                }
                break;
            case Function function:
                AddFunction(_module, function);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(definition), definition, "unknown parameter type");
            }

            // local function
            void AddEnvironmentParameter(string type, object value) {
                foreach(var function in definition.Scope.Select(name => _module.Resources.OfType<Function>().First(f => f.Name == name))) {
                    if(type == "Secret") {
                        function.Environment["SEC_" + fullEnvName] = value;
                    } else {
                        function.Environment["STR_" + fullEnvName] = value;
                    }
                }
            }

            object GetReference() => _module.Variables[definition.FullName].Reference;
        }

        private void AddFunction(Module module, Function function) {

            // initialize function environment variables
            var environmentVariables = function.Environment.ToDictionary(kv => kv.Key, kv => (dynamic)kv.Value);
            environmentVariables["MODULE_NAME"] = module.Name;
            environmentVariables["MODULE_ID"] = FnRef("AWS::StackName");
            environmentVariables["MODULE_VERSION"] = module.Version.ToString();
            environmentVariables["LAMBDA_NAME"] = function.Name;
            environmentVariables["LAMBDA_RUNTIME"] = function.Runtime;
            environmentVariables["DEADLETTERQUEUE"] = module.Variables["LambdaSharp::DeadLetterQueueArn"].Reference;
            environmentVariables["DEFAULTSECRETKEY"] = module.Variables["LambdaSharp::DefaultSecretKeyArn"].Reference;

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
                Role = FnGetAtt("ModuleRole", "Arn"),
                Code = new Lambda.FunctionTypes.Code {
                    S3Bucket = FnRef("DeploymentBucketName"),
                    S3Key = FnSub($"Modules/{module.Name}/Assets/{Path.GetFileName(function.PackagePath)}")
                },
                DeadLetterConfig = new Lambda.FunctionTypes.DeadLetterConfig {
                    TargetArn = module.Variables["Module::DeadLetterQueueArn"].Reference
                },
                Environment = new Lambda.FunctionTypes.Environment {
                    Variables = environmentVariables
                },
                VpcConfig = vpcConfig
            });
        }
    }
}