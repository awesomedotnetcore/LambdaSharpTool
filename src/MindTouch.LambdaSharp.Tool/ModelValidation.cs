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
using System.Text.RegularExpressions;
using MindTouch.LambdaSharp.Tool.Model.AST;

namespace MindTouch.LambdaSharp.Tool {

    public class ModelValidation : AModelProcessor {

        //--- Constants ---
        private const string SECRET_ALIAS_PATTERN = "[0-9a-zA-Z/_\\-]+";

        //--- Fields ---
        private ModuleNode _module;
        private HashSet<string> _names;

        //--- Constructors ---
        public ModelValidation(Settings settings, string sourceFilename) : base(settings, sourceFilename) { }

        //--- Methods ---
        public void Process(ModuleNode module) {
            Validate(module);
        }

        private void Validate(ModuleNode module) {
            _module = module;
            _names = new HashSet<string>();
            Validate(module.Module != null, "missing module name");

            // ensure collections are present
            module.Pragmas = module.Pragmas ?? new List<object>();
            module.Secrets = module.Secrets ?? new List<string>();
            module.Inputs = module.Inputs ?? new List<InputNode>();
            module.Variables = module.Variables ?? new List<ParameterNode>();
            module.Functions = module.Functions ?? new List<FunctionNode>();
            module.Outputs = module.Outputs ?? new List<OutputNode>();

            // ensure version is present
            if(module.Version == null) {
                module.Version = "1.0";
            } else if(!VersionInfo.TryParse(module.Version, out VersionInfo version)) {
                AddError("`Version` expected to have format: Major.Minor[.Build[.Revision]]");
                module.Version = "0.0";
            }

            // process data structures
            AtLocation("Secrets", () => ValidateSecrets(module.Secrets));
            AtLocation("Inputs", () => ValidateInputs(module.Inputs));
            AtLocation("Variables", () => ValidateParameters(module.Variables));
            AtLocation("Functions", () => ValidateFunctions(module.Functions));
            AtLocation("Outputs", () => ValidateOutputs(module.Outputs));
        }

        private void ValidateSecrets(IEnumerable<string> secrets) {
            var index = 0;
            foreach(var secret in secrets) {
                ++index;
                AtLocation($"[{index}]", () => {
                    if(string.IsNullOrEmpty(secret)) {
                        AddError($"secret has no value");
                    } else if(secret.Equals("aws/ssm", StringComparison.OrdinalIgnoreCase)) {
                        AddError($"cannot grant permission to decrypt with aws/ssm");
                    } else if(secret.StartsWith("arn:")) {
                        if(!Regex.IsMatch(secret, $"arn:aws:kms:{Settings.AwsRegion}:{Settings.AwsAccountId}:key/[a-fA-F0-9\\-]+")) {
                            AddError("secret key must be a valid ARN for the current region and account ID");
                        }
                    } else if(!Regex.IsMatch(secret, SECRET_ALIAS_PATTERN)) {
                        AddError("secret key must be a valid alias");
                    }
                });
            }
        }

        private void ValidateParameters(IEnumerable<ParameterNode> parameters, string prefix = "") {
            var index = 0;
            foreach(var parameter in parameters) {
                ++index;
                AtLocation(parameter.Var ?? parameter.Package ?? $"[{index}]", () => {
                    ValidateFields("variable", parameter, ParameterNode.FieldCheckers, ParameterNode.FieldCombinations);
                    ValidateScope(parameter.Scope);
                    if(parameter.Secret != null) {
                        ValidateResourceName(parameter.Var, prefix);
                    } else if(parameter.Value != null) {
                        ValidateResourceName(parameter.Var, prefix);
                    } else if(parameter.Package != null) {
                        ValidateResourceName(parameter.Package, prefix);

                        // check if required attributes are present
                        Validate(parameter.Files != null, "missing 'Files' attribute");
                        Validate(parameter.Bucket != null, "missing 'Bucket' attribute");
                        if(parameter.Bucket is string bucketParameter) {

                            // verify that target bucket is defined as parameter with correct type
                            ValidateSourceParameter(bucketParameter, "AWS::S3::Bucket", "Kinesis S3 bucket resource");
                        }

                        // check if package is nested
                        if(prefix != "") {
                            AddError("parameter package cannot be nested");
                        }
                    } else if(parameter.Resource != null) {
                        ValidateResourceName(parameter.Var, prefix);
                    } else if(parameter.Variables == null) {
                        AddError("unknown variable type");
                    }
                    if(parameter.Variables != null) {
                        AtLocation("Variables", () => {

                            // recursively validate nested parameters
                            ValidateParameters(parameter.Variables, prefix + "::" + parameter.Var);
                        });
                    }
                    if(parameter.Resource != null) {
                        AtLocation("Resource", () => ValidateResource(parameter, parameter.Resource));
                    }
                });
            }
        }

        private void ValidateResource(ParameterNode parameter, ResourceNode resource) {
            if(parameter.Value != null) {
                resource.Type = resource.Type ?? "AWS";
                if(parameter.Value is string text) {
                    ValidateARN(text);
                } else if(parameter.Value is IList<object> values) {
                    foreach(var value in values) {
                        ValidateARN(value);
                    }
                }
            } else if(resource.Type == null) {
                AddError("missing Type attribute");
            } else if(
                resource.Type.StartsWith("AWS::", StringComparison.Ordinal)
                && !ResourceMapping.IsResourceTypeSupported(resource.Type)
            ) {
                AddError($"unsupported resource type: {resource.Type}");
            } else if(!resource.Type.StartsWith("AWS::", StringComparison.Ordinal)) {
                Validate(resource.Allow == null, "'Allow' attribute is not valid for custom resources");
            }

            // validate dependencies
            if(resource.DependsOn == null) {
                resource.DependsOn = new List<string>();
            } else {
                AtLocation("DependsOn", () => {
                    var dependencies = ConvertToStringList(resource.DependsOn);
                    foreach(var dependency in dependencies) {
                        var dependentParameter = _module.Variables.FirstOrDefault(p => p.Var == dependency);
                        if(dependentParameter == null) {
                            AddError($"could not find dependency '{dependency}'");
                        } else if(dependentParameter.Resource == null) {
                            AddError($"cannot depend on literal parameter '{dependency}'");
                        } else if(parameter.Var == dependency) {
                            AddError($"dependency cannot be on itself '{dependency}'");
                        }
                    }
                });
            }

            // local functions
            void ValidateARN(object resourceArn) {
                if((resourceArn is string text) && !text.StartsWith("arn:") && (text != "*")) {
                    AddError($"resource name must be a valid ARN or wildcard: {resourceArn}");
                }
            }
        }

        private void ValidateFunctions(IEnumerable<FunctionNode> functions) {
            if(!functions.Any()) {
                return;
            }

            // validate functions
            var index = 0;
            foreach(var function in functions) {
                ++index;
                AtLocation(function.Function ?? $"[{index}]", () => {
                    ValidateResourceName(function.Function, "");
                    Validate(function.Memory != null, "missing Memory attribute");
                    Validate(int.TryParse(function.Memory, out _), "invalid Memory value");
                    Validate(function.Timeout != null, "missing Name attribute");
                    Validate(int.TryParse(function.Timeout, out _), "invalid Timeout value");
                    function.Sources = function.Sources ?? new List<FunctionSourceNode>();
                    function.Environment = function.Environment ?? new Dictionary<string, object>();
                    function.VPC = function.VPC ?? new Dictionary<string, object>();
                    ValidateFunctionSource(function.Sources);
                    if(function.Pragmas == null) {
                        function.Pragmas = new List<object>();
                    }
                });
            }
        }

        private void ValidateFunctionSource(IEnumerable<FunctionSourceNode> sources) {
            var index = 0;
            foreach(var source in sources) {
                ++index;
                AtLocation($"{index}", () => {
                    ValidateFields("source", source, FunctionSourceNode.FieldCheckers, FunctionSourceNode.FieldCombinations);
                    if(source.Api != null) {

                        // TODO (2018-11-10, bjorg): validate API expression
                    } else if(source.Schedule != null) {

                        // TODO (2018-06-27, bjorg): add cron/rate expression validation
                    } else if(source.S3 != null) {

                        // TODO (2018-06-27, bjorg): add events, prefix, suffix validation

                        // verify source exists
                        ValidateSourceParameter(source.S3, "AWS::S3::Bucket", "S3 bucket");
                    } else if(source.SlackCommand != null) {

                        // TODO (2018-11-10, bjorg): validate API expression
                    } else if(source.Topic != null) {

                        // verify source exists
                        ValidateSourceParameter(source.Topic, "AWS::SNS::Topic", "SNS topic");
                    } else if(source.Sqs != null) {

                        // validate settings
                        AtLocation("BatchSize", () => {
                            if((source.BatchSize < 1) || (source.BatchSize > 10)) {
                                AddError($"invalid BatchSize value: {source.BatchSize}");
                            }
                        });

                        // verify source exists
                        ValidateSourceParameter(source.Sqs, "AWS::SQS::Queue", "SQS queue");
                    } else if(source.Alexa != null) {

                        // TODO (2018-11-10, bjorg): validate Alexa Skill ID
                    } else if(source.DynamoDB != null) {

                        // validate settings
                        AtLocation("BatchSize", () => {
                            if((source.BatchSize < 1) || (source.BatchSize > 100)) {
                                AddError($"invalid BatchSize value: {source.BatchSize}");
                            }
                        });
                        AtLocation("StartingPosition", () => {
                            switch(source.StartingPosition) {
                            case "TRIM_HORIZON":
                            case "LATEST":
                            case null:
                                break;
                            default:
                                AddError($"invalid StartingPosition value: {source.StartingPosition}");
                                break;
                            }
                        });

                        // verify source exists
                        ValidateSourceParameter(source.DynamoDB, "AWS::DynamoDB::Table", "DynamoDB table");
                    } else if(source.Kinesis != null) {

                        // validate settings
                        AtLocation("BatchSize", () => {
                            if((source.BatchSize < 1) || (source.BatchSize > 100)) {
                                AddError($"invalid BatchSize value: {source.BatchSize}");
                            }
                        });
                        AtLocation("StartingPosition", () => {
                            switch(source.StartingPosition) {
                            case "TRIM_HORIZON":
                            case "LATEST":
                            case null:
                                break;
                            default:
                                AddError($"invalid StartingPosition value: {source.StartingPosition}");
                                break;
                            }
                        });

                        // verify source exists
                        ValidateSourceParameter(source.Kinesis, "AWS::Kinesis::Stream", "Kinesis stream");
                    } else {
                        AddError("unknown source type");
                    }
                });
            }
        }

        private void ValidateSourceParameter(string name, string awsType, string typeDescription) {
            var input = _module.Inputs.FirstOrDefault(i => i.Parameter == name);
            var import = _module.Inputs.FirstOrDefault(i => i.Import == name);
            var parameter = _module.Variables.FirstOrDefault(p => p.Var == name);
            if(input != null) {
                if(input.Resource?.Type != awsType) {
                    AddError($"function source must be an {typeDescription} resource: '{name}'");
                }
            } else if(import != null) {
                if(import.Resource?.Type != awsType) {
                    AddError($"function source must be an {typeDescription} resource: '{name}'");
                }
            } else if(parameter != null) {
                if(parameter.Resource?.Type != awsType) {
                    AddError($"function source must be an {typeDescription} resource: '{name}'");
                }
            } else {
                AddError($"could not find function source: '{name}'");
            }
        }

        private void ValidateInputs(IList<InputNode> inputs) {
            var index = 0;
            foreach(var input in inputs) {
                ++index;
                AtLocation(input.Parameter ?? $"[{index}]", () => {
                    ValidateFields("input", input, InputNode.FieldCheckers, InputNode.FieldCombinations);
                    if(input.Type == null) {
                        input.Type = "String";
                    }
                    ValidateScope(input.Scope);
                    if(input.Import != null) {
                        Validate(input.Import.Split("::").Length == 2, "incorrect format for `Import` attribute");
                        if(input.Resource != null) {
                            Validate(input.Type == "String", "input 'Type' must be string");
                            AtLocation("Resource", () => {
                                Validate(input.Resource.Type != null, "'Type' attribute is required");
                                Validate(input.Resource.Allow != null, "'Allow' attribute is required");
                                Validate(ConvertToStringList(input.Resource.DependsOn).Any() != true, "'DependsOn' cannot be used on an input");
                            });
                        }
                    } else {
                        ValidateResourceName(input.Parameter, "");
                        if(input.Resource != null) {
                            Validate(input.Type == "String", "input 'Type' must be string");
                            AtLocation("Resource", () => {
                                Validate(ConvertToStringList(input.Resource.DependsOn).Any() != true, "'DependsOn' cannot be used on an input");
                                if(input.Default == null) {
                                    Validate(input.Resource.Properties == null, "'Properties' section cannot be used with `Input` attribute unless the 'Default' is set to a blank string");
                                }
                            });
                        }
                    }
                });
            }
        }

        private void ValidateOutputs(IList<OutputNode> outputs) {
            var index = 0;
            foreach(var output in outputs) {
                ++index;
                AtLocation(output.Export ?? output.CustomResource ?? $"[{index}]", () => {
                    ValidateFields("output", output, OutputNode.FieldCheckers, OutputNode.FieldCombinations);
                    if(output.Export != null) {

                        // TODO (2018-09-20, bjorg): add name validation
                        if(
                            (output.Value == null)
                            && (_module.Variables.FirstOrDefault(p => p?.Var == output.Export) == null)
                            && (_module.Inputs.FirstOrDefault(i => i?.Parameter == output.Export) == null)
                        ) {
                            AddError("output must either have a Value attribute or match the name of an existing variable/parameter");
                        }
                    } else if(output.CustomResource != null) {

                        // TODO (2018-09-20, bjorg): add custom resource name validation

                        Validate(output.Handler != null, "missing Handler attribute");

                        // TODO (2018-09-20, bjorg): confirm that `Handler` is set to an SNS topic or lambda function
                    } else if(output.Macro != null) {

                        // TODO (2018-10-30, bjorg): confirm that `Handler` is set to a lambda function
                    } else {
                        AddError("unknown output type");
                    }
                });
            }
        }

        private void ValidateResourceName(string name, string prefix) {
            var fullname = prefix + name;
            if(name == null) {
                AddError("missing name");
            } else if(fullname == "Module") {
                AddError($"'{fullname}' is a reserved name");
            } else if(!_names.Add(fullname)) {
                AddError($"duplicate name '{fullname}'");
            } else {
                Validate(Regex.IsMatch(name, CLOUDFORMATION_ID_PATTERN), "name is not valid");
            }
        }

        private void ValidateScope(object scope) {
            AtLocation("Scope", () => {
                if(scope == null) {
                    return;
                }
                var names = new List<string>();
                if(scope is string text) {
                    names.AddRange(text.Split(",").Select(v => v.Trim()).Where(v => v.Length > 0));
                }
                if(scope is IList<object> list) {
                    foreach(var entry in list) {
                        if(entry is string value) {
                            names.AddRange(value.Split(",").Select(v => v.Trim()).Where(v => v.Length > 0));
                        } else {
                            AddError("invalid function name");
                        }
                    }
                }
                foreach(var name in names) {
                    ValidateFunctionName(name);
                }
            });

            // local function
            void ValidateFunctionName(string function) {
                if(function == "*") {
                    return;
                }
                if(!_module.Functions.Any(f => f.Function == function)) {
                    AddError($"could not find function named: {function}");
                }
            }
        }

        private void ValidateFields<T>(string type, T instance, Dictionary<string, Func<T, bool>> fieldChecker, Dictionary<string, IEnumerable<string>> fieldCombinations) {

            // find the first declaration field with a non-null value; use alphabetical order for consistency
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
                AddError($"unknown {type} type");
                return;
            case 1:

                // good to go
                break;
            default:
                AddError($"ambiguous {type} type: {string.Join(", ", matches.Select(kv => kv.Key))}");
                return;
            }
            var match = matches.First();
            foreach(var checker in fieldChecker.Where(kv =>
                (kv.Key != match.Key
                && !match.Value.Contains(kv.Key))
                && kv.Value(instance)
            )) {
                AddError($"'{checker.Key}' cannot be used with '{match.Key}'");
            }
        }
    }
}